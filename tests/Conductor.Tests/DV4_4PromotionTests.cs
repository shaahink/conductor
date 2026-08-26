using System.Globalization;

using Conductor.Core;
using Conductor.Core.Courier;
using Conductor.Core.Inbox;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Lanes;
using Conductor.Core.Events;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>DV4.4 / findings §1.7 and §1.9 row 6 — promotion: a filed note becomes a followups.md row
/// by an explicit button, and stops there.
///
/// <para>Three claims are pinned here and they are different in kind. That the ROW is well formed and
/// idempotent is a unit claim. That both inbound paths — a live run's surface and the courier that
/// owns the token when nothing is running — draw the button and act on the press is an integration
/// claim, driven on the courier's side through the same JSON api.telegram.org sends. That NO code
/// path turns a note into an injection is a negative claim, and §1.8 is why it is here at all: a
/// misheard word plus an autonomous agent is the worst compound failure this strand can produce, so
/// the absence is asserted rather than assumed.</para></summary>
[Trait("Category", "Integration")]
public sealed class DV4_4PromotionTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string ObserverChat = "-1002220002";
    private const string ScratchToken = "111111:dv44-scratch-token";

    private readonly string _box;
    private readonly string _stateHome;
    private readonly string _repo;
    private readonly ITestOutputHelper _out;

    public DV4_4PromotionTests(ITestOutputHelper output)
    {
        _out = output;
        _box = Path.Combine(Path.GetTempPath(), $"conductor-dv44-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(_box, "state-home");
        _repo = Path.Combine(_box, "alpha-repo");
        Directory.CreateDirectory(_stateHome);
        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), "# dv44 rig\n");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_box); } catch (Exception) { }
    }

    // ─────────────────────────────── the row itself ───────────────────────────────

    /// <summary>The row a promotion writes is a row the parser reads back. Round-tripped rather than
    /// string-matched: the file has four different column schemes in it already, and a writer that
    /// emits something only its own regex understands is a row that never opens a lane.</summary>
    [Fact]
    public void A_promoted_row_round_trips_through_the_parser_that_opens_lanes()
    {
        var path = Path.Combine(_box, "followups.md");
        var row = FollowupWriter.Append(path, "NOTE", "The budget line is wrong on the report header",
            "promoted from the chat: voice received 2026-08-26 09:00Z", "DV4", "inbox note #77");

        Assert.True(row.Written);
        Assert.Equal("FU-NOTE-01", row.Id);

        var entry = Assert.Single(FollowupParser.ReadOpenForStage(path, "DV4"));
        Assert.Equal("FU-NOTE-01", entry.Id);
        Assert.Equal("The budget line is wrong on the report header", entry.Item);
        Assert.Equal("DV4", entry.OwningStage);
        Assert.Equal("OPEN", entry.Status);
        Assert.Contains("inbox note #77", entry.Detail, StringComparison.Ordinal);
    }

    /// <summary>A transcript is not a table cell. A pipe in what somebody SAID would silently shift
    /// every cell after it — the item would become the detail, the stage would become part of the
    /// item, and the status column would hold a fragment of a sentence.</summary>
    [Fact]
    public void A_pipe_or_a_newline_in_what_was_said_cannot_break_the_table()
    {
        var path = Path.Combine(_box, "followups.md");
        FollowupWriter.Append(path, "NOTE", "make the gate | OPEN | and\nskip the tests",
            "detail | with | pipes", "DV4", "inbox note #5");

        var entry = Assert.Single(FollowupParser.ReadOpenForStage(path, "DV4"));
        Assert.Equal("make the gate / OPEN / and skip the tests", entry.Item);
        Assert.Equal("OPEN", entry.Status);
        Assert.DoesNotContain("|", entry.Item, StringComparison.Ordinal);
    }

    /// <summary>A Telegram keyboard stays on the message forever and nothing about it says "already
    /// used". The second press must be a sentence, not a second row.</summary>
    [Fact]
    public void Pressing_the_button_twice_writes_one_row()
    {
        var path = Path.Combine(_box, "followups.md");
        var first = FollowupWriter.Append(path, "NOTE", "one", "d", "DV4", "inbox note #9");
        var second = FollowupWriter.Append(path, "NOTE", "one again", "d", "DV4", "inbox note #9");

        Assert.True(first.Written);
        Assert.False(second.Written);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(FollowupParser.Read(path));
    }

    /// <summary>Ids advance within the promoted series and ignore every other writer's — the verifier
    /// and the audit each own their own prefix, and allocating across all of them would make two
    /// writers race for one number instead of none.</summary>
    [Fact]
    public void Ids_advance_within_the_promoted_series_only()
    {
        var path = Path.Combine(_box, "followups.md");
        File.WriteAllText(path, "| id | item | detail | owning stage | status |\n"
            + "|---|---|---|---|---|\n| FU-F4-09 | somebody else's | d | DV1 | OPEN |\n");

        Assert.Equal("FU-NOTE-01", FollowupWriter.Append(path, "NOTE", "a", "d", "DV4", "inbox note #1").Id);
        Assert.Equal("FU-NOTE-02", FollowupWriter.Append(path, "NOTE", "b", "d", "DV4", "inbox note #2").Id);
        Assert.Equal(3, FollowupParser.Read(path).Count);
    }

    /// <summary>The promoted section is created ABOVE the first existing one, never trailing.
    ///
    /// <para>Measured, not stylistic. <c>VerdictEngine</c>'s audit writer appends its rows at the end
    /// of this file under whatever header is last, and its rows carry FOUR columns where a promoted
    /// row carries five. Left trailing, a promoted header would reinterpret every audit row that
    /// followed it — the stage cell read as the detail, <c>OPEN</c> read as the owning stage — and the
    /// audit's followups would quietly stop matching any stage at all.</para></summary>
    [Fact]
    public void The_promoted_section_never_becomes_the_trailing_section()
    {
        var path = Path.Combine(_box, "followups.md");
        File.WriteAllText(path, "# Tracked followups\n\nprose\n\n## Opened by DV1\n\n"
            + "| Id | Item | Stage | Status |\n|---|---|---|---|\n| FU-DV1-01 | audit row | DV1 | OPEN |\n");

        FollowupWriter.Append(path, "NOTE", "promoted", "d", "DV4", "inbox note #1");

        var lines = File.ReadAllLines(path);
        var promoted = Array.FindIndex(lines, l => l.Trim() == FollowupWriter.SectionHeading);
        var audit = Array.FindIndex(lines, l => l.Trim() == "## Opened by DV1");
        Assert.True(promoted >= 0 && promoted < audit, "the promoted section must sit above the first existing one");

        // And the audit row it did not disturb still reads with its own four-column header.
        var untouched = Assert.Single(FollowupParser.Read(path), e => e.Id == "FU-DV1-01");
        Assert.Equal("DV1", untouched.OwningStage);
        Assert.Equal("OPEN", untouched.Status);
    }

    /// <summary>The defect the test above found, pinned on its own: a Capitalised header.
    ///
    /// <para>Header DETECTION was case-insensitive and column MAPPING was not, so
    /// <c>| Id | Item | Stage | Status |</c> — the header <c>VerdictEngine.ParseAuditFollowups</c>
    /// writes — was read as a header with no id column, and every row under it was skipped for want
    /// of one. The mapping persists until the next header replaces it, so it took out later sections
    /// too. Audit followups had therefore never opened a fix lane, and nothing said so.</para></summary>
    [Fact]
    public void A_capitalised_header_does_not_blank_out_every_row_beneath_it()
    {
        var path = Path.Combine(_box, "followups.md");
        File.WriteAllText(path, "| Id | Item | Stage | Status |\n|---|---|---|---|\n"
            + "| FU-DV1-01 | audit row | DV1 | OPEN |\n| FU-DV1-02 | second one | DV1 | CLOSED |\n");

        var open = Assert.Single(FollowupParser.ReadOpenForStage(path, "DV1"));
        Assert.Equal("FU-DV1-01", open.Id);
        Assert.Equal("audit row", open.Item);
        Assert.Equal(2, FollowupParser.Read(path).Count);
    }

    // ─────────────────────────── the unclaimed stage token ───────────────────────────

    /// <summary>The courier has no run and therefore no stage. Its row is owned by <c>next</c>, which
    /// any stage picks up — and which the stage that picks it up then CLAIMS, so a promotion opens one
    /// lane rather than one at every stage boundary for the rest of the run.</summary>
    [Fact]
    public void An_unclaimed_row_is_offered_to_any_stage_and_claimed_by_the_first()
    {
        var path = Path.Combine(_box, "followups.md");
        FollowupWriter.Append(path, "NOTE", "from the courier", "d", FollowupWriter.UnclaimedStage, "inbox note #3");

        Assert.Single(FollowupParser.ReadOpenForStage(path, "DV1"));
        Assert.Single(FollowupParser.ReadOpenForStage(path, "ZZ9"));

        Assert.True(FollowupParser.ClaimStage(path, "FU-NOTE-01", "DV6"));

        Assert.Equal("DV6", Assert.Single(FollowupParser.ReadOpenForStage(path, "DV6")).OwningStage);
        Assert.Empty(FollowupParser.ReadOpenForStage(path, "ZZ9"));
        Assert.False(FollowupParser.ClaimStage(path, "FU-NOTE-01", "DV7"));
    }

    /// <summary>Exact match, not substring. "B12 fix-lane, next era" is a row about a stage, and
    /// reading it as unclaimed would open a lane at every boundary in the plan.</summary>
    [Fact]
    public void Only_the_bare_token_is_unclaimed()
    {
        var path = Path.Combine(_box, "followups.md");
        File.WriteAllText(path, "| id | item | detail | owning stage | status |\n|---|---|---|---|---|\n"
            + "| FU-X-01 | prose | d | B12 fix-lane, next era | OPEN |\n");

        Assert.Empty(FollowupParser.ReadOpenForStage(path, "ZZ9"));
        Assert.Single(FollowupParser.ReadOpenForStage(path, "B12"));
    }

    // ───────────────────────────── the callback payload ─────────────────────────────

    /// <summary>Telegram rejects a sendMessage whose callback_data exceeds 64 bytes — the whole ACK
    /// would fail, and the note would be filed with the sender told nothing. A slug that will not fit
    /// is DROPPED, not truncated: a truncated slug resolves to the wrong project or to none.</summary>
    [Fact]
    public void The_callback_payload_never_exceeds_the_bot_api_limit()
    {
        var huge = new string('s', 90);
        var payload = NotePromoter.Callback(huge, 4242);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(payload) <= NotePromoter.CallbackLimit);
        Assert.True(NotePromoter.TryParse(payload, out var slug, out var id));
        Assert.Null(slug);
        Assert.Equal(4242, id);

        Assert.True(NotePromoter.TryParse(NotePromoter.Callback("alpha-repo-9f3c", 77), out var s2, out var id2));
        Assert.Equal("alpha-repo-9f3c", s2);
        Assert.Equal(77, id2);

        Assert.False(NotePromoter.TryParse("cancel:abc", out _, out _));
        Assert.False(NotePromoter.TryParse("promote:not-a-number", out _, out _));
    }

    // ─────────────────────────── the in-run path, end to end ───────────────────────────

    /// <summary>A note filed by a LIVE run is acknowledged with the button, and the press writes the
    /// row against the stage that run is on — so it opens its lane at the confirmation of the stage
    /// the owner was watching when they pressed it.</summary>
    [Fact]
    public async Task A_live_run_acknowledges_with_the_button_and_the_press_writes_the_row()
    {
        var surface = Surface(out var channel);

        await surface.HandleNoteAsync(Voice(101), ChatProfile.Admin, CancellationToken.None);

        var ack = channel.Sent[^1];
        var button = Assert.Single(ack.Buttons!);
        Assert.Equal(NotePromoter.ButtonText, button.Text);

        await surface.HandleCallbackAsync(AdminChat, ChatProfile.Admin, button.CallbackData, CancellationToken.None);

        var entry = Assert.Single(FollowupParser.ReadOpenForStage(Followups(_repo), "DV4"));
        Assert.Equal("DV4", entry.OwningStage);
        Assert.Contains(NotePromoter.SourceKey(101), entry.Detail, StringComparison.Ordinal);
        Assert.Contains("FU-NOTE-01", channel.Sent[^1].Text, StringComparison.Ordinal);

        // Pressed again: answered, not written twice.
        await surface.HandleCallbackAsync(AdminChat, ChatProfile.Admin, button.CallbackData, CancellationToken.None);
        Assert.Single(FollowupParser.Read(Followups(_repo)));
        Assert.Contains("Already promoted", channel.Sent[^1].Text, StringComparison.Ordinal);
    }

    /// <summary>KS11.2's rule reaches the new button without being restated in it: a keyboard fans out
    /// to every configured chat, so an observer sees the promote button and pressing it must refuse.</summary>
    [Fact]
    public async Task An_observer_pressing_promote_is_refused_and_writes_nothing()
    {
        var surface = Surface(out var channel);
        await surface.HandleNoteAsync(Voice(102), ChatProfile.Admin, CancellationToken.None);
        var button = Assert.Single(channel.Sent[^1].Buttons!);

        await surface.HandleCallbackAsync(ObserverChat, ChatProfile.Observer, button.CallbackData,
            CancellationToken.None);

        Assert.False(File.Exists(Followups(_repo)));
        Assert.Contains("not part of the observer surface", channel.Sent[^1].Text, StringComparison.Ordinal);
    }

    // ─────────────────────── the courier path, on the real wire ───────────────────────

    /// <summary>The courier's press, through the JSON api.telegram.org actually sends.
    ///
    /// <para>This is the case that did not work at all before DV4.4 and could not have been caught by
    /// asserting against a seam: a <c>callback_query</c> update has no <c>message</c>, the adapter
    /// returned Ignored, and the offset advanced past it. On a machine where the courier owns the
    /// token there is no second consumer that could have picked it up — Telegram permits one — so the
    /// button was decorative and the press was silence.</para></summary>
    [Fact]
    public async Task The_courier_draws_the_button_and_a_press_becomes_a_row_with_no_run_alive()
    {
        using var bot = new RecordingBotApi();
        var settings = Settings(bot, _repo);
        using var source = Source(settings);
        var courier = Daemon(source, settings);

        new ChatRoutes(_stateHome).Set(AdminChat, null, StateHome.SlugFor(_repo, Path.GetFileName(_repo)));
        bot.QueueMessage($$"""{"message_id":7,"chat":{"id":{{AdminChat}}},"text":"the report header double-counts the budget"}""");

        var filed = await courier.PollOnceAsync(CancellationToken.None);
        Assert.Equal(1, filed.Filed);

        var ack = Assert.Single(bot.Snapshot(), c => c.Method == "sendMessage");
        Assert.NotNull(ack.ReplyMarkup);
        Assert.Contains(NotePromoter.CallbackPrefix, ack.ReplyMarkup, StringComparison.Ordinal);
        _out.WriteLine("ack reply_markup: " + ack.ReplyMarkup);

        var data = CallbackDataIn(ack.ReplyMarkup!);
        bot.QueueCallback(AdminChat, data);
        await courier.PollOnceAsync(CancellationToken.None);

        var entry = Assert.Single(FollowupParser.ReadOpenForStage(Followups(_repo), "any-stage-at-all"));
        Assert.Equal(FollowupWriter.UnclaimedStage, entry.OwningStage);
        Assert.Equal("the report header double-counts the budget", entry.Item);
        Assert.Contains("answerCallbackQuery", bot.Snapshot().Select(c => c.Method), StringComparer.Ordinal);
    }

    /// <summary>The courier applies the same profile gate to a press that the surface applies. It has
    /// to state it itself: the surface's gate lives in <c>CommandRouter</c>, which the daemon does not
    /// use, so an observer's press would otherwise be honoured by the one component that is awake when
    /// nobody is watching.</summary>
    [Fact]
    public async Task The_courier_refuses_an_observers_press()
    {
        using var bot = new RecordingBotApi();
        var settings = Settings(bot, _repo);
        using var source = Source(settings);
        var courier = Daemon(source, settings);

        new ChatRoutes(_stateHome).Set(AdminChat, null, StateHome.SlugFor(_repo, Path.GetFileName(_repo)));
        bot.QueueMessage($$"""{"message_id":8,"chat":{"id":{{AdminChat}}},"text":"a real note"}""");
        await courier.PollOnceAsync(CancellationToken.None);

        var data = CallbackDataIn(Assert.Single(bot.Snapshot(), c => c.Method == "sendMessage").ReplyMarkup!);
        bot.QueueCallback(ObserverChat, data);
        await courier.PollOnceAsync(CancellationToken.None);

        Assert.False(File.Exists(Followups(_repo)));
        Assert.Contains(bot.Snapshot(),
            c => c.Method == "sendMessage" && (c.Text ?? "").Contains("observer surface", StringComparison.Ordinal));
    }

    // ────────────────────── the exit: a row that opens a Tier-B lane ──────────────────────

    /// <summary>§1.9 row 6's falsifiable exit, whole: a note promoted in the chat appears as a
    /// followups.md row and OPENS A LANE — a real worktree, a real agent, a real merge gate, merged
    /// back into the primary tree — with the row claimed by the stage that ran it and closed after.
    ///
    /// <para>The row here is not hand-written: it is the one the courier's press produced above, with
    /// no stage on it, which is the case a hand-written row would not have exercised.</para></summary>
    [Fact]
    public async Task A_promoted_row_opens_a_tier_b_fix_lane_and_is_closed_by_it()
    {
        var (repo, cleanup) = TestRepo();
        try
        {
            var followups = Followups(repo);
            Directory.CreateDirectory(Path.GetDirectoryName(followups)!);
            var store = new InboxStore(Path.Combine(repo, ".conductor"));
            store.Append(new InboxNote(4242, new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc),
                AdminChat, "text", "Add a line to README.md describing the project"));

            var promoted = NotePromoter.Promote(store, 4242, stageId: null);
            Assert.Equal(PromotionResult.Promoted, promoted.Result);
            Assert.Equal(FollowupWriter.UnclaimedStage,
                Assert.Single(FollowupParser.Read(followups)).OwningStage);

            var plan = new PlanConfig
            {
                Name = "dv44-promotion-rig",
                Repo = repo,
                Agent = new AgentConfig
                {
                    Command = "cmd",
                    Args =
                    [
                        "/c",
                        "echo Conductor is a stateful AI orchestration tool.>> README.md "
                        + "&& git add README.md && git commit -m fix-from-a-promoted-note",
                    ],
                },
                Gates =
                [
                    new GateConfig
                    {
                        Name = "verify-readme",
                        Command = "if (Select-String -Path README.md -Pattern 'orchestration') { exit 0 } else { exit 1 }",
                        Shell = "powershell",
                        TimeoutMinutes = 1,
                    },
                ],
            };

            var events = new CollectingEventSink();
            var lanes = new LaneCoordinator(plan, new RunState { RunId = "dv44", CurrentStage = "DV6" },
                new RecordingSink(), events, m => _out.WriteLine(m));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await lanes.RunFollowupFixLanesAsync("DV6", cts.Token);

            Assert.Contains(events.Events,
                e => e is MutatingLaneStarted ml && ml.LaneId == "fix-fu-note-01");
            Assert.Contains(events.Events, e => e is MutatingLaneFinished f && f.Outcome == "success");
            Assert.Contains("orchestration", await File.ReadAllTextAsync(Path.Combine(repo, "README.md"), cts.Token),
                StringComparison.OrdinalIgnoreCase);

            // Claimed by the stage that ran it, and closed by the merge — so it fires exactly once.
            var after = Assert.Single(FollowupParser.Read(followups));
            Assert.Equal("DV6", after.OwningStage);
            Assert.StartsWith("CLOSED", after.Status, StringComparison.Ordinal);
            Assert.Empty(FollowupParser.ReadOpenForStage(followups, "DV7"));
        }
        finally { cleanup(); }
    }

    // ───────────────────────────── the refusal, by design ─────────────────────────────

    /// <summary>§1.7's tier table has three rows and promotion moves a note exactly ONE. No path from
    /// a note reaches an injection, and this asserts the absence rather than trusting the reading:
    /// the files that handle an inbound note, file it, promote it and render it into a prompt must
    /// name no injection API at all.</summary>
    [Fact]
    public void No_file_that_handles_an_inbound_note_can_reach_the_injection_api()
    {
        string[] paths =
        [
            "src/Conductor.Core/Integrations/Messaging/RemoteSurface.Inbound.cs",
            "src/Conductor.Core/Integrations/Messaging/RemoteSurface.Routing.cs",
            "src/Conductor.Core/Inbox/NotePromotion.cs",
            "src/Conductor.Core/Inbox/InboxStore.cs",
            "src/Conductor.Core/Inbox/InboxBattery.cs",
            "src/Conductor.Core/Courier/CourierDaemon.cs",
            "src/Conductor.Core/FollowupWriter.cs",
        ];

        string[] forbidden = ["WriteInjection", "SurfaceAction.Inject", "SurfaceAction.ArmInjection", "_injectionArmed"];
        var root = RepoRoot();

        foreach (var rel in paths)
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), rel + " has moved — this test names the files by path on purpose");
            var text = File.ReadAllText(full);

            foreach (var token in forbidden)
                Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }
    }

    /// <summary>The behavioural half of the same claim, and the sharper one: a chat that has ARMED an
    /// injection and then sends a voice note gets a filed note, not an injected instruction. Text and
    /// media take different paths through the surface, and this is the join a refactor would break.</summary>
    [Fact]
    public async Task An_armed_chat_that_sends_a_voice_note_files_it_instead_of_injecting_it()
    {
        var surface = Surface(out var channel);

        await surface.HandleCallbackAsync(AdminChat, ChatProfile.Admin, "inject:needsHuman", CancellationToken.None);
        Assert.Contains("Reply to this message", channel.Sent[^1].Text, StringComparison.Ordinal);

        await surface.HandleNoteAsync(Voice(103), ChatProfile.Admin, CancellationToken.None);

        // Filed, acknowledged, promotable — and no injection was attempted: the surface holds a null
        // store, so an injection here would have answered "Cannot inject: store is not available".
        Assert.NotNull(new InboxStore(Path.Combine(_repo, ".conductor")).Find(103));
        Assert.DoesNotContain("inject", channel.Sent[^1].Text ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Single(channel.Sent[^1].Buttons!);
    }

    /// <summary>The router cannot answer a promote press with anything that steers a run. Pinned at
    /// the decision, not the effect: this is the one place a future button prefix could be added that
    /// made a note reach the control file.</summary>
    [Fact]
    public void A_promote_press_can_only_ever_produce_a_promotion()
    {
        var plan = new PlanConfig { Name = "p", Repo = _repo, Tracker = "TRACKER.md" };
        var state = new RunState { RunId = "r", CurrentStage = "DV4" };
        var router = new CommandRouter(
            new MessageComposer(plan, state, ProgressProviderFactory.Create(plan), null, _ => { }), plan);

        var outcome = router.RouteCallback(NotePromoter.Callback(null, 12), ChatProfile.Admin);

        Assert.Equal(SurfaceAction.Promote, outcome.Action);
        Assert.Null(outcome.ControlAction);
        Assert.False(outcome.Confirmed);
    }

    // ─────────────────────────────────── the rig ───────────────────────────────────

    private static string Followups(string repo) => Path.Combine(repo, ".conductor", "followups.md");

    private RemoteSurface Surface(out FakeChannel channel)
    {
        channel = new FakeChannel();
        var plan = new PlanConfig
        {
            Name = "alpha-repo",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "DV4", Title = "The courier", Sessions = 1 } },
        };
        var state = new RunState { RunId = "dv44", SessionCounter = 13, CurrentStage = "DV4" };
        var composer = new MessageComposer(plan, state, ProgressProviderFactory.Create(plan), null, _ => { });

        return new RemoteSurface(channel, composer, new CommandRouter(composer, plan), state, null,
            (_, _, _) => Task.CompletedTask, (_, _) => { },
            inbox: new InboxStore(Path.Combine(_repo, ".conductor")));
    }

    /// <summary>A voice note as the adapter hands it over — bytes already on disk, which is the state
    /// that makes the ack the sender sees the one a promotion is offered on.</summary>
    private InboundNote Voice(long id)
    {
        var media = Path.Combine(_box, "staging");
        Directory.CreateDirectory(media);
        var path = Path.Combine(media, id.ToString(CultureInfo.InvariantCulture) + "-voice.oga");
        File.WriteAllBytes(path, [0x4F, 0x67, 0x67, 0x53]);

        return new InboundNote(AdminChat, id, "", new InboundMedia(InboundMediaKind.Voice, "f" + id,
            "voice.oga", "audio/ogg", 4, 3, path, null), null, null, null, id);
    }

    private CourierSettings Settings(RecordingBotApi bot, params string[] repos)
    {
        var settings = new CourierSettings
        {
            ApiBaseUrl = bot.Root,
            PollIntervalSeconds = 1,
            Chats = [new CourierChat(AdminChat, "admin"), new CourierChat(ObserverChat, "observer")],
            Projects = [.. repos.Select(r => new CourierProject(Path.GetFileName(r), r))],
        };
        settings.Save(_stateHome);
        return settings;
    }

    private TelegramCourierSource Source(CourierSettings settings) =>
        new(settings, ScratchToken, NullLogger.Instance, _stateHome);

    private CourierDaemon Daemon(ICourierSource source, CourierSettings settings) =>
        new(source, settings, _stateHome, m => _out.WriteLine(m));

    /// <summary>The <c>callback_data</c> the engine drew, read back off the wire rather than
    /// reconstructed — a test that rebuilds the payload would pass against a button that carries a
    /// different one.</summary>
    private static string CallbackDataIn(string replyMarkup)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(replyMarkup);
        return doc.RootElement.GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString()!;
    }

    private static (string Repo, Action Cleanup) TestRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-dv44r-{Guid.NewGuid():N}"[..42]);
        Directory.CreateDirectory(repo);

        Git.Exec(repo, "init", "-b", "main");
        Git.Exec(repo, "config", "user.email", "conductor@test.local");
        Git.Exec(repo, "config", "user.name", "Conductor Test");

        File.WriteAllText(Path.Combine(repo, "README.md"), "# Test Repo\n");
        Git.Exec(repo, "add", "README.md");
        Git.Exec(repo, "commit", "-m", "initial commit");

        void Cleanup()
        {
            try
            {
                var gitDir = Path.Combine(repo, ".git");
                if (Directory.Exists(gitDir))
                    foreach (var f in Directory.GetFiles(gitDir, "*", SearchOption.AllDirectories))
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch (Exception) { }
                TestTemp.DeleteTree(repo);
            }
            catch (Exception) { }
        }

        return (repo, Cleanup);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>A channel that delivers to a list — the KS11.1 fake, kept local so this file has no
    /// dependency on another test class's internals.</summary>
    private sealed class FakeChannel : IMessageChannel
    {
        public string Name => "fake";
        public bool IsLive => true;
        public bool AllowsControl => true;
        public IReadOnlyList<ChatTarget> Targets =>
            [new ChatTarget(AdminChat, ChatProfile.Admin), new ChatTarget(ObserverChat, ChatProfile.Observer)];

        public List<OutboundMessage> Sent { get; } = [];

        public Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task SendAsync(OutboundMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
