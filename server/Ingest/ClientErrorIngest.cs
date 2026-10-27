using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace TurboClientErrors.Ingest;

/// <summary>What became of one report a client sent.</summary>
public enum IngestOutcome
{
    Queued,
    Disabled,
    Empty,
    TooLarge,
    RateLimited,
    SessionLimit,
    NotAccepted,
    QueueFull,
}

/// <summary>A report waiting for the database: who sent it, when, and its JSON as it came.</summary>
public sealed record PendingReport(int PlayerId, DateTime ReceivedAt, byte[] Json);

/// <summary>
/// Where a session's reports come in. Everything here is checked in memory, in this order: the
/// plugin is on, the report is not empty or larger than <see cref="ClientErrorsConfig.MaxPayloadBytes"/>,
/// the session is within <see cref="ClientErrorsConfig.ReportsPerMinute"/> and
/// <see cref="ClientErrorsConfig.MaxReportsPerSession"/>, the session accepted the extension (asked
/// once per session, the answer kept), and the queue has room. A report that passes is queued for
/// <c>ClientErrorsWorker</c>; one that does not is dropped and counted. The session never waits for
/// the database.
/// </summary>
public sealed class ClientErrorIngest(
    IOptionsMonitor<ClientErrorsConfig> options,
    TimeProvider time
)
{
    /// <summary>Sessions not heard from for this long are forgotten by <see cref="Evict"/>.</summary>
    public static readonly TimeSpan SESSION_IDLE = TimeSpan.FromHours(2);

    private sealed class Session
    {
        public double Tokens;
        public DateTimeOffset Refilled;
        public DateTimeOffset LastSeen;
        public int Taken;
        public bool Accepted;
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<IngestOutcome, long> _counts = new();
    private readonly Channel<PendingReport> _queue = Channel.CreateUnbounded<PendingReport>(
        new UnboundedChannelOptions { SingleReader = true }
    );
    private int _queued;

    private ClientErrorsConfig Config => options.CurrentValue;

    public ChannelReader<PendingReport> Reader => _queue.Reader;

    /// <summary>How many reports wait for the database now.</summary>
    public int Queued => Volatile.Read(ref _queued);

    /// <summary>How many reports came to each outcome since the plugin started.</summary>
    public IReadOnlyDictionary<IngestOutcome, long> Counts => _counts.ToDictionary();

    public int Sessions => _sessions.Count;

    /// <summary>
    /// Takes or drops one report from <paramref name="session"/>; <paramref name="acceptsCapability"/>
    /// says whether the session accepted the extension, and is asked once per session.
    /// </summary>
    public async ValueTask<IngestOutcome> AcceptAsync(
        string session,
        int playerId,
        byte[] report,
        Func<CancellationToken, ValueTask<bool>> acceptsCapability,
        CancellationToken ct
    )
    {
        var outcome = await DecideAsync(session, playerId, report, acceptsCapability, ct)
            .ConfigureAwait(false);

        _counts.AddOrUpdate(outcome, 1, (_, n) => n + 1);

        return outcome;
    }

    /// <summary>The worker took <paramref name="count"/> reports off the queue.</summary>
    public void Taken(int count) => Interlocked.Add(ref _queued, -count);

    /// <summary>Forgets sessions idle for <see cref="SESSION_IDLE"/>; how many.</summary>
    public int Evict()
    {
        var before = time.GetUtcNow() - SESSION_IDLE;
        var evicted = 0;

        foreach (var (key, session) in _sessions)
            if (session.LastSeen < before && _sessions.TryRemove(key, out _))
                evicted++;

        return evicted;
    }

    private async ValueTask<IngestOutcome> DecideAsync(
        string session,
        int playerId,
        byte[] report,
        Func<CancellationToken, ValueTask<bool>> acceptsCapability,
        CancellationToken ct
    )
    {
        var config = Config;

        if (!config.Enabled)
            return IngestOutcome.Disabled;

        if (report.Length == 0)
            return IngestOutcome.Empty;

        if (report.Length > config.MaxPayloadBytes)
            return IngestOutcome.TooLarge;

        var now = time.GetUtcNow();
        var state = _sessions.GetOrAdd(
            session,
            _ => new Session { Tokens = config.ReportsPerMinute, Refilled = now }
        );
        bool accepted;

        lock (state)
        {
            state.LastSeen = now;

            if (config.MaxReportsPerSession > 0 && state.Taken >= config.MaxReportsPerSession)
                return IngestOutcome.SessionLimit;

            if (config.ReportsPerMinute > 0)
            {
                state.Tokens = Math.Min(
                    config.ReportsPerMinute,
                    state.Tokens + (now - state.Refilled).TotalMinutes * config.ReportsPerMinute
                );
                state.Refilled = now;

                if (state.Tokens < 1)
                    return IngestOutcome.RateLimited;

                state.Tokens -= 1;
            }

            // Counted before the capability is asked: a client that never accepted it cannot make
            // the server ask more than the limits allow.
            state.Taken++;
            accepted = state.Accepted;
        }

        if (!accepted)
        {
            if (!await acceptsCapability(ct).ConfigureAwait(false))
                return IngestOutcome.NotAccepted;

            lock (state)
                state.Accepted = true;
        }

        if (Interlocked.Increment(ref _queued) > Math.Max(1, config.QueueCapacity))
        {
            Interlocked.Decrement(ref _queued);

            return IngestOutcome.QueueFull;
        }

        _queue.Writer.TryWrite(new PendingReport(playerId, now.UtcDateTime, report));

        return IngestOutcome.Queued;
    }
}
