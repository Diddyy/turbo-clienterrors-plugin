using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Turbo.Contracts.Plugins.Admin;
using TurboClientErrors.Database;
using TurboClientErrors.Ingest;

namespace TurboClientErrors;

/// <summary>
/// The Client errors pages of the admin panel: the issues, most recently seen first, filtered by
/// status, kind or text, each with its count, players, builds, browsers and a 14-day trend; one
/// issue with its days, its recent occurrences, resolving, ignoring and reopening it, and a field for
/// the tester report it belongs to; one occurrence with its raw stack, React component stack and
/// breadcrumbs. Seeing them needs <c>plugin.clienterrors.issues.view</c>; resolving, ignoring,
/// reopening and linking <c>plugin.clienterrors.issues.manage</c>; deleting
/// <c>plugin.clienterrors.issues.delete</c>. No one holds them until a hotel grants them.
/// </summary>
public sealed class ClientErrorsAdmin(
    IServiceScopeFactory scopes,
    ClientErrorIngest ingest,
    TimeProvider time
) : IPluginAdmin
{
    public const int PAGE_SIZE = 25;
    public const int TREND_DAYS = 14;
    public const int OCCURRENCES_PAGE_SIZE = 10;

    private const string SPARKS = "▁▂▃▄▅▆▇█";

    public void Configure(IPluginAdminBuilder admin)
    {
        var view = admin.Node("issues.view", "See client errors in the admin panel");
        var manage = admin.Node(
            "issues.manage",
            "Resolve, ignore and reopen client errors, and link them to a tester report"
        );
        var delete = admin.Node("issues.delete", "Delete client errors");

        admin.Map("GET", "stats", view, (_, ct) => StatsAsync(ct));
        admin.Map("GET", "issues", view, ListAsync);
        admin.Map("GET", "issues/{id}", view, (r, ct) => WithIssueAsync(r, ct, IssueDetailAsync));
        admin.Map("GET", "issues/{id}/days", view, (r, ct) => WithIssueAsync(r, ct, DaysAsync));
        admin.Map("GET", "issues/{id}/occurrences", view, OccurrencesAsync);
        admin.Map(
            "POST",
            "issues/{id}/resolve",
            manage,
            (r, ct) => SetStatusAsync(r, IssueEntity.RESOLVED, ct)
        );
        admin.Map(
            "POST",
            "issues/{id}/ignore",
            manage,
            (r, ct) => SetStatusAsync(r, IssueEntity.IGNORED, ct)
        );
        admin.Map(
            "POST",
            "issues/{id}/reopen",
            manage,
            (r, ct) => SetStatusAsync(r, IssueEntity.OPEN, ct)
        );
        admin.Map("POST", "issues/{id}/link", manage, LinkAsync);
        admin.Map("DELETE", "issues/{id}", delete, DeleteAsync);
        admin.Map(
            "GET",
            "occurrences/{id}",
            view,
            (r, ct) => WithOccurrenceAsync(r, ct, OccurrenceDetail)
        );
        admin.Map(
            "GET",
            "occurrences/{id}/stack",
            view,
            (r, ct) => WithOccurrenceAsync(r, ct, x => Lines(x.Stack))
        );
        admin.Map(
            "GET",
            "occurrences/{id}/components",
            view,
            (r, ct) => WithOccurrenceAsync(r, ct, x => Lines(x.ComponentStack))
        );
        admin.Map(
            "GET",
            "occurrences/{id}/breadcrumbs",
            view,
            (r, ct) => WithOccurrenceAsync(r, ct, Breadcrumbs)
        );
        admin.Map(
            "GET",
            "occurrences/{id}/file",
            view,
            (r, ct) =>
                WithOccurrenceAsync(
                    r,
                    ct,
                    x =>
                        PluginAdminResult.FileOf(
                            JsonSerializer.SerializeToUtf8Bytes(OccurrenceJson(x), JSON),
                            "application/json",
                            $"clienterrors-occurrence-{x.Id}.json"
                        )
                )
        );
        admin.Map(
            "GET",
            "occurrences/{id}/stack.txt",
            view,
            (r, ct) =>
                WithOccurrenceAsync(
                    r,
                    ct,
                    x =>
                        PluginAdminResult.FileOf(
                            Encoding.UTF8.GetBytes(StackText(x)),
                            "text/plain",
                            $"clienterrors-occurrence-{x.Id}.txt"
                        )
                )
        );

        admin.Page(
            new PluginAdminPage(
                "issues",
                "Client errors",
                view,
                [
                    new PluginAdminBlock("stats", Endpoint: "stats"),
                    new PluginAdminBlock(
                        "table",
                        Endpoint: "issues",
                        Columns:
                        [
                            new PluginAdminColumn("id", "#", "number"),
                            new PluginAdminColumn("message", "Error"),
                            new PluginAdminColumn("occurrences", "Count", "number"),
                            new PluginAdminColumn("players", "Players", "number"),
                            new PluginAdminColumn("trend", "Last 14 days"),
                            new PluginAdminColumn("builds", "Builds"),
                            new PluginAdminColumn("browsers", "Browsers"),
                            new PluginAdminColumn("lastSeen", "Last seen", "time"),
                            new PluginAdminColumn("firstSeen", "First seen", "time"),
                            new PluginAdminColumn("status", "Status", "badge"),
                        ],
                        Filters:
                        [
                            new PluginAdminField(
                                "status",
                                "Status",
                                "select",
                                Options:
                                [
                                    "",
                                    IssueEntity.OPEN,
                                    IssueEntity.RESOLVED,
                                    IssueEntity.IGNORED,
                                ]
                            ),
                            new PluginAdminField(
                                "kind",
                                "Kind",
                                "select",
                                Options: ["", .. ClientErrorReport.KINDS.Order()]
                            ),
                            new PluginAdminField("q", "Message, frame or build"),
                        ],
                        ItemPage: "issue"
                    ),
                    new PluginAdminBlock(
                        "text",
                        Title: "How issues work",
                        Text: "Reports with the same message, top stack frames and client build are one issue. The same error on a later build joins the issue it had, so an issue lists every build it was seen on.\n\n"
                            + "**Resolve** an issue once its fix is deployed: reported again on a build it had not been seen on, it opens again. **Ignore** one you will not fix: it is still counted, but stays out of the open list.\n\n"
                            + "Clients send what they caught (message, raw stack, the last actions in the client, build, browser and system), never chat text. Stacks are kept as the browser wrote them: resolve them against the build's source maps."
                    ),
                ],
                "The errors players' clients hit, grouped into issues.",
                Icon: "scroll"
            )
        );
        admin.Page(
            new PluginAdminPage(
                "issue",
                "Client error",
                view,
                [
                    new PluginAdminBlock(
                        "detail",
                        Endpoint: "issues/{id}",
                        Columns:
                        [
                            new PluginAdminColumn("message", "Error"),
                            new PluginAdminColumn("kind", "Caught as"),
                            new PluginAdminColumn("status", "Status", "badge"),
                            new PluginAdminColumn("occurrences", "Count", "number"),
                            new PluginAdminColumn("players", "Players", "number"),
                            new PluginAdminColumn("trend", "Last 14 days"),
                            new PluginAdminColumn("firstSeen", "First seen", "time"),
                            new PluginAdminColumn("lastSeen", "Last seen", "time"),
                            new PluginAdminColumn("builds", "Builds"),
                            new PluginAdminColumn("browsers", "Browsers"),
                            new PluginAdminColumn("platforms", "Systems"),
                            new PluginAdminColumn("topFrame", "Thrown at"),
                            new PluginAdminColumn("report", "Tester report"),
                            new PluginAdminColumn("note", "Note"),
                            new PluginAdminColumn("resolvedAt", "Resolved or ignored", "time"),
                            new PluginAdminColumn("reopenedAt", "Reopened", "time"),
                            new PluginAdminColumn("reopenedBuild", "Reopened on build"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "actions",
                        Actions:
                        [
                            new PluginAdminAction(
                                "Resolve",
                                "POST",
                                "issues/{id}/resolve",
                                Node: manage,
                                When: $"status={IssueEntity.OPEN}"
                            ),
                            new PluginAdminAction(
                                "Ignore",
                                "POST",
                                "issues/{id}/ignore",
                                Node: manage,
                                When: $"status={IssueEntity.OPEN}"
                            ),
                            new PluginAdminAction(
                                "Reopen",
                                "POST",
                                "issues/{id}/reopen",
                                Node: manage,
                                When: "closed=true"
                            ),
                            new PluginAdminAction(
                                "Delete",
                                "DELETE",
                                "issues/{id}",
                                "Delete this issue with its counts, trend and occurrences? A client reporting it again makes a new one.",
                                Danger: true,
                                Node: delete,
                                Then: "back"
                            ),
                        ]
                    ),
                    new PluginAdminBlock(
                        "form",
                        Title: "Tester report",
                        Fields:
                        [
                            new PluginAdminField(
                                "report",
                                "Report",
                                Hint: "A bug report number, a forum or Discord link: whatever finds the tester's report. Empty clears it."
                            ),
                            new PluginAdminField("note", "Note", "textarea"),
                        ],
                        Submit: new PluginAdminAction("Save", "POST", "issues/{id}/link")
                    ),
                    new PluginAdminBlock(
                        "table",
                        Title: "Recent occurrences",
                        Endpoint: "issues/{id}/occurrences",
                        Columns:
                        [
                            new PluginAdminColumn("id", "#", "number"),
                            new PluginAdminColumn("time", "When", "time"),
                            new PluginAdminColumn("build", "Build"),
                            new PluginAdminColumn("browser", "Browser"),
                            new PluginAdminColumn("platform", "System"),
                            new PluginAdminColumn("player", "Player", "number"),
                            new PluginAdminColumn("room", "Room", "number"),
                            new PluginAdminColumn("kind", "Caught as"),
                        ],
                        ItemPage: "occurrence"
                    ),
                    new PluginAdminBlock(
                        "table",
                        Title: "Last 14 days",
                        Endpoint: "issues/{id}/days",
                        Columns:
                        [
                            new PluginAdminColumn("day", "Day (UTC)"),
                            new PluginAdminColumn("occurrences", "Count", "number"),
                            new PluginAdminColumn("bar", ""),
                        ]
                    ),
                ],
                Nav: false
            )
        );
        admin.Page(
            new PluginAdminPage(
                "occurrence",
                "Occurrence",
                view,
                [
                    new PluginAdminBlock(
                        "detail",
                        Endpoint: "occurrences/{id}",
                        Columns:
                        [
                            new PluginAdminColumn("issue", "Issue"),
                            new PluginAdminColumn("time", "When", "time"),
                            new PluginAdminColumn("kind", "Caught as"),
                            new PluginAdminColumn("message", "Error"),
                            new PluginAdminColumn("source", "Thrown at"),
                            new PluginAdminColumn("build", "Build"),
                            new PluginAdminColumn("production", "production.version"),
                            new PluginAdminColumn("browser", "Browser"),
                            new PluginAdminColumn("platform", "System"),
                            new PluginAdminColumn("renderer", "Renderer"),
                            new PluginAdminColumn("viewport", "Viewport"),
                            new PluginAdminColumn("uptime", "Client running for"),
                            new PluginAdminColumn("player", "Player", "number"),
                            new PluginAdminColumn("room", "Room", "number"),
                            new PluginAdminColumn("page", "Page"),
                            new PluginAdminColumn("userAgent", "User agent"),
                        ],
                        Downloads:
                        [
                            new PluginAdminAction(
                                "Stack (text)",
                                "GET",
                                "occurrences/{id}/stack.txt"
                            ),
                            new PluginAdminAction(
                                "Occurrence (JSON)",
                                "GET",
                                "occurrences/{id}/file"
                            ),
                        ]
                    ),
                    new PluginAdminBlock(
                        "table",
                        Title: "Stack, as the browser wrote it",
                        Endpoint: "occurrences/{id}/stack",
                        Columns:
                        [
                            new PluginAdminColumn("id", "#", "number"),
                            new PluginAdminColumn("line", "Frame"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "table",
                        Title: "Breadcrumbs: the client's last actions",
                        Endpoint: "occurrences/{id}/breadcrumbs",
                        Columns:
                        [
                            new PluginAdminColumn("before", "Before the error"),
                            new PluginAdminColumn("category", "What"),
                            new PluginAdminColumn("action", "Action"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "table",
                        Title: "React component stack",
                        Endpoint: "occurrences/{id}/components",
                        Columns:
                        [
                            new PluginAdminColumn("id", "#", "number"),
                            new PluginAdminColumn("line", "Component"),
                        ]
                    ),
                ],
                Nav: false
            )
        );
    }

    private static readonly JsonSerializerOptions JSON = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private async Task<PluginAdminResult> StatsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClientErrorsDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var dayAgo = now.AddDays(-1);
        var byStatus = await db
            .Issues.GroupBy(x => x.Status)
            .Select(x => new { x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct)
            .ConfigureAwait(false);
        var fresh = await db
            .Issues.CountAsync(x => x.FirstSeen >= dayAgo, ct)
            .ConfigureAwait(false);
        var today = await db
            .Days.Where(x => x.Day == now.Date)
            .SumAsync(x => x.Occurrences, ct)
            .ConfigureAwait(false);
        var players = await db
            .Facets.Where(x => x.Facet == FacetEntity.PLAYER && x.LastSeen >= dayAgo)
            .Select(x => x.Value)
            .Distinct()
            .CountAsync(ct)
            .ConfigureAwait(false);
        var dropped = ingest.Counts.Where(x => x.Key is not IngestOutcome.Queued).Sum(x => x.Value);

        return PluginAdminResult.Ok(
            new object[]
            {
                new { label = "Open", value = byStatus.GetValueOrDefault(IssueEntity.OPEN) },
                new { label = "New in 24 h", value = fresh },
                new { label = "Reports today (UTC)", value = today },
                new { label = "Players in 24 h", value = players },
                new
                {
                    label = "Resolved",
                    value = byStatus.GetValueOrDefault(IssueEntity.RESOLVED),
                },
                new { label = "Ignored", value = byStatus.GetValueOrDefault(IssueEntity.IGNORED) },
                new { label = "Dropped since start", value = dropped },
                new { label = "Waiting to be written", value = ingest.Queued },
            }
        );
    }

    private async Task<PluginAdminResult> ListAsync(
        PluginAdminRequest request,
        CancellationToken ct
    )
    {
        var page = int.TryParse(request.Query.GetValueOrDefault("page"), out var p)
            ? Math.Max(0, p)
            : 0;
        var status = request.Query.GetValueOrDefault("status");
        var kind = request.Query.GetValueOrDefault("kind");
        var text = request.Query.GetValueOrDefault("q")?.Trim();

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClientErrorsDbContext>();
        var query = db.Issues.AsNoTracking();

        if (status is IssueEntity.OPEN or IssueEntity.RESOLVED or IssueEntity.IGNORED)
            query = query.Where(x => x.Status == status);

        if (!string.IsNullOrEmpty(kind))
            query = query.Where(x => x.Kind == kind);

        if (!string.IsNullOrEmpty(text))
            query = query.Where(x =>
                x.Message.Contains(text)
                || (x.TopFrame != null && x.TopFrame.Contains(text))
                || db.Facets.Any(f =>
                    f.IssueId == x.Id && f.Facet == FacetEntity.BUILD && f.Value == text
                )
            );

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var issues = await query
            .OrderByDescending(x => x.LastSeen)
            .ThenByDescending(x => x.Id)
            .Skip(page * PAGE_SIZE)
            .Take(PAGE_SIZE)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var ids = issues.Select(x => x.Id).ToList();
        var facets = await FacetsAsync(db, ids, [FacetEntity.BUILD, FacetEntity.BROWSER], ct)
            .ConfigureAwait(false);
        var trends = await TrendsAsync(db, ids, ct).ConfigureAwait(false);

        return PluginAdminResult.Ok(
            new
            {
                items = issues.Select(x => new
                {
                    id = x.Id,
                    message = x.Message.Length > 140 ? x.Message[..140] + "..." : x.Message,
                    occurrences = x.Occurrences,
                    players = x.Players,
                    trend = Sparkline(trends.GetValueOrDefault(x.Id) ?? new int[TREND_DAYS]),
                    builds = Summary(facets, x.Id, FacetEntity.BUILD, 2),
                    browsers = Summary(facets, x.Id, FacetEntity.BROWSER, 2),
                    lastSeen = x.LastSeen,
                    firstSeen = x.FirstSeen,
                    status = x.Status,
                    kind = x.Kind,
                }),
                total,
                pageSize = PAGE_SIZE,
            }
        );
    }

    private async Task<PluginAdminResult> IssueDetailAsync(
        ClientErrorsDbContext db,
        IssueEntity x,
        CancellationToken ct
    )
    {
        var facets = await FacetsAsync(
                db,
                [x.Id],
                [FacetEntity.BUILD, FacetEntity.BROWSER, FacetEntity.PLATFORM],
                ct
            )
            .ConfigureAwait(false);
        var trend =
            (await TrendsAsync(db, [x.Id], ct).ConfigureAwait(false)).GetValueOrDefault(x.Id)
            ?? new int[TREND_DAYS];

        return PluginAdminResult.Ok(
            new
            {
                id = x.Id,
                message = x.Message,
                kind = x.Kind,
                status = x.Status,
                closed = x.Status != IssueEntity.OPEN,
                occurrences = x.Occurrences,
                players = x.Players,
                trend = $"{Sparkline(trend)}  {trend.Sum()} in {TREND_DAYS} days",
                firstSeen = x.FirstSeen,
                lastSeen = x.LastSeen,
                builds = Summary(facets, x.Id, FacetEntity.BUILD, 5),
                browsers = Summary(facets, x.Id, FacetEntity.BROWSER, 5),
                platforms = Summary(facets, x.Id, FacetEntity.PLATFORM, 5),
                topFrame = x.TopFrame,
                report = x.Report,
                note = x.Note,
                resolvedAt = x.ResolvedAt,
                reopenedAt = x.ReopenedAt,
                reopenedBuild = x.ReopenedBuild,
            }
        );
    }

    private async Task<PluginAdminResult> DaysAsync(
        ClientErrorsDbContext db,
        IssueEntity issue,
        CancellationToken ct
    )
    {
        var counts =
            (await TrendsAsync(db, [issue.Id], ct).ConfigureAwait(false)).GetValueOrDefault(
                issue.Id
            ) ?? new int[TREND_DAYS];
        var max = Math.Max(1, counts.Max());
        var today = time.GetUtcNow().UtcDateTime.Date;
        var items = Enumerable
            .Range(0, TREND_DAYS)
            .Reverse()
            .Select(i => new
            {
                id = i,
                day = today
                    .AddDays(i - (TREND_DAYS - 1))
                    .ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture),
                occurrences = counts[i],
                bar = counts[i] == 0
                    ? ""
                    : new string('█', Math.Max(1, (int)Math.Round(counts[i] * 30.0 / max))),
            })
            .ToList();

        return PluginAdminResult.Ok(
            new
            {
                items,
                total = items.Count,
                pageSize = items.Count,
            }
        );
    }

    private async Task<PluginAdminResult> OccurrencesAsync(
        PluginAdminRequest request,
        CancellationToken ct
    )
    {
        if (!TryId(request, out var id))
            return PluginAdminResult.BadRequest("An issue is named by its number.");

        var page = int.TryParse(request.Query.GetValueOrDefault("page"), out var p)
            ? Math.Max(0, p)
            : 0;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClientErrorsDbContext>();
        var query = db.Occurrences.AsNoTracking().Where(x => x.IssueId == id);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(x => x.Id)
            .Skip(page * OCCURRENCES_PAGE_SIZE)
            .Take(OCCURRENCES_PAGE_SIZE)
            .Select(x => new
            {
                id = x.Id,
                time = x.OccurredAt,
                build = x.Build,
                browser = x.Browser,
                platform = x.Platform,
                player = x.PlayerId,
                room = x.RoomId,
                kind = x.Kind,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return PluginAdminResult.Ok(
            new
            {
                items,
                total,
                pageSize = OCCURRENCES_PAGE_SIZE,
            }
        );
    }

    private async Task<PluginAdminResult> SetStatusAsync(
        PluginAdminRequest request,
        string status,
        CancellationToken ct
    ) =>
        await WithIssueAsync(
                request,
                ct,
                async (db, x, token) =>
                {
                    if (status == IssueEntity.OPEN)
                    {
                        x.ResolvedAt = null;
                        x.ResolvedBy = null;
                    }
                    else
                    {
                        x.ResolvedAt = time.GetUtcNow().UtcDateTime;
                        x.ResolvedBy = request.PlayerId;
                        x.ReopenedAt = null;
                        x.ReopenedBuild = null;
                    }

                    x.Status = status;
                    await db.SaveChangesAsync(token).ConfigureAwait(false);

                    return await IssueDetailAsync(db, x, token).ConfigureAwait(false);
                }
            )
            .ConfigureAwait(false);

    private Task<PluginAdminResult> LinkAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithIssueAsync(
            request,
            ct,
            async (db, x, token) =>
            {
                if (request.Body is not { ValueKind: JsonValueKind.Object } body)
                    return PluginAdminResult.BadRequest("Send the report and the note as JSON.");

                var report = Field(body, "report");
                var note = Field(body, "note");

                if (report is { Length: > 512 })
                    return PluginAdminResult.BadRequest("The report is at most 512 characters.");

                if (note is { Length: > 1000 })
                    return PluginAdminResult.BadRequest("The note is at most 1000 characters.");

                // A field left out stays as it was: the panel sends only the fields staff changed.
                if (body.TryGetProperty("report", out _))
                    x.Report = string.IsNullOrEmpty(report) ? null : report;

                if (body.TryGetProperty("note", out _))
                    x.Note = string.IsNullOrEmpty(note) ? null : note;

                await db.SaveChangesAsync(token).ConfigureAwait(false);

                return await IssueDetailAsync(db, x, token).ConfigureAwait(false);
            }
        );

    private async Task<PluginAdminResult> DeleteAsync(
        PluginAdminRequest request,
        CancellationToken ct
    )
    {
        if (!TryId(request, out var id))
            return PluginAdminResult.BadRequest("An issue is named by its number.");

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClientErrorsDbContext>();

        if (!await db.Issues.AnyAsync(x => x.Id == id, ct).ConfigureAwait(false))
            return PluginAdminResult.NotFound($"There is no issue {id}.");

        await ClientErrorRetention.DeleteIssuesAsync(db, [id], ct).ConfigureAwait(false);

        return PluginAdminResult.NoContent();
    }

    private static PluginAdminResult OccurrenceDetail(OccurrenceEntity x) =>
        PluginAdminResult.Ok(
            new
            {
                id = x.Id,
                issue = $"#{x.IssueId}",
                issueId = x.IssueId,
                time = x.OccurredAt,
                kind = x.Kind,
                message = x.Message,
                source = x.Source,
                build = x.Build,
                production = x.Production,
                browser = x.Browser,
                platform = x.Platform,
                renderer = x.Renderer,
                viewport = x.Viewport,
                uptime = Duration(x.UptimeMs),
                player = x.PlayerId,
                room = x.RoomId,
                page = x.Page,
                userAgent = x.UserAgent,
            }
        );

    private static PluginAdminResult Breadcrumbs(OccurrenceEntity x)
    {
        var items = ClientErrorReport
            .ReadBreadcrumbs(x.Breadcrumbs)
            .Select(
                (b, i) =>
                    new
                    {
                        id = i + 1,
                        before = x.UptimeMs > 0 && b.T > 0 && b.T <= x.UptimeMs
                            ? $"-{((x.UptimeMs - b.T) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} s"
                            : "",
                        category = b.C,
                        action = b.M,
                    }
            )
            .Reverse()
            .ToList();

        return PluginAdminResult.Ok(
            new
            {
                items,
                total = items.Count,
                pageSize = Math.Max(1, items.Count),
            }
        );
    }

    private static PluginAdminResult Lines(string? text)
    {
        var items = (text ?? string.Empty)
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Select((line, i) => new { id = i + 1, line })
            .ToList();

        return PluginAdminResult.Ok(
            new
            {
                items,
                total = items.Count,
                pageSize = Math.Max(1, items.Count),
            }
        );
    }

    private static object OccurrenceJson(OccurrenceEntity x) =>
        new
        {
            x.Id,
            x.IssueId,
            x.OccurredAt,
            x.Kind,
            x.Message,
            x.Source,
            x.Stack,
            x.ComponentStack,
            Breadcrumbs = ClientErrorReport.ReadBreadcrumbs(x.Breadcrumbs),
            x.Build,
            x.Production,
            x.Browser,
            x.Platform,
            x.UserAgent,
            x.Renderer,
            x.Viewport,
            x.UptimeMs,
            x.Page,
            x.RoomId,
            x.PlayerId,
        };

    private static string StackText(OccurrenceEntity x)
    {
        var text = new StringBuilder()
            .AppendLine(x.Message)
            .AppendLine($"build {x.Build}, {x.Browser} on {x.Platform}, {x.OccurredAt:u}");

        if (!string.IsNullOrEmpty(x.Source))
            text.AppendLine($"thrown at {x.Source}");

        text.AppendLine().AppendLine(x.Stack ?? "(no stack)");

        if (!string.IsNullOrEmpty(x.ComponentStack))
            text.AppendLine().AppendLine("React component stack:").AppendLine(x.ComponentStack);

        return text.ToString();
    }

    /// <summary>The top values of each facet asked for, per issue, most reported first.</summary>
    private static async Task<List<FacetEntity>> FacetsAsync(
        ClientErrorsDbContext db,
        List<int> ids,
        string[] facets,
        CancellationToken ct
    ) =>
        await db
            .Facets.AsNoTracking()
            .Where(x => ids.Contains(x.IssueId) && facets.Contains(x.Facet))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    private static string? Summary(List<FacetEntity> facets, int issueId, string facet, int top)
    {
        var values = facets
            .Where(x => x.IssueId == issueId && x.Facet == facet)
            .OrderByDescending(x => x.Occurrences)
            .ThenByDescending(x => x.LastSeen)
            .ToList();

        if (values.Count == 0)
            return null;

        var shown = string.Join(", ", values.Take(top).Select(x => $"{x.Value} ({x.Occurrences})"));

        return values.Count > top ? $"{shown} +{values.Count - top} more" : shown;
    }

    /// <summary>Each issue's counts for the last <see cref="TREND_DAYS"/> UTC days, oldest first.</summary>
    private async Task<Dictionary<int, int[]>> TrendsAsync(
        ClientErrorsDbContext db,
        List<int> ids,
        CancellationToken ct
    )
    {
        var today = time.GetUtcNow().UtcDateTime.Date;
        var from = today.AddDays(-(TREND_DAYS - 1));
        var days = await db
            .Days.AsNoTracking()
            .Where(x => ids.Contains(x.IssueId) && x.Day >= from)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var trends = new Dictionary<int, int[]>();

        foreach (var day in days)
        {
            var index = (int)(day.Day.Date - from).TotalDays;

            if (index is < 0 or >= TREND_DAYS)
                continue;

            if (!trends.TryGetValue(day.IssueId, out var counts))
                trends[day.IssueId] = counts = new int[TREND_DAYS];

            counts[index] += day.Occurrences;
        }

        return trends;
    }

    /// <summary>One character a day, oldest first: a dot for none, a bar as tall as the day's share of the busiest.</summary>
    internal static string Sparkline(int[] counts)
    {
        var max = counts.Length == 0 ? 0 : counts.Max();

        return string.Concat(
            counts.Select(x =>
                x == 0
                    ? '·'
                    : SPARKS[
                        Math.Clamp(
                            (int)Math.Ceiling(x * (double)SPARKS.Length / max) - 1,
                            0,
                            SPARKS.Length - 1
                        )
                    ]
            )
        );
    }

    private static string Duration(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);

        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
            : span.TotalMinutes >= 1 ? $"{span.Minutes} min {span.Seconds} s"
            : $"{span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
    }

    private static string? Field(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private async Task<PluginAdminResult> WithIssueAsync(
        PluginAdminRequest request,
        CancellationToken ct,
        Func<ClientErrorsDbContext, IssueEntity, CancellationToken, Task<PluginAdminResult>> answer
    )
    {
        if (!TryId(request, out var id))
            return PluginAdminResult.BadRequest("An issue is named by its number.");

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClientErrorsDbContext>();
        var issue = await db.Issues.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);

        return issue is null
            ? PluginAdminResult.NotFound($"There is no issue {id}.")
            : await answer(db, issue, ct).ConfigureAwait(false);
    }

    private async Task<PluginAdminResult> WithOccurrenceAsync(
        PluginAdminRequest request,
        CancellationToken ct,
        Func<OccurrenceEntity, PluginAdminResult> answer
    )
    {
        if (!TryId(request, out var id))
            return PluginAdminResult.BadRequest("An occurrence is named by its number.");

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClientErrorsDbContext>();
        var occurrence = await db
            .Occurrences.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            .ConfigureAwait(false);

        return occurrence is null
            ? PluginAdminResult.NotFound(
                $"There is no occurrence {id}: occurrences are kept for a while, then only counted."
            )
            : answer(occurrence);
    }

    private static bool TryId(PluginAdminRequest request, out int id) =>
        int.TryParse(request.Route.GetValueOrDefault("id"), out id) && id > 0;
}
