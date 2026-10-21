using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Turbo.Primitives.Packets;
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
