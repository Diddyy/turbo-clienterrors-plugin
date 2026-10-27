using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TurboClientErrors.Ingest;

/// <summary>
/// The plugin's one background loop: takes queued reports in batches of up to
/// <see cref="BATCH_SIZE"/> (or what came within <see cref="BATCH_WAIT"/>) into the tables, tells
/// other plugins of new and reopened issues (<see cref="Notices"/>), posts new issues to its own
/// Discord webhook without waiting for it, and runs retention a minute after start and then
/// hourly. A batch that fails is logged and dropped: the next one is not held up by it.
/// </summary>
public sealed class ClientErrorsWorker(
    ClientErrorIngest ingest,
    ClientErrorStore store,
    ClientErrorRetention retention,
    DiscordWebhook discord,
    Notices notices,
    TimeProvider time,
    ILogger<ClientErrorsWorker> logger
)
{
    public const int BATCH_SIZE = 200;

    public static readonly TimeSpan BATCH_WAIT = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RETENTION_EVERY = TimeSpan.FromHours(1);
    public static readonly TimeSpan RETENTION_FIRST = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DRAIN_ON_STOP = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _stop;
    private Task? _loop;

    public void Start()
    {
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Stops the loop, and writes what is still queued (for a few seconds at most).</summary>
    public async Task StopAsync()
    {
        if (_stop is null || _loop is null)
            return;

        await _stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        using var drain = new CancellationTokenSource(DRAIN_ON_STOP);

        await WriteAsync(Take(int.MaxValue), drain.Token).ConfigureAwait(false);

        _stop.Dispose();
        _stop = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var nextRetention = time.GetUtcNow() + RETENTION_FIRST;

        while (!ct.IsCancellationRequested)
        {
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(BATCH_WAIT);

                try
                {
                    await ingest.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }

            if (ct.IsCancellationRequested)
                break;

            await WriteAsync(Take(BATCH_SIZE), ct).ConfigureAwait(false);

            if (time.GetUtcNow() < nextRetention)
                continue;

            nextRetention = time.GetUtcNow() + RETENTION_EVERY;

            try
            {
                var removed = await retention.RunAsync(ct).ConfigureAwait(false);
                var sessions = ingest.Evict();

                if (removed.Occurrences + removed.Issues + removed.Days > 0)
                    logger.LogInformation(
                        "Client error retention removed {Occurrences} occurrences, {Issues} issues and {Days} trend days; forgot {Sessions} idle sessions",
                        removed.Occurrences,
                        removed.Issues,
                        removed.Days,
                        sessions
                    );
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Client error retention failed");
            }
        }
    }

    private List<PendingReport> Take(int max)
    {
        var batch = new List<PendingReport>();

        while (batch.Count < max && ingest.Reader.TryRead(out var report))
            batch.Add(report);

        ingest.Taken(batch.Count);

        return batch;
    }

    private async Task WriteAsync(List<PendingReport> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
            return;

        try
        {
            var result = await store.StoreAsync(batch, ct).ConfigureAwait(false);

            foreach (var issue in result.NewIssues)
                _ = discord.PostNewAsync(issue);

            // After the commit; Notices logs what it cannot publish, so the batch stands.
            foreach (var issue in result.NewIssues)
                await notices.NewAsync(issue, ct).ConfigureAwait(false);

            foreach (var issue in result.Reopened)
                await notices.ReopenedAsync(issue, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "{Count} client error reports could not be stored", batch.Count);
        }
    }
}
