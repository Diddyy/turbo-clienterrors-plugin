using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Turbo.Primitives.Packets;
using Turbo.Primitives.Plugins.Events;
using TurboClientErrors.Database;
using TurboClientErrors.Ingest;
using TurboClientErrors.Packets;
using Xunit;

namespace TurboClientErrors.Tests;

public class ClientErrorsTests
{
    private static readonly CancellationToken CT = TestHost.CT;

    private static ValueTask<bool> Accepts(CancellationToken _) => ValueTask.FromResult(true);

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    // Ingestion: the session path.

    [Fact]
    public async Task A_session_is_held_to_its_rate_and_its_total_and_others_are_not()
    {
        using var host = new TestHost(
            new ClientErrorsConfig { ReportsPerMinute = 3, MaxReportsPerSession = 5 }
        );
        var report = Bytes(TestHost.Report());

        for (var i = 0; i < 3; i++)
            Assert.Equal(
                IngestOutcome.Queued,
                await host.Ingest.AcceptAsync("s1", 1, report, Accepts, CT)
            );

        Assert.Equal(
            IngestOutcome.RateLimited,
            await host.Ingest.AcceptAsync("s1", 1, report, Accepts, CT)
        );
        Assert.Equal(
            IngestOutcome.Queued,
            await host.Ingest.AcceptAsync("s2", 2, report, Accepts, CT)
        );

        // A minute refills the bucket, but the session's total stays.
        host.Time.Now = host.Time.Now.AddMinutes(1);

        Assert.Equal(
            IngestOutcome.Queued,
            await host.Ingest.AcceptAsync("s1", 1, report, Accepts, CT)
        );
        Assert.Equal(
            IngestOutcome.Queued,
            await host.Ingest.AcceptAsync("s1", 1, report, Accepts, CT)
        );
        Assert.Equal(
            IngestOutcome.SessionLimit,
            await host.Ingest.AcceptAsync("s1", 1, report, Accepts, CT)
        );
        Assert.Equal(6, host.Ingest.Queued);
        Assert.Equal(1, host.Ingest.Counts[IngestOutcome.RateLimited]);
        Assert.Equal(1, host.Ingest.Counts[IngestOutcome.SessionLimit]);
    }

    [Fact]
    public async Task An_empty_oversized_or_unaccepted_report_is_dropped_and_the_capability_is_asked_once()
    {
        using var host = new TestHost(new ClientErrorsConfig { MaxPayloadBytes = 64 });
        var asked = 0;
        ValueTask<bool> Counting(CancellationToken _)
        {
            asked++;

            return ValueTask.FromResult(true);
        }

        Assert.Equal(IngestOutcome.Empty, await host.Ingest.AcceptAsync("s", 1, [], Accepts, CT));
        Assert.Equal(
            IngestOutcome.TooLarge,
            await host.Ingest.AcceptAsync("s", 1, new byte[65], Accepts, CT)
        );
        Assert.Equal(
            IngestOutcome.NotAccepted,
            await host.Ingest.AcceptAsync(
                "other",
                2,
                new byte[10],
                _ => ValueTask.FromResult(false),
                CT
            )
        );
        Assert.Equal(
            IngestOutcome.Queued,
            await host.Ingest.AcceptAsync("s", 1, new byte[10], Counting, CT)
        );
        Assert.Equal(
            IngestOutcome.Queued,
            await host.Ingest.AcceptAsync("s", 1, new byte[10], Counting, CT)
        );
        Assert.Equal(1, asked);
        Assert.Equal(2, host.Ingest.Queued);
    }

