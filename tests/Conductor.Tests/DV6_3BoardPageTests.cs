using System.Globalization;
using System.Text.RegularExpressions;

using Conductor.Core;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Publishing;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV6.3 — the board snapshot as ONE self-contained HTML file, and the document it leaves as.
///
/// <para><b>What the checkpoint is for.</b> The page that was asked for, without the thing that was
/// refused: ADR-0005 rules out inbound — no port, no tunnel, no reverse proxy — and the loopback
/// control plane carries <c>/control</c>, so a route to the read view is a route to the steering
/// wheel. A file has no route. It is rendered at a boundary from the SAME contracts the control
/// plane serves, pushed out as a Telegram document, and read wherever it lands.</para>
///
/// <para><b>The three bars, and why each is a test rather than a promise.</b> A page that fetches a
/// stylesheet is unstyled text on a phone in a tunnel, and a page that fetches anything tells a third
/// party when the owner read their own board — so the self-containment scan is on the bytes. A file
/// cannot say "now", and a file that looks live is worse than none — so the staleness sentence is
/// pinned. And an attachment that is really a path is the K5.3 defect this project already paid for
/// once — so the document is proven on the wire, against a stub standing in for api.telegram.org,
/// by the field name, the file name and the byte count that actually left the process.</para>
/// </summary>
public sealed class DV6_3BoardPageTests
{
    private readonly ITestOutputHelper _out;

    public DV6_3BoardPageTests(ITestOutputHelper output) => _out = output;

    /// <summary>Fixed, so the golden pins the render and not the clock.</summary>
    private static readonly DateTime RenderedAt = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private const string Boundary = "session 18 end";
    private const string ChatId = "-1009000600300";

    // ────────────────────────────── the page states its own staleness ──────────────────────────────

    [Fact]
    public void The_page_says_when_it_was_rendered_which_boundary_made_it_and_that_it_does_not_update()
    {
        var html = BoardSnapshotHtml.Render(Snapshot());

        Assert.Contains("as of 2026-08-26 12:00 UTC", html, StringComparison.Ordinal);
        Assert.Contains(Boundary, html, StringComparison.Ordinal);
        Assert.Contains("does not update", html, StringComparison.Ordinal);
        // The claim is above the board, not in a footer nobody scrolls to.
        Assert.True(html.IndexOf("as of 2026-08-26", StringComparison.Ordinal)
                  < html.IndexOf("<section class=\"board\"", StringComparison.Ordinal),
            "the staleness line must come before the board");
    }

    // ────────────────────────────── self-contained means self-contained ──────────────────────────────

    /// <summary>Every way a document can ask a network for something, checked on the rendered bytes.
    /// <c>src=</c> covers img/script/iframe in one; <c>url(</c> covers a CSS fetch; the two schemes
    /// cover a link, a form action and an <c>@import</c> that dodged the others.</summary>
    [Fact]
    public void The_page_reaches_out_to_nothing()
    {
        var html = BoardSnapshotHtml.Render(Snapshot());

        foreach (var forbidden in new[] { "http://", "https://", "<script", "src=", "@import", "url(", "<iframe", "<link", "<form" })
            Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);

