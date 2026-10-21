using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TurboClientErrors.Database;

namespace TurboClientErrors.Ingest;

/// <summary>What one retention run removed.</summary>
public sealed record RetentionResult(int Occurrences, int Days, int Issues);

/// <summary>
/// Keeps the tables bounded (run hourly by <c>ClientErrorsWorker</c>): occurrences older than
/// <see cref="ClientErrorsConfig.OccurrenceRetentionDays"/> or beyond an issue's latest
/// <see cref="ClientErrorsConfig.MaxOccurrencesPerIssue"/>, then issues no client reported for
/// <see cref="ClientErrorsConfig.IssueRetentionDays"/> with everything of theirs, and trend days
/// older than that (a year when issues are kept for ever).
/// </summary>
public sealed class ClientErrorRetention(
    IServiceScopeFactory scopes,
    IOptionsMonitor<ClientErrorsConfig> options,
    TimeProvider time
)
{
    private const int CHUNK = 500;

    public async Task<RetentionResult> RunAsync(CancellationToken ct)
    {
        var config = options.CurrentValue;
        var now = time.GetUtcNow().UtcDateTime;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClientErrorsDbContext>();
        var occurrences = 0;

        if (config.OccurrenceRetentionDays > 0)
        {
            var before = now.AddDays(-config.OccurrenceRetentionDays);

            occurrences += await db
                .Occurrences.Where(x => x.OccurredAt < before)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        if (config.MaxOccurrencesPerIssue > 0)
        {
            var max = config.MaxOccurrencesPerIssue;
            var over = await db
                .Occurrences.GroupBy(x => x.IssueId)
                .Where(x => x.Count() > max)
                .Select(x => x.Key)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var issueId in over)
            {
                var oldestKept = await db
                    .Occurrences.Where(x => x.IssueId == issueId)
                    .OrderByDescending(x => x.Id)
                    .Skip(max - 1)
                    .Select(x => x.Id)
                    .FirstAsync(ct)
                    .ConfigureAwait(false);

                occurrences += await db
                    .Occurrences.Where(x => x.IssueId == issueId && x.Id < oldestKept)
                    .ExecuteDeleteAsync(ct)
                    .ConfigureAwait(false);
            }
        }

        var issues = 0;

        if (config.IssueRetentionDays > 0)
        {
            var before = now.AddDays(-config.IssueRetentionDays);

            while (true)
            {
                var stale = await db
                    .Issues.Where(x => x.LastSeen < before)
                    .OrderBy(x => x.Id)
                    .Select(x => x.Id)
                    .Take(CHUNK)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                if (stale.Count == 0)
                    break;

                occurrences += await DeleteIssuesAsync(db, stale, ct).ConfigureAwait(false);
                issues += stale.Count;
            }
        }

        var oldestDay = now.Date.AddDays(
            -(config.IssueRetentionDays > 0 ? config.IssueRetentionDays : 365)
        );
        var days = await db
            .Days.Where(x => x.Day < oldestDay)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        return new RetentionResult(occurrences, days, issues);
    }

    /// <summary>Deletes issues with their fingerprints, facets, days and occurrences; how many occurrences went.</summary>
    internal static async Task<int> DeleteIssuesAsync(
        ClientErrorsDbContext db,
        IReadOnlyCollection<int> ids,
        CancellationToken ct
    )
    {
        var occurrences = await db
            .Occurrences.Where(x => ids.Contains(x.IssueId))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        await db
            .Facets.Where(x => ids.Contains(x.IssueId))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        await db
            .Days.Where(x => ids.Contains(x.IssueId))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        await db
            .Fingerprints.Where(x => ids.Contains(x.IssueId))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        await db.Issues.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return occurrences;
    }
}