    [Fact]
    public async Task A_full_queue_drops_at_once_instead_of_waiting_and_turning_it_off_drops_everything()
    {
        using var host = new TestHost(
            new ClientErrorsConfig { QueueCapacity = 2, ReportsPerMinute = 0 }
        );
        var report = Bytes(TestHost.Report());

        Assert.Equal(
            IngestOutcome.Queued,
            await host.Ingest.AcceptAsync("s", 1, report, Accepts, CT)
        );
        Assert.Equal(
            IngestOutcome.Queued,
            await host.Ingest.AcceptAsync("s", 1, report, Accepts, CT)
        );

        var full = host.Ingest.AcceptAsync("s", 1, report, Accepts, CT);

        Assert.True(full.IsCompleted);
        Assert.Equal(IngestOutcome.QueueFull, await full);

        // The worker taking them makes room again.
        Assert.True(host.Ingest.Reader.TryRead(out _));
        host.Ingest.Taken(1);
        Assert.Equal(
            IngestOutcome.Queued,
            await host.Ingest.AcceptAsync("s", 1, report, Accepts, CT)
        );

        host.Settings.Value = new ClientErrorsConfig { Enabled = false };
        Assert.Equal(
            IngestOutcome.Disabled,
            await host.Ingest.AcceptAsync("s", 1, report, Accepts, CT)
        );
    }

    [Fact]
    public async Task Idle_sessions_are_forgotten()
    {
        using var host = new TestHost();

        await host.Ingest.AcceptAsync("s", 1, Bytes(TestHost.Report()), Accepts, CT);
        Assert.Equal(0, host.Ingest.Evict());

        host.Time.Now += ClientErrorIngest.SESSION_IDLE + TimeSpan.FromMinutes(1);

        Assert.Equal(1, host.Ingest.Evict());
        Assert.Equal(0, host.Ingest.Sessions);
    }

    [Fact]
    public void The_report_packet_reads_as_the_client_writes_it_and_never_past_its_end()
    {
        var json = Bytes("""{"message":"x"}""");
        var body = new byte[4 + json.Length];

        BinaryPrimitives.WriteInt32BigEndian(body, json.Length);
        json.CopyTo(body, 4);

        var message = (ClientErrorReportMessage)
            new ClientErrorReportParser().Parse(new ClientPacket(ClientErrorsPackets.REPORT, body));

        Assert.Equal(json, message.Report);

        BinaryPrimitives.WriteInt32BigEndian(body, int.MaxValue);
        Assert.Empty(
            (
                (ClientErrorReportMessage)
                    new ClientErrorReportParser().Parse(
                        new ClientPacket(ClientErrorsPackets.REPORT, body)
                    )
            ).Report
        );
        Assert.Empty(
            (
                (ClientErrorReportMessage)
                    new ClientErrorReportParser().Parse(
                        new ClientPacket(ClientErrorsPackets.REPORT, Array.Empty<byte>())
                    )
            ).Report
        );
    }

    // Reading and fingerprints.

    [Fact]
    public void A_report_is_read_with_its_texts_cut_and_the_page_without_its_query()
    {
        Assert.True(
            ClientErrorReport.TryRead(Bytes(TestHost.Report(kind: "nonsense")), 40, out var report)
        );

        Assert.Equal("error", report.Kind);
        Assert.Equal(
            "TypeError: Cannot read properties of undefined (reading 'roomId')",
            report.Message
        );
        Assert.Equal(40, report.Stack!.Length);
        Assert.Equal("https://hotel.example/client", report.Page);
        Assert.Equal("1920x1080@1.25", report.Viewport);
        Assert.Equal(7, report.RoomId);
        Assert.Equal(2, report.Breadcrumbs.Count);
        Assert.Null(report.Gpu);

        Assert.False(ClientErrorReport.TryRead(Bytes("not json"), 100, out _));
        Assert.False(ClientErrorReport.TryRead(Bytes("[1,2]"), 100, out _));
        Assert.False(ClientErrorReport.TryRead(Bytes("""{"build":"x"}"""), 100, out _));
    }

    [Fact]
    public void Only_the_last_breadcrumbs_are_kept()
    {
        var crumbs = Enumerable
            .Range(0, 50)
            .Select(i =>
                (object)
                    new
                    {
                        t = i,
                        c = "out",
                        m = $"Packet{i}",
                    }
            )
            .ToArray();

        Assert.True(
            ClientErrorReport.TryRead(
                Bytes(TestHost.Report(breadcrumbs: crumbs)),
                100,
                out var report
            )
        );
        Assert.Equal(ClientErrorReport.MAX_BREADCRUMBS, report.Breadcrumbs.Count);
        Assert.Equal("Packet49", report.Breadcrumbs[^1].M);
    }