        // And it is a whole document that IS styled — the point is one file, not a plain one.
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("<style>", html, StringComparison.Ordinal);
        Assert.EndsWith("</html>\n", html, StringComparison.Ordinal);
    }

    /// <summary>A title carrying markup must not be able to open a tag, close the style block, or
    /// smuggle a script into a file the owner opens on their phone.</summary>
    [Fact]
    public void A_card_that_carries_markup_cannot_break_the_page()
    {
        var hostile = Snapshot() with
        {
            Tasks = new TasksDto([Task("DV6.9", "<script>alert(\"x\")</script> & </style>", "todo", 9, null)]),
        };

        var html = BoardSnapshotHtml.Render(hostile);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; &lt;/style&gt;", html, StringComparison.Ordinal);
    }

    // ────────────────────────────── the board itself ──────────────────────────────

    [Fact]
    public void Every_column_is_there_even_when_it_is_empty()
    {
        var html = BoardSnapshotHtml.Render(Snapshot());

        // DV6.2's own first-choice names, so the page and the Projects v2 mirror share a vocabulary.
        foreach (var column in new[] { "Todo", "In Progress", "Blocked", "Done", "Skipped" })
            Assert.Contains(">" + column + " <span", html, StringComparison.Ordinal);

        // The fixture has nothing skipped, and the column says so rather than vanishing: a board that
        // changes shape between renders cannot be compared with the one before it.
        Assert.Contains("empty", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_card_with_no_status_stamp_says_age_unknown_rather_than_zero()
    {
        var unstamped = Snapshot() with { Tasks = new TasksDto([Task("DV6.9", "no stamp", "todo", 9, null)]) };

        var html = BoardSnapshotHtml.Render(unstamped);

        Assert.Contains("age unknown", html, StringComparison.Ordinal);
        Assert.DoesNotContain("0 days in column", html, StringComparison.Ordinal);
        Assert.DoesNotContain("under a minute in column", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_card_that_has_been_in_its_column_for_days_says_how_many()
    {
        var html = BoardSnapshotHtml.Render(Snapshot());
        Assert.Contains("3 days in column", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_owed_is_said_out_loud_rather_than_hidden()
    {
        var quiet = Snapshot() with { Owner = new OwnerQueueDto(0, RenderedAt.ToString("O", CultureInfo.InvariantCulture), []) };

        var html = BoardSnapshotHtml.Render(quiet);

        Assert.Contains("Owner queue — 0 items", html, StringComparison.Ordinal);
        Assert.Contains("Nothing is waiting on the owner.", html, StringComparison.Ordinal);
    }

    /// <summary>DV6.1's rule, inherited: a surface that prints "0 open bugs" every day teaches its
    /// reader to skip the line that will one day say eleven.</summary>
    [Fact]
    public void An_empty_ledger_puts_no_ledger_row_on_the_page()
    {
        var html = BoardSnapshotHtml.Render(Snapshot() with { LedgerLine = "" });

        // The fact, not the word: an evidence path may well have "ledger" in its name.
        Assert.DoesNotContain("<span>ledger</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("open bug", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_owner_queue_carries_the_exact_command_that_clears_each_item()
    {
        var html = BoardSnapshotHtml.Render(Snapshot());

        Assert.Contains("clears with <code>conductor approve</code>", html, StringComparison.Ordinal);
        // A blocked-until wait has no command, and inventing one would send the owner to a keyboard
        // for nothing (DV1.2's rule, kept).
        Assert.Contains("nothing typed clears this", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_status_with_no_column_is_reported_rather_than_dropped()
    {
        var odd = Snapshot() with { Tasks = new TasksDto([Task("DV6.9", "parked somewhere new", "quarantined", 9, null)]) };

        var html = BoardSnapshotHtml.Render(odd);

        Assert.Contains("1 card in no column — status quarantined", html, StringComparison.Ordinal);
    }

    // ────────────────────────────── the golden ──────────────────────────────

    /// <summary>The whole page, byte for byte. Strict, like KS11.1's: a missing golden FAILS rather
    /// than writing itself, because a golden that writes itself on first run pins whatever the code
    /// happened to do that day.</summary>
    [Fact]
    public void Golden_the_whole_page()
    {
        var html = BoardSnapshotHtml.Render(Snapshot());
        var path = Path.Combine(RepoRoot(), "tests", "Conductor.Tests", "testdata", "dv6-3", "board.html");
        var normalised = html.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

        if (string.Equals(Environment.GetEnvironmentVariable("CONDUCTOR_GOLDEN_REBASELINE"), "1", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, normalised);
            return;
        }

        Assert.True(File.Exists(path),
            $"golden board.html is missing — regenerate with CONDUCTOR_GOLDEN_REBASELINE=1 and READ the diff");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), normalised, StringComparer.Ordinal);
    }

    // ────────────────────────────── written, and pushed as a document ──────────────────────────────

    [Fact]
    public void Publishing_writes_one_file_atomically_and_hands_back_what_it_rendered()
    {
        var repo = Path.Combine(Path.GetTempPath(), "conductor-dv63-publish-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, ".conductor"));
            var plan = new PlanConfig { Name = "Divan", Repo = repo, Tracker = "TRACKER.md" };
            var state = new RunState { RunId = "dv63", SessionCounter = 18 };

            var published = BoardSnapshotPublisher.Publish(plan, state, new TrackerSnapshot(),
                SnapshotBuilder.Build(plan, state, new TrackerSnapshot(), "", null), null,
                Boundary, RenderedAt, out var refusal);

            Assert.Equal("", refusal);
            Assert.NotNull(published);
            Assert.Equal(Path.Combine(repo, ".conductor", "board.html"), published!.Path);
            Assert.True(File.Exists(published.Path));
            Assert.Equal(BoardSnapshotHtml.Render(published.Snapshot),
                File.ReadAllText(published.Path).Replace("\r\n", "\n", StringComparison.Ordinal));

            // No litter beside it: the atomic write's temp file is gone, not left for a reader to find.
            Assert.Empty(Directory.GetFiles(Path.Combine(repo, ".conductor"), "*.tmp-*"));
        }
        finally { TestTemp.DeleteTree(repo); }
    }

    /// <summary>The wire, not the intention. K5.3 registered artifacts and pushed their PATHS, which
    /// from a phone is useless; this asserts the bytes of the page left the process as a multipart
    /// <c>sendDocument</c> upload, with the caption that says how old it is.</summary>
    [Fact]
    public async System.Threading.Tasks.Task The_board_page_arrives_as_a_document_not_as_a_path()
    {
        var repo = Path.Combine(Path.GetTempPath(), "conductor-dv63-wire-" + Guid.NewGuid().ToString("N"));
        using var bot = new RecordingBotApi();
        try
        {
            var stateDir = Path.Combine(repo, ".conductor");
            Directory.CreateDirectory(stateDir);
            // Trap 4: a SCRATCH token, never the owner's — the bot this reaches is the loopback stub.
            SecretsStore.WriteTelegramToken(stateDir, "dv63-scratch-token");
            var page = Path.Combine(stateDir, BoardSnapshotHtml.FileName);
            var snapshot = Snapshot();
            await File.WriteAllTextAsync(page, BoardSnapshotHtml.Render(snapshot));

            var plan = new PlanConfig
            {
                Name = "Divan",
                Repo = repo,
                Tracker = "TRACKER.md",
                Stages = { new StageConfig { Id = "DV6", Title = "The record that gets out", Sessions = 1 } },
                Telegram = new TelegramConfig
                {
                    AllowedChatIds = { ChatId },
                    PollIntervalSeconds = 60,
                    ApiBaseUrl = bot.Root,
                },
            };

            using var svc = new TelegramService(plan, new RunState { RunId = "dv63", SessionCounter = 18 },
                NullLogger<TelegramService>.Instance);
            await ((IHostedService)svc).StartAsync(CancellationToken.None);
            await svc.PushBoardSnapshotAsync(page, snapshot);
            await ((IHostedService)svc).StopAsync(CancellationToken.None);

            var calls = bot.Snapshot();
            foreach (var c in calls) _out.WriteLine(c.Describe());

            var call = Assert.Single(calls);
            Assert.Equal("sendDocument", call.Method);
            Assert.Equal("document", call.FileField);
            Assert.Equal("board.html", call.FileName);
            // The whole page went up, not a path to it. The stub trims the trailing CR/LF of a part
            // when it splits on the boundary, so a page ending in a newline is recorded one byte
            // short of the file; everything else about the upload is exact.
            var onDisk = new FileInfo(page).Length;
            Assert.InRange(call.FileBytes, onDisk - 2, onDisk);
            Assert.True(call.FileBytes > 4000, $"only {call.FileBytes} bytes were uploaded");
            Assert.True(call.DisableNotification, "the board is quiet — the owner queue is what buzzes");

            // The caption carries the fact a document in a chat cannot carry: how old it is.
            Assert.Contains("as of 2026-08-26 12:00 UTC", call.Caption!, StringComparison.Ordinal);
            Assert.Contains("it does not update", call.Caption!, StringComparison.Ordinal);
            Assert.Contains("board — 3 of 8 checkpoints done", call.Caption!, StringComparison.Ordinal);
            Assert.DoesNotContain(page, call.Caption!, StringComparison.OrdinalIgnoreCase);
        }
        finally { TestTemp.DeleteTree(repo); }
    }

    // ────────────────────────────── ADR-0005 holds ──────────────────────────────

    /// <summary>Publish, don't serve — asserted over the source of the publishing path rather than
    /// promised in a doc comment. A listener, a socket, a URL prefix or a tunnel appearing anywhere in
    /// it is the checkpoint being quietly reversed, and the whole point of the file is that there is
    /// nothing to connect to.</summary>
    [Fact]
    public void The_publishing_path_opens_no_port_no_listener_and_no_tunnel()
    {
        var root = RepoRoot();
        string[] files =
        [
            .. Directory.GetFiles(Path.Combine(root, "src", "Conductor.Core", "Publishing"), "*.cs"),
            Path.Combine(root, "src", "Conductor.Core", "Orchestration", "RunContext.Board.cs"),
        ];
        Assert.True(files.Length >= 4, "the publishing path should be four files; found " + files.Length.ToString(CultureInfo.InvariantCulture));

        var forbidden = new Regex(@"HttpListener|TcpListener|Socket|\.Prefixes|\bBind\(|ngrok|cloudflared|tailscale|Funnel",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var hit = forbidden.Match(text);
            Assert.False(hit.Success,
                $"{Path.GetFileName(file)} names '{hit.Value}' — ADR-0005 says the read view is published, never served");
        }
    }

    // ────────────────────────────── the fixture ──────────────────────────────

    /// <summary>Every column, an owner queue, evidence and a ledger line — the shape that exercises
    /// the whole renderer. <c>CH1_1BoardPageLineEndingsTests</c> reads it too, so the line-ending
    /// property is asserted over the same document this class pins.</summary>
    internal static BoardSnapshot Snapshot() => new(
        State: State(),
        Tasks: new TasksDto(
        [
            Task("DV6.1", "Bugs and followups as a long-lived issue class", "done", 1, RenderedAt.AddDays(-2)),
            Task("DV6.2", "The columns: the Projects v2 mutation path", "done", 2, RenderedAt.AddHours(-20)),
            Task("DV6.3", "The board snapshot as one self-contained HTML file", "in_progress", 3, RenderedAt.AddMinutes(-40)),
            Task("DV6.4", "SARIF export for file/line bugs", "todo", 4, RenderedAt.AddDays(-3)),
            Task("DV7.1", "The archived one", "archived", 5, RenderedAt.AddDays(-9)),
            Task("DV5.3", "Waiting on the owner", "blocked", 6, RenderedAt.AddDays(-1)),
        ]),
        Owner: new OwnerQueueDto(2, RenderedAt.ToString("O", CultureInfo.InvariantCulture),
        [
            new OwnerQueueItemDto("owner-1", "budget", "The run has reached its cost cap", "DV6.3",
                "conductor approve", RenderedAt.AddHours(-2).ToString("O", CultureInfo.InvariantCulture), 7200,
                "only the owner can raise a ceiling"),
            new OwnerQueueItemDto("owner-2", "wait", "Blocked until the deploy window opens", "DV6.4",
                "", null, null, null),
        ]),
        Evidence:
        [
            new EvidenceArtifactDto(".conductor/evidence/DV6/dv6-2-the-columns.md", "text", "DV6.2", "DV6", 17,
                "abc123", 8_842, "2026-08-26T09:14:02.0000000Z", "claim", false),
            new EvidenceArtifactDto(".conductor/evidence/DV6/dv6-1-ledger-issue-class.md", "text", "DV6.1", "DV6", 16,
                "def456", 512, "2026-08-25T18:31:44.0000000Z", "claim", false),
        ],
        LedgerLine: "ledger: 28 open bugs · 12 open followups · oldest bug 21 days",
        Boundary: Boundary,
        RenderedUtc: RenderedAt);

    private static TaskDto Task(string id, string title, string status, int order, DateTime? since) => new(
        TaskId: id, CheckpointId: id, Title: title, Status: status, Source: "plan", Order: order,
        Context: "", Paths: [], Kind: "checkpoint", StageId: id[..3],
        Confirmed: string.Equals(status, "done", StringComparison.Ordinal), Qa: "",
        SessionNumber: order + 12, StatusSinceUtc: since?.ToString("O", CultureInfo.InvariantCulture),
        Attempts: order == 3 ? 2 : 1);

    private static StateDto State() => new(
        PlanName: "Divan - the chancellery", Status: "running", AttentionReason: null,
        StageId: "DV6", StageTitle: "The record that gets out", Persona: null,
        DoneCount: 3, TotalCount: 8, TotalCostUsd: 41.5m, OverheadCostUsd: 0.4m,
        TokensInput: 12_000, TokensOutput: 4_000, TokensReasoning: 0,
        CurrentCheckpoint: "DV6.3", CurrentCheckpointTitle: "The board snapshot",
        GateSummary: "build OK · tests OK",
        Stages: [], RunId: "dv63", Repo: "C:/code/conductor", PlanDir: "plans/divan",
        SessionNumber: 18, SessionKind: "Deliver", Attempt: 1, MaxAttempts: 8,
        SessionElapsedSec: 2_400, AgentActive: true,
        SessionCostUsd: 3.2m, SessionTokensInput: 900, SessionTokensOutput: 300, SessionTokensReasoning: 0,
        Gates: [])
    {
        CostSpent = 41.5m,
        CostCap = 280m,
        CostRemaining = 238.5m,
        EngineVersion = "0.4.1",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
