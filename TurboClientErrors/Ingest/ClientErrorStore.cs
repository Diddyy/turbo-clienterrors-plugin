using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TurboClientErrors.Database;

namespace TurboClientErrors.Ingest;

/// <summary>An issue a batch made or reopened, with the report that did.</summary>
public sealed record NewIssue(IssueEntity Issue, ClientErrorReport Report);

/// <summary>What one batch did.</summary>
public sealed record StoreResult(
    int Stored,
    int Unreadable,
    IReadOnlyList<NewIssue> NewIssues,
    IReadOnlyList<NewIssue> Reopened
);

/// <summary>
/// Writes a batch of reports, in one transaction. Each report's fingerprint
/// (<see cref="Fingerprints.Of"/>) finds its issue; a fingerprint not seen before joins the issue
/// with the same signature (the same error on another build, most recently seen first), and only
/// when there is none is the report a new issue. A resolved issue reported on a build it had not
/// been seen on is open again; an ignored one stays ignored. Every report counts towards its issue,
/// its day and its facets (build, browser, platform, player); a few of each fingerprint's reports
/// per batch are kept whole as occurrences.
/// </summary>
public sealed class ClientErrorStore(
    IServiceScopeFactory scopes,
    IOptionsMonitor<ClientErrorsConfig> options,
    ILogger<ClientErrorStore> logger
)
{
    /// <summary>The most reports of one fingerprint kept whole from one batch; the rest are counted.</summary>
    public const int OCCURRENCES_PER_BATCH = 5;

    private sealed record Read(
        PendingReport Pending,
        ClientErrorReport Report,
        string Browser,
        string Platform
    );

    public async Task<StoreResult> StoreAsync(
        IReadOnlyList<PendingReport> batch,
        CancellationToken ct
    )
    {
        var config = options.CurrentValue;
        var frames = Math.Clamp(config.FingerprintFrames, 0, 50);
        var read = new List<(string Fingerprint, string Signature, Read Read)>();
        var unreadable = 0;

        foreach (var pending in batch)
        {
            if (!ClientErrorReport.TryRead(pending.Json, config.MaxStackLength, out var report))
            {
                unreadable++;
                continue;
            }

            read.Add(
                (
                    Fingerprints.Of(report, frames),
                    Fingerprints.Signature(report, frames),
                    new Read(
                        pending,
                        report,
                        UserAgents.Browser(report.UserAgent),
                        UserAgents.Platform(report.UserAgent, report.Platform)
                    )
                )
            );
        }

        if (read.Count == 0)
            return new StoreResult(0, unreadable, [], []);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClientErrorsDbContext>();
        await using var transaction = await db
            .Database.BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        var groups = read.GroupBy(x => x.Fingerprint).ToList();
        var keys = groups.Select(x => x.Key).ToList();
        var known = await db
            .Fingerprints.Where(x => keys.Contains(x.Fingerprint))
            .ToDictionaryAsync(x => x.Fingerprint, x => x.IssueId, ct)
            .ConfigureAwait(false);
        var signatures = groups
            .Where(x => !known.ContainsKey(x.Key))
            .Select(x => x.First().Signature)
            .Distinct()
            .ToList();
        var knownIds = known.Values.Distinct().ToList();
        var issues = await db
            .Issues.Where(x => knownIds.Contains(x.Id) || signatures.Contains(x.Signature))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var newIssues = new List<NewIssue>();
        var assigned =
            new List<(
                IssueEntity Issue,
                IGrouping<string, (string Fingerprint, string Signature, Read Read)> Group,
                bool NewFingerprint
            )>();

        // First every group's issue, the new ones saved for their ids.
        foreach (var group in groups)
        {
            var first = group.First();
            IssueEntity? issue = known.TryGetValue(group.Key, out var id)
                ? issues.FirstOrDefault(x => x.Id == id)
                : issues
                    .Where(x => x.Signature == first.Signature)
                    .OrderByDescending(x => x.LastSeen)
                    .FirstOrDefault();

            if (issue is null)
            {
                issue = new IssueEntity
                {
                    Signature = first.Signature,
                    Kind = first.Read.Report.Kind,
                    Message = ClientErrorReport.Cut(first.Read.Report.Message, 512),
                    TopFrame = Fingerprints
                        .Frames(first.Read.Report.Stack)
                        .FirstOrDefault()
                        ?.ToString()
                        is { } top
                        ? ClientErrorReport.Cut(top, 512)
                        : first.Read.Report.Source,
                    Status = IssueEntity.OPEN,
                    FirstSeen = group.Min(x => x.Read.Pending.ReceivedAt),
                    LastSeen = group.Min(x => x.Read.Pending.ReceivedAt),
                };

                db.Issues.Add(issue);
                issues.Add(issue);
                newIssues.Add(new NewIssue(issue, first.Read.Report));
            }

            assigned.Add((issue, group, !known.ContainsKey(group.Key)));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var issueIds = assigned.Select(x => x.Issue.Id).Distinct().ToList();
        var values = read.SelectMany(x => FacetsOf(x.Read))
            .Select(x => x.Value)
            .Distinct()
            .ToList();
        var facets = await db
            .Facets.Where(x => issueIds.Contains(x.IssueId) && values.Contains(x.Value))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var dayKeys = read.Select(x => x.Read.Pending.ReceivedAt.Date).Distinct().ToList();
        var days = await db
            .Days.Where(x => issueIds.Contains(x.IssueId) && dayKeys.Contains(x.Day))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var reopened = new List<NewIssue>();

        foreach (var (issue, group, newFingerprint) in assigned)
        {
            var build = group.First().Read.Report.Build;

            if (newFingerprint)
            {
                // A build the issue had not been seen on: a fix that did not hold.
                if (
                    issue.Status == IssueEntity.RESOLVED
                    && !facets.Any(x =>
                        x.IssueId == issue.Id && x.Facet == FacetEntity.BUILD && x.Value == build
                    )
                )
                {
                    issue.Status = IssueEntity.OPEN;
                    issue.ReopenedAt = group.Min(x => x.Read.Pending.ReceivedAt);
                    issue.ReopenedBuild = build;
                    reopened.Add(new NewIssue(issue, group.First().Read.Report));
                }

                db.Fingerprints.Add(
                    new FingerprintEntity
                    {
                        Fingerprint = group.Key,
                        IssueId = issue.Id,
                        Build = build,
                    }
                );
            }

            foreach (var (_, _, report) in group)
            {
                var at = report.Pending.ReceivedAt;

                issue.Occurrences++;

                if (at > issue.LastSeen)
                    issue.LastSeen = at;

                issue.LastBuild = report.Report.Build;

                foreach (var (facet, value) in FacetsOf(report))
                {
                    var row = facets.FirstOrDefault(x =>
                        x.IssueId == issue.Id && x.Facet == facet && x.Value == value
                    );

                    if (row is null)
                    {
                        row = new FacetEntity
                        {
                            IssueId = issue.Id,
                            Facet = facet,
                            Value = value,
                            FirstSeen = at,
                        };
                        db.Facets.Add(row);
                        facets.Add(row);

                        if (facet == FacetEntity.PLAYER)
                            issue.Players++;
                    }

                    row.Occurrences++;

                    if (at > row.LastSeen)
                        row.LastSeen = at;
                }

                var day = days.FirstOrDefault(x => x.IssueId == issue.Id && x.Day == at.Date);

                if (day is null)
                {
                    day = new IssueDayEntity { IssueId = issue.Id, Day = at.Date };
                    db.Days.Add(day);
                    days.Add(day);
                }

                day.Occurrences++;
            }

            foreach (var (_, _, report) in group.TakeLast(OCCURRENCES_PER_BATCH))
                db.Occurrences.Add(Occurrence(issue.Id, report));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        if (unreadable > 0)
            logger.LogDebug("{Count} client error reports were not readable", unreadable);

        return new StoreResult(read.Count, unreadable, newIssues, reopened);
    }

    private static IEnumerable<(string Facet, string Value)> FacetsOf(Read read)
    {
        yield return (FacetEntity.BUILD, read.Report.Build);
        yield return (FacetEntity.BROWSER, read.Browser);
        yield return (FacetEntity.PLATFORM, read.Platform);

        if (read.Pending.PlayerId > 0)
            yield return (
                FacetEntity.PLAYER,
                read.Pending.PlayerId.ToString(CultureInfo.InvariantCulture)
            );
    }

    private static OccurrenceEntity Occurrence(int issueId, Read read) =>
        new()
        {
            IssueId = issueId,
            OccurredAt = read.Pending.ReceivedAt,
            PlayerId = read.Pending.PlayerId,
            Kind = read.Report.Kind,
            Message = read.Report.Message,
            Stack = read.Report.Stack,
            ComponentStack = read.Report.ComponentStack,
            Source = read.Report.Source,
            Breadcrumbs = read.Report.BreadcrumbsJson(),
            Build = read.Report.Build,
            Production = read.Report.Production,
            Browser = read.Browser,
            Platform = read.Platform,
            UserAgent = read.Report.UserAgent,
            Page = read.Report.Page,
            RoomId = read.Report.RoomId,
            Viewport = read.Report.Viewport,
            Renderer = read.Report.Renderer,
            UptimeMs = read.Report.UptimeMs,
        };
}