    [Fact]
    public void Chrome_and_firefox_frames_are_read_without_origin_or_query()
    {
        var chrome = Fingerprints
            .Frames(
                "TypeError: x\n    at RoomView (https://a.example/assets/index-B3x9_kQa.js?v=2:12:3456)\n    at https://a.example/src/main.tsx:4:5"
            )
            .ToList();
        var firefox = Fingerprints
            .Frames(
                "RoomView@https://b.example/assets/index-B3x9_kQa.js:12:3456\n@debugger eval code:1:1"
            )
            .ToList();

        Assert.Equal(new StackFrame("RoomView", "/assets/index-B3x9_kQa.js", 12, 3456), chrome[0]);
        Assert.Equal(new StackFrame("", "/src/main.tsx", 4, 5), chrome[1]);
        Assert.Equal(chrome[0], firefox[0]);
        Assert.Equal(
            new StackFrame("roomViewClicked", "<anonymous>", 3, 15),
            Fingerprints
                .Frames("    at roomViewClicked (eval at evaluate (:311:30), <anonymous>:3:15)")
                .Single()
        );
        Assert.Empty(Fingerprints.Frames("    at async Promise.all (index 0)"));
        Assert.Equal(
            "/assets/index.js",
            Fingerprints.WithoutContentHash("/assets/index-B3x9_kQa.js")
        );
    }

    [Fact]
    public void Numbers_and_ids_do_not_split_an_error_but_the_build_does()
    {
        Read(
            TestHost.Report(
                message: "Room 12 has no furni 0x1f3a with id 8f14e45f-ceea-467f-a0e6-4a9e1b2c3d4e"
            ),
            out var a
        );
        Read(
            TestHost.Report(
                message: "Room 99 has no furni 0xbeef with id 00000000-0000-4000-8000-000000000000"
            ),
            out var b
        );
        Read(
            TestHost.Report(
                message: "Room 99 has no furni 0xbeef with id 00000000-0000-4000-8000-000000000000",
                build: "ffffffffffff"
            ),
            out var c
        );

        Assert.Equal(Fingerprints.Of(a, 5), Fingerprints.Of(b, 5));
        Assert.NotEqual(Fingerprints.Of(b, 5), Fingerprints.Of(c, 5));
        Assert.Equal(Fingerprints.Signature(b, 5), Fingerprints.Signature(c, 5));
        Assert.Equal(
            "TypeError: Room <n> has no furni <hex> with id <uuid>",
            Fingerprints.NormaliseMessage(a.Message)
        );
    }

    private static void Read(string json, out ClientErrorReport report) =>
        Assert.True(ClientErrorReport.TryRead(Bytes(json), 16000, out report));

    // Storing: dedupe, counts, reopen.

    [Fact]
    public async Task The_same_error_from_two_players_is_one_issue_with_its_counts_facets_and_day()
    {
        using var host = new TestHost();
        var first = host.Time.Now.UtcDateTime;

        var result = await host.StoreAsync(1, TestHost.Report(), TestHost.Report());

        host.Time.Now = host.Time.Now.AddMinutes(5);
        await host.StoreAsync(
            2,
            TestHost.Report(
                userAgent: "Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0"
            )
        );
        await host.StoreAsync(3, TestHost.Report(message: "Something else entirely"));

        Assert.Single(result.NewIssues);

        var issue = host.Query(db => db.Issues.OrderBy(x => x.Id).First());

        Assert.Equal(2, host.Query(db => db.Issues.Count()));
        Assert.Equal(3, issue.Occurrences);
        Assert.Equal(2, issue.Players);
        Assert.Equal(first, issue.FirstSeen);
        Assert.Equal(first.AddMinutes(5), issue.LastSeen);
        Assert.Equal(IssueEntity.OPEN, issue.Status);
        Assert.StartsWith("RoomView (/assets/index-B3x9_kQa.js:12:3456)", issue.TopFrame);

        var facets = host.Query(db => db.Facets.Where(x => x.IssueId == issue.Id).ToList());

        Assert.Equal(
            2,
            facets
                .Single(x => x.Facet == FacetEntity.BROWSER && x.Value == "Chrome 141")
                .Occurrences
        );
        Assert.Equal(
            1,
            facets
                .Single(x => x.Facet == FacetEntity.BROWSER && x.Value == "Firefox 133")
                .Occurrences
        );
        Assert.Contains(facets, x => x.Facet == FacetEntity.PLATFORM && x.Value == "Linux");
        Assert.Equal(3, facets.Single(x => x.Facet == FacetEntity.BUILD).Occurrences);
        Assert.Equal(3, host.Query(db => db.Days.Single(x => x.IssueId == issue.Id).Occurrences));
        Assert.Equal(3, host.Query(db => db.Occurrences.Count(x => x.IssueId == issue.Id)));
        Assert.All(
            host.Query(db => db.Occurrences.ToList()),
            x => Assert.DoesNotContain("secret-ticket", x.Page)
        );
    }

    [Fact]
    public async Task A_burst_of_one_error_keeps_a_few_occurrences_whole_and_counts_all()
    {
        using var host = new TestHost();

        await host.StoreAsync(1, Enumerable.Repeat(TestHost.Report(), 40).ToArray());

        Assert.Equal(40, host.Query(db => db.Issues.Single().Occurrences));
        Assert.Equal(
            ClientErrorStore.OCCURRENCES_PER_BATCH,
            host.Query(db => db.Occurrences.Count())
        );
    }

    [Fact]
    public async Task A_resolved_issue_reopens_on_a_new_build_but_not_on_the_build_it_was_resolved_on()
    {
        using var host = new TestHost();

        await host.StoreAsync(1, TestHost.Report(build: "build-one"));

        var admin = host.Admin();
        var id = host.Query(db => db.Issues.Single().Id);

        await admin.CallAsync("POST issues/{id}/resolve", id);

        // An old client still on the resolved build: counted, still resolved.
        var same = await host.StoreAsync(2, TestHost.Report(build: "build-one"));

        Assert.Empty(same.Reopened);
        Assert.Equal(IssueEntity.RESOLVED, host.Query(db => db.Issues.Single().Status));

        // A build with a different line for the same error: the fix did not hold.
        var later = await host.StoreAsync(
            3,
            TestHost.Report(
                build: "build-two",
                stack: "TypeError: x\n    at Xe (https://hotel.example/assets/index-Zz9_YYYY.js:40:10)\n    at Qa (https://hotel.example/assets/vendor-Ck2pL0aa.js:1:1200)"
            )
        );
        var issue = host.Query(db => db.Issues.Single());

        Assert.Empty(later.NewIssues);
        Assert.Equal(issue.Id, Assert.Single(later.Reopened).Issue.Id);
        Assert.Equal(IssueEntity.OPEN, issue.Status);
        Assert.Equal("build-two", issue.ReopenedBuild);
        Assert.Equal(3, issue.Occurrences);
        Assert.Equal(2, host.Query(db => db.Fingerprints.Count()));
        Assert.Contains(
            "build-one (2), build-two (1)",
            Admin.Json(await admin.CallAsync("GET issues/{id}", id))
        );
    }

    [Fact]
    public async Task An_ignored_issue_stays_ignored_and_counted()
    {
        using var host = new TestHost();

        await host.StoreAsync(1, TestHost.Report(build: "one"));

        var id = host.Query(db => db.Issues.Single().Id);

        await host.Admin().CallAsync("POST issues/{id}/ignore", id);
        await host.StoreAsync(1, TestHost.Report(build: "two"));

        Assert.Equal(IssueEntity.IGNORED, host.Query(db => db.Issues.Single().Status));
        Assert.Equal(2, host.Query(db => db.Issues.Single().Occurrences));
    }

    [Fact]
    public async Task Unreadable_reports_are_counted_and_skipped()
    {
        using var host = new TestHost();

        var result = await host.StoreAsync(1, "garbage", """{"nothing":true}""", TestHost.Report());

        Assert.Equal(2, result.Unreadable);
        Assert.Equal(1, result.Stored);
    }

    // Retention.

    [Fact]
    public async Task Retention_drops_old_occurrences_the_surplus_of_an_issue_and_issues_not_seen_for_long()
    {
        using var host = new TestHost(
            new ClientErrorsConfig
            {
                OccurrenceRetentionDays = 30,
                MaxOccurrencesPerIssue = 3,
                IssueRetentionDays = 90,
            }
        );
        var start = host.Time.Now;

        await host.StoreAsync(1, TestHost.Report(message: "Old error"));

        host.Time.Now = start.AddDays(40);
        await host.StoreAsync(1, TestHost.Report(message: "Busy error", build: "b1"));
        await host.StoreAsync(1, TestHost.Report(message: "Busy error", build: "b1"));

        for (var i = 0; i < 3; i++)
            await host.StoreAsync(1, TestHost.Report(message: "Busy error", build: "b1"));

        var removed = await host.Retention.RunAsync(CT);

        // The old error's one occurrence is past 30 days; the busy one keeps its 3 latest.
        Assert.Equal(1 + 2, removed.Occurrences);
        Assert.Equal(0, removed.Issues);
        Assert.Equal(2, host.Query(db => db.Issues.Count()));

        var busy = host.Query(db => db.Issues.Single(x => x.Message.Contains("Busy")).Id);
        var kept = host.Query(db =>
            db.Occurrences.Where(x => x.IssueId == busy).Select(x => x.Id).OrderBy(x => x).ToList()
        );

        Assert.Equal(3, kept.Count);
        Assert.Equal(5, host.Query(db => db.Issues.Single(x => x.Id == busy).Occurrences));

        host.Time.Now = start.AddDays(95);
        removed = await host.Retention.RunAsync(CT);

        // The old error was last seen 95 days ago: gone with its facets, day and fingerprint.
        Assert.Equal(1, removed.Issues);
        Assert.Equal(1, host.Query(db => db.Issues.Count()));
        Assert.Equal(1, host.Query(db => db.Fingerprints.Count()));
        Assert.True(host.Query(db => db.Facets.All(x => x.IssueId == busy)));
        Assert.True(host.Query(db => db.Days.All(x => x.IssueId == busy)));
    }

    // Discord.

    [Fact]
    public async Task No_webhook_posts_nothing()
    {
        using var host = new TestHost();
        var result = await host.StoreAsync(1, TestHost.Report());

        Assert.False(await host.Discord.PostNewAsync(result.NewIssues.Single()));
        Assert.Empty(host.Http.Posts);
    }

    [Fact]
    public async Task A_webhook_gets_new_issues_only_within_its_hourly_limit()
    {
        using var host = new TestHost(
            new ClientErrorsConfig
            {
                DiscordWebhookUrl = "https://discord.example/api/webhooks/1/x",
                DiscordMaxPerHour = 2,
            }
        );
        var results = new[]
        {
            await host.StoreAsync(1, TestHost.Report(message: "one")),
            await host.StoreAsync(1, TestHost.Report(message: "one")),
            await host.StoreAsync(1, TestHost.Report(message: "two")),
            await host.StoreAsync(1, TestHost.Report(message: "three")),
        };
        var issues = results.SelectMany(x => x.NewIssues).ToList();

        Assert.Equal(3, issues.Count);
        Assert.True(await host.Discord.PostNewAsync(issues[0]));
        Assert.True(await host.Discord.PostNewAsync(issues[1]));
        Assert.False(await host.Discord.PostNewAsync(issues[2]));
        Assert.Equal(2, host.Http.Posts.Count);
        Assert.Contains("New client error #1", host.Http.Posts[0]);
        Assert.Contains("\"parse\":[]", host.Http.Posts[0]);

        // The next hour says how many it left out.
        host.Time.Now = host.Time.Now.AddHours(1);
        Assert.True(await host.Discord.PostNewAsync(issues[2]));
        Assert.Contains("1 more new issues were not posted", host.Http.Posts[2]);
    }

    // Plugin notices.

    /// <summary>
    /// Waits (a few seconds at most) for <paramref name="calls"/> to reach <paramref name="count"/>:
    /// the worker publishes after a batch is committed. The test never reads the database while the
    /// worker runs (one SQLite connection).
    /// </summary>
    private static async Task WaitForAsync(Func<int> calls, int count)
    {
        for (var i = 0; i < 200 && calls() < count; i++)
            await Task.Delay(25, CT);

        Assert.Equal(count, calls());
    }

    [Fact]
    public async Task The_worker_publishes_a_new_issue_as_a_plugin_notice()
    {
        using var host = new TestHost();
        var published = new ConcurrentQueue<PluginNoticeEvent>();

        host.Notices.Publisher = (notice, _) =>
        {
            published.Enqueue(notice);

            return Task.CompletedTask;
        };

        var worker = host.Worker();

        worker.Start();
        await host.Ingest.AcceptAsync("s1", 4, Bytes(TestHost.Report(build: "b1")), Accepts, CT);
        await WaitForAsync(() => published.Count, 1);
        await worker.StopAsync();

        var notice = Assert.Single(published);
        var id = host.Query(db => db.Issues.Single().Id).ToString();

        Assert.Equal("clienterrors", notice.Source);
        Assert.Equal("issue.new", notice.Topic);
        Assert.Equal(id, notice.Key);
        Assert.Equal(PluginNoticeLevel.Error, notice.Level);
        Assert.Equal($"New client error #{id}", notice.Title);
        Assert.Contains("Cannot read properties of undefined", notice.Text);
        Assert.Equal(
            ["Kind=error", "Build=b1", "Players=1", "Count=1"],
            notice.Fields.Select(x => $"{x.Name}={x.Value}")
        );
        Assert.Null(notice.Url);
    }

    [Fact]
    public async Task A_publisher_that_throws_does_not_stop_the_worker_storing()
    {
        using var host = new TestHost();
        var calls = 0;

        host.Notices.Publisher = (_, _) =>
        {
            Interlocked.Increment(ref calls);

            throw new InvalidOperationException("relay down");
        };

        var worker = host.Worker();

        worker.Start();
        await host.Ingest.AcceptAsync("s1", 1, Bytes(TestHost.Report(message: "one")), Accepts, CT);
        await WaitForAsync(() => Volatile.Read(ref calls), 1);
        await host.Ingest.AcceptAsync("s1", 1, Bytes(TestHost.Report(message: "two")), Accepts, CT);
        await WaitForAsync(() => Volatile.Read(ref calls), 2);
        await worker.StopAsync();

        Assert.Equal(2, host.Query(db => db.Issues.Count()));
        Assert.Equal(2, host.Query(db => db.Occurrences.Count()));
    }

    [Fact]
    public async Task A_reopened_issue_is_a_warning_keyed_by_its_build_and_links_to_the_panel()
    {
        using var host = new TestHost();
        var notices = new Notices(
            NullLogger<Notices>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Turbo:Admin:PanelUrl"] = "https://admin.example/",
                    }
                )
                .Build()
        );
        var result = await host.StoreAsync(1, TestHost.Report());
        var issue = result.NewIssues.Single();

        issue.Issue.ReopenedBuild = "b2";

        var notice = Notices.Fit(notices.Issue(issue, reopened: true));

        Assert.Equal("issue.reopened", notice.Topic);
        Assert.Equal($"{issue.Issue.Id}@b2", notice.Key);
        Assert.Equal(PluginNoticeLevel.Warning, notice.Level);
        Assert.Equal(
            $"https://admin.example/plugin/clienterrors/issue/{issue.Issue.Id}",
            notice.Url
        );
        Assert.Equal("b2", notice.Fields.Single(x => x.Name == "Build").Value);
        // Cut to the event's limits, not refused.
        var fitted = Notices.Fit(
            notice with
            {
                Title = new string('t', 300),
                Text = new string('x', 5000),
                Fields = [.. Enumerable.Range(0, 30).Select(x => new PluginNoticeField("f", "v"))],
            }
        );

        Assert.Equal(PluginNoticeEvent.MAX_TITLE_LENGTH, fitted.Title.Length);
        Assert.Equal(PluginNoticeEvent.MAX_TEXT_LENGTH, fitted.Text!.Length);
        Assert.Equal(PluginNoticeEvent.MAX_FIELDS, fitted.Fields.Length);
    }

    // Admin.

    [Fact]
    public async Task A_lost_GPU_report_keeps_what_the_renderer_held_and_its_occurrence_shows_it()
    {
        using var host = new TestHost();

        await host.StoreAsync(
            1,
            TestHost.Report(
                kind: "renderer",
                message: "GPU device or context lost: the client started again",
                gpu: new
                {
                    name = "ANGLE (NVIDIA, NVIDIA GeForce RTX 3080 Direct3D11 vs_5_0 ps_5_0, D3D11)",
                    reason = "unknown",
                    message = "GPU process crashed",
                    visibility = "hidden",
                    focused = false,
                    textures = 331,
                    renderTextures = 194,
                    textureBytes = 40831092,
                    heapBytes = 200024266,
                    heapLimitBytes = 4395630592,
                }
            )
        );

        var occurrence = host.Query(db => db.Occurrences.Single());

        Assert.Equal(
            "ANGLE (NVIDIA, NVIDIA GeForce RTX 3080 Direct3D11 vs_5_0 ps_5_0, D3D11)"
                + " | lost: unknown: GPU process crashed | page hidden, not focused"
                + " | 331 textures (194 render), 38.9 MB | JS heap 190.8 MB of 4192.0 MB",
            occurrence.Gpu
        );
        Assert.Contains(
            "331 textures (194 render), 38.9 MB",
            Admin.Json(await host.Admin().CallAsync("GET occurrences/{id}", occurrence.Id))
        );
    }

    [Fact]
    public async Task The_admin_pages_list_filter_resolve_link_and_delete_behind_their_own_nodes()
    {
        using var host = new TestHost();

        await host.StoreAsync(
            1,
            TestHost.Report(),
            TestHost.Report(kind: "rejection", message: "Load failed")
        );
        await host.StoreAsync(2, TestHost.Report());

        var admin = host.Admin();
        var id = host.Query(db => db.Issues.Single(x => x.Message.Contains("roomId")).Id);
        var other = host.Query(db => db.Issues.Single(x => x.Message.Contains("Load")).Id);

        Assert.Equal(
            [
                "plugin.clienterrors.issues.view",
                "plugin.clienterrors.issues.manage",
                "plugin.clienterrors.issues.delete",
            ],
            admin.Nodes
        );
        Assert.All(
            admin.Map.Where(x => x.Key.StartsWith("GET")),
            x => Assert.Equal("plugin.clienterrors.issues.view", x.Value.Node)
        );
        Assert.Equal("plugin.clienterrors.issues.manage", admin.Map["POST issues/{id}/link"].Node);
        Assert.Equal("plugin.clienterrors.issues.delete", admin.Map["DELETE issues/{id}"].Node);
        Assert.Equal(["issues", "issue", "occurrence"], admin.Pages.Select(x => x.Id));

        var list = Admin.Json(await admin.CallAsync("GET issues"));

        Assert.Contains("\"total\":2", list);
        Assert.Contains("\"players\":2", list);
        Assert.Contains("Chrome 141 (2)", list);
        Assert.Equal(
            "·············█",
            System
                .Text.Json.JsonDocument.Parse(list)
                .RootElement.GetProperty("items")[1]
                .GetProperty("trend")
                .GetString()
        );
        Assert.Contains(
            "\"total\":1",
            Admin.Json(await admin.CallAsync("GET issues", query: "kind=rejection"))
        );
        Assert.Contains(
            "\"total\":1",
            Admin.Json(await admin.CallAsync("GET issues", query: "q=roomId"))
        );
        Assert.Contains(
            "\"total\":2",
            Admin.Json(await admin.CallAsync("GET issues", query: "q=a1b2c3d4e5f6"))
        );

        await admin.CallAsync("POST issues/{id}/resolve", id);
        Assert.Contains(
            "\"total\":1",
            Admin.Json(await admin.CallAsync("GET issues", query: "status=open"))
        );
        Assert.Equal(9, host.Query(db => db.Issues.Single(x => x.Id == id).ResolvedBy));
        Assert.Contains(
            "\"closed\":true",
            Admin.Json(await admin.CallAsync("GET issues/{id}", id))
        );

        await admin.CallAsync("POST issues/{id}/reopen", id);
        Assert.Null(host.Query(db => db.Issues.Single(x => x.Id == id).ResolvedAt));

        var linked = await admin.CallAsync(
            "POST issues/{id}/link",
            id,
            body: """{"report":"https://forum.example/t/123","note":"Tester saw it in the catalog"}"""
        );

        Assert.Equal(200, linked.Status);
        Assert.Equal(
            "https://forum.example/t/123",
            host.Query(db => db.Issues.Single(x => x.Id == id).Report)
        );

        // Only the note changed: the report stays.
        await admin.CallAsync("POST issues/{id}/link", id, body: """{"note":"And in the shop"}""");
        Assert.Equal(
            "https://forum.example/t/123",
            host.Query(db => db.Issues.Single(x => x.Id == id).Report)
        );
        Assert.Equal("And in the shop", host.Query(db => db.Issues.Single(x => x.Id == id).Note));
        Assert.Equal(
            400,
            (
                await admin.CallAsync(
                    "POST issues/{id}/link",
                    id,
                    body: $$"""{"report":"{{new string('x', 600)}}"}"""
                )
            ).Status
        );

        var occurrences = Admin.Json(await admin.CallAsync("GET issues/{id}/occurrences", id));
        var occurrence = host.Query(db => db.Occurrences.First(x => x.IssueId == id).Id);

        Assert.Contains("\"total\":2", occurrences);
        Assert.Contains(
            "RoomView",
            Admin.Json(await admin.CallAsync("GET occurrences/{id}/stack", occurrence))
        );
        Assert.Contains(
            "\"before\":\"-1.0 s\"",
            Admin.Json(await admin.CallAsync("GET occurrences/{id}/breadcrumbs", occurrence))
        );
        Assert.Equal(
            "text/plain",
            (await admin.CallAsync("GET occurrences/{id}/stack.txt", occurrence)).ContentType
        );
        Assert.Contains(
            "\"total\":14",
            Admin.Json(await admin.CallAsync("GET issues/{id}/days", id))
        );
        Assert.Contains(
            "{\"label\":\"Reports today (UTC)\",\"value\":3}",
            Admin.Json(await admin.CallAsync("GET stats"))
        );

        Assert.Equal(204, (await admin.CallAsync("DELETE issues/{id}", other)).Status);
        Assert.Equal(404, (await admin.CallAsync("GET issues/{id}", other)).Status);
        Assert.Equal(0, host.Query(db => db.Occurrences.Count(x => x.IssueId == other)));
    }

    [Fact]
    public void The_sparkline_is_one_character_a_day()
    {
        Assert.Equal("·▁█▄", ClientErrorsAdmin.Sparkline([0, 1, 8, 4]));
        Assert.Equal("····", ClientErrorsAdmin.Sparkline([0, 0, 0, 0]));
    }

    [Fact]
    public async Task The_migration_creates_the_tables_the_model_names()
    {
        using var host = new TestHost();

        // EnsureCreated made them from the model; the migrations must name the same tables.
        var folder = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "TurboClientErrors",
            "Database",
            "Migrations"
        );
        var migrations = string.Concat(
            await Task.WhenAll(
                System
                    .IO.Directory.GetFiles(folder, "*.cs")
                    .Where(x => !x.EndsWith(".Designer.cs") && !x.EndsWith("Snapshot.cs"))
                    .Select(x => System.IO.File.ReadAllTextAsync(x, CT))
            )
        );

        foreach (
            var table in host.Query(db =>
                db.Model.GetEntityTypes().Select(x => x.GetTableName()).ToList()
            )
        )
            Assert.Contains($"\"{table}\"", migrations);
    }
}
