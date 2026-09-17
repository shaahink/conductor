using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Budget;
using Conductor.Core.Courier;
using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Core.History;
using Conductor.Core.Inbox;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Money;
using Conductor.Core.Store;
using Conductor.Models;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using MoneyLine = Conductor.Core.Integrations.Messaging.MoneyLine;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// PK5.2 / D10 / findings F-COUR-7 - the figure verbs answered by the courier from <c>run.db</c>,
/// read-only, for the chat's project, with no run live. ADR-0005 (the phone cannot change what the
/// run decides) and ADR-0008 condition 2 (the courier is ingress for notes, never for run state) are
/// what the no-write test below holds: every byte under the state home and the checkout is the same
/// after every verb has been answered.
///
/// <para>Driven against the loopback Bot API stand-in on a scratch state home, a scratch token and
/// scratch chat ids; the store is seeded, closed, and only then asked.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PK5_2CourierFiguresTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string ObserverChat = "-100520000002";
    private const string ScratchToken = "111111:pk52-scratch-token";
    private const string PlanName = "PK52 rig";
    private const string RunId = "pk52-run-0001";

    private readonly string _tmp;
    private readonly string _stateHome;
    private readonly string _repo;
    private readonly string _db;
    private readonly ITestOutputHelper _out;

    public PK5_2CourierFiguresTests(ITestOutputHelper output)
    {
        _out = output;
        _tmp = Path.Combine(Path.GetTempPath(), $"conductor-pk52-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(_tmp, "state-home");
        _repo = Path.Combine(_tmp, "pk52-repo");
        Directory.CreateDirectory(_stateHome);
        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));
        Directory.CreateDirectory(Path.Combine(_repo, "plans", "pk52"));

        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# PK52 rig\n\n## Checkpoints\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + "| PK5.1 | inbound with a name | DONE | 321c73b | pk5.1.md |\n"
            + "| PK5.2 | figures from the store | IN PROGRESS | | |\n");
        File.WriteAllText(Path.Combine(_repo, "plans", "pk52", "rig.plan.json"), JsonSerializer.Serialize(new
        {
            name = PlanName,
            repo = "../..",
            tracker = "TRACKER.md",
            agent = new { command = "cmd.exe", args = new[] { "/c", "echo", "{prompt}" } },
            stages = new[] { new { id = "PK5", title = "Inbound with a name", sessions = 1 } },
        }));

        _db = StateHome.DerivedRunDbPath(_stateHome, _repo, PlanName);
        Directory.CreateDirectory(Path.GetDirectoryName(_db)!);
        using (var store = new SqliteRunStore(_db, NullLogger<SqliteRunStore>.Instance))
        {
            store.SetRunId(RunId);
            store.InitializeRun(RunId, PlanName, _repo, "feat/peyk-courier", EngineStamp.Parse("test"));
            store.RecordSession(RunId, "PK5", 1, "delivery", new DateTime(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc), "Advanced", null, 0, 1,
                "build:OK", "SESSION-RESULT: inbound with a name", 2, "PK5.1");
            store.RecordCost(RunId, 1, "agent", 40_000, 60_000, 0, 6_000_000, 31.40m, 3_600_000);
            store.RecordCost(RunId, 1, "advisor", 2_000, 1_000, 0, 50_000, 1.35m, 9_000);
            store.Emit(new EvidenceRegistered
            {
                RunId = RunId, Path = ".conductor/evidence/PK5/pk5.1.md", Kind = EvidenceKinds.Text, Sha256 = "5eed5eed5eed5eed",
                Bytes = 2048, CheckpointId = "PK5.1", StageId = "PK5", SessionNumber = 1, Source = "claim",
                Ts = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero),
            });
            store.FlushEvents();
            store.SaveRunState(RunId, PlanName, JsonSerializer.Serialize(
                new RunState { RunId = RunId, PlanName = PlanName, CurrentStage = "PK5", SessionCounter = 1, History = { new SessionRecord { Number = 1, Stage = "PK5", CostUsd = 32.75m } } },
                PlanConfig.JsonOpts));
        }

        // A closed store is a closed FILE: the writer's pooled handle would otherwise keep the WAL beside it
        // and read as a run still live.
        SqliteConnection.ClearAllPools();
        StateCatalogue.Upsert(_stateHome, _repo, PlanName, _db);
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_tmp); } catch (Exception) { }
    }

    // -- the rig ---------------------------------------------------------------------------------

    private ProjectRef Project => new(PlanName, _repo, StateHome.SlugFor(_repo, PlanName), Path.Combine(_repo, ".conductor"), true);

    private CourierSettings Settings(RecordingBotApi bot)
    {
        var settings = new CourierSettings
        {
            ApiBaseUrl = bot.Root,
            PollIntervalSeconds = 1,
            Chats = [new CourierChat(AdminChat, "admin"), new CourierChat(ObserverChat, "observer")],
            Projects = [new CourierProject(PlanName, _repo)],
        };
        settings.Save(_stateHome);
        new ChatRoutes(_stateHome).Set(AdminChat, null, StateHome.SlugFor(_repo, PlanName));
        new ChatRoutes(_stateHome).Set(ObserverChat, null, StateHome.SlugFor(_repo, PlanName));
        return settings;
    }

    private (TelegramCourierSource Source, CourierDaemon Daemon) Courier(CourierSettings settings)
    {
        var source = new TelegramCourierSource(settings, ScratchToken, NullLogger.Instance, _stateHome);
        return (source, new CourierDaemon(source, settings, _stateHome, m => _out.WriteLine(m)));
    }

    /// <summary>What the courier said, in order, after one poll.</summary>
    private static async Task<List<BotCall>> AskAsync(RecordingBotApi bot, CourierDaemon courier)
    {
        var before = bot.Snapshot().Count;
        await courier.PollOnceAsync(CancellationToken.None);
        return [.. bot.Snapshot().Skip(before).Where(c => c.Method == "sendMessage")];
    }

    /// <summary>The same reading <c>conductor money</c> takes of this database.</summary>
    private MoneyRun Expected()
    {
        var archive = RunArchive.TryOpen(_db);
        Assert.NotNull(archive);
        var sessions = archive!.Sessions(RunId);
        var costs = archive.Costs(RunId);
        var windows = BudgetAnalyzer.Analyze(RunId, PlanName, sessions, archive.SoftBreaks(RunId)).Windows;
        return MoneyAnalyzer.AnalyzeRun(RunId, PlanName, _repo, null, null, sessions, costs, windows);
    }

    /// <summary>Every file under the scratch root: its bytes, length and write time. Sidecars count - a
    /// <c>-wal</c> or <c>-shm</c> that appears is a file the answer created.</summary>
    private Dictionary<string, string> Tree() =>
        Directory.EnumerateFiles(_tmp, "*", SearchOption.AllDirectories).ToDictionary(
            p => Path.GetRelativePath(_tmp, p),
            p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) + "/" + new FileInfo(p).Length.ToString(CultureInfo.InvariantCulture)
               + "/" + File.GetLastWriteTimeUtc(p).Ticks.ToString(CultureInfo.InvariantCulture),
            StringComparer.OrdinalIgnoreCase);

    // -- D10: the courier answers from the store ----------------------------------------------------

    [Fact]
    public async Task With_no_run_live_money_in_a_rig_chat_answers_the_figures_conductor_money_prints()
    {
        Assert.False(File.Exists(_db + "-wal"), "the rig's store must be closed - no run live");
        using var bot = new RecordingBotApi();
        var (source, courier) = Courier(Settings(bot));
        using var _ = source;
        bot.QueueCommand(AdminChat, "/money");

        var reply = Assert.Single(await AskAsync(bot, courier));
        _out.WriteLine(reply.Text);

        var total = Expected().Total;
        Assert.Equal(AdminChat, reply.ChatId);
        Assert.Contains(MoneyLine.Usd(total.Cost), reply.Text, StringComparison.Ordinal);
        Assert.Contains($"{total.Sessions} session", reply.Text, StringComparison.Ordinal);
        Assert.Contains(MoneyLine.Usd(total.CostPerCheckpoint!.Value) + " per delivered checkpoint", reply.Text, StringComparison.Ordinal);
        Assert.Contains("read-only", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("files notes", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_progress_status_and_evidence_answer_from_the_same_store()
    {
        var total = Expected().Total;
        Assert.EndsWith("rig.plan.json", CourierFigures.PlanFor(Project, _out.WriteLine).PlanFilePath, StringComparison.Ordinal);

        var tokens = CourierFigures.Answer("tokens", "", Project, _stateHome);
        Assert.Contains($"over {total.Sessions} session", tokens, StringComparison.Ordinal);

        var progress = CourierFigures.Answer("progress", "", Project, _stateHome);
        Assert.Contains("PK5", progress, StringComparison.Ordinal);
        Assert.Contains("1/2", progress, StringComparison.Ordinal);

        var status = CourierFigures.Answer("status", "", Project, _stateHome);
        _out.WriteLine(status);
        StatusReport report;
        using (var reader = SqliteRunStore.OpenReadOnly(_db))
            report = StatusReportBuilder.Build(PlanConfig.Load(Path.Combine(_repo, "plans", "pk52", "rig.plan.json")), reader);
        Assert.Equal(2, report.TotalCount);
        Assert.Contains($"Checkpoints {report.DoneCount}/{report.TotalCount}", status, StringComparison.Ordinal);
        Assert.Contains($"sessions {report.SessionCount}", status, StringComparison.Ordinal);
        Assert.Contains(report.Verdict.Split(" ")[0], status, StringComparison.Ordinal);

        var evidence = CourierFigures.Answer("evidence", "", Project, _stateHome);
        Assert.Contains(".conductor/evidence/PK5/pk5.1.md", evidence, StringComparison.Ordinal);
        Assert.Contains("PK5.1", evidence, StringComparison.Ordinal);
        Assert.Contains(".conductor/evidence/PK5/pk5.1.md", CourierFigures.Answer("evidence", "PK5.1", Project, _stateHome), StringComparison.Ordinal);
        Assert.Contains("Nothing is registered against PK5.9", CourierFigures.Answer("evidence", "PK5.9", Project, _stateHome), StringComparison.Ordinal);
    }

    /// <summary>The acceptance's second half: a write to any store is asserted absent. The run.db, the
    /// catalogue, the chat routes, the inbox, the tracker - every file under the scratch root is the
    /// same bytes at the same write time after all five verbs, and no sidecar has appeared.</summary>
    [Fact]
    public void No_figure_verb_writes_to_any_store()
    {
        var before = Tree();

        foreach (var verb in CourierFigures.Verbs)
        {
            var text = CourierFigures.Answer(verb, "", Project, _stateHome);
            _out.WriteLine($"/{verb}: {text.Length} chars");
            Assert.DoesNotContain("nothing to count", text, StringComparison.Ordinal);
        }
        _ = CourierFigures.Answer("evidence", "PK5.1", Project, _stateHome);

        var after = Tree();
        Assert.Equal(before.Keys.Order(StringComparer.OrdinalIgnoreCase), after.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var (path, stamp) in before)
            Assert.True(stamp == after[path], $"{path} changed while the courier answered a figure verb");
        Assert.False(File.Exists(_db + "-wal"));
        Assert.False(File.Exists(_db + "-shm"));
    }

    /// <summary>The courier's own reads, as source: nothing under <c>Core/Courier</c> opens a writable
    /// store or resolves state the writing way. The tree test above is the measurement; this is what
    /// keeps the next verb added here from reaching for the easy constructor.</summary>
    [Fact]
    public void The_courier_sources_never_open_a_writable_store_or_resolve_state()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var sources = Directory.GetFiles(Path.Combine(dir!.FullName, "src", "Conductor.Core", "Courier"), "*.cs");
        Assert.NotEmpty(sources);
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[] { "new SqliteRunStore(", "StateHome.Resolve(", ".ResolveState()", "StateCatalogue.Upsert(" })
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} contains {forbidden} - the courier reads run state, it never writes it (ADR-0008)");
        }
    }

    [Fact]
    public void A_live_run_holding_the_store_is_answered_too()
    {
        using var writer = new SqliteRunStore(_db, NullLogger<SqliteRunStore>.Instance);
        writer.RecordCost(RunId, 2, "agent", 1_000, 1_000, 0, 100_000, 2.00m, 60_000);

        var money = CourierFigures.Answer("money", "", Project, _stateHome);
        Assert.Contains(MoneyLine.Usd(Expected().Total.Cost), money, StringComparison.Ordinal);
        Assert.Contains("$34.75", money, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_with_no_run_recorded_says_so_and_creates_nothing()
    {
        var other = Path.Combine(_tmp, "never-ran");
        Directory.CreateDirectory(other);
        var before = Tree();

        var text = CourierFigures.Answer("money", "", new ProjectRef("never ran", other, "never-ran", Path.Combine(other, ".conductor"), true), _stateHome);

        Assert.Contains("No run of <b>never ran</b> is recorded", text, StringComparison.Ordinal);
        Assert.Equal(before.Count, Tree().Count);
    }

    // -- who may ask: the surface's rule, as the list grows --------------------------------------------

    /// <summary>Property over <see cref="CourierFigures.Verbs"/>: each is a Browse verb of the surface,
    /// and each is ANSWERED for an observer chat - which may not file a note - rather than refused or
    /// handed the "courier files notes" line. A verb added to the list without either fails here.</summary>
    [Fact]
    public async Task Every_figure_verb_is_a_browse_verb_an_observer_may_ask()
    {
        using var bot = new RecordingBotApi();
        var (source, courier) = Courier(Settings(bot));
        using var _ = source;

        foreach (var verb in CourierFigures.Verbs)
        {
            var surface = SurfaceCommands.Find("/" + verb);
            Assert.NotNull(surface);
            Assert.Equal(SurfaceScope.Browse, surface!.Scope);

            bot.QueueCommand(ObserverChat, "/" + verb);
            var reply = Assert.Single(await AskAsync(bot, courier));
            _out.WriteLine($"/{verb} -> {reply.Text?.Split('\n')[0]}");
            Assert.Equal(ObserverChat, reply.ChatId);
            Assert.DoesNotContain("files notes", reply.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(InboundAck.NotYours(ChatProfile.Observer), reply.Text, StringComparison.Ordinal);
            Assert.Contains("read-only", reply.Text, StringComparison.Ordinal);
        }

        // ...and the observer still may not file: the read went first, the gate did not move.
        bot.QueueCommand(ObserverChat, "a sentence that would be a note");
        var refusal = Assert.Single(await AskAsync(bot, courier));
        Assert.Contains(InboundAck.NotYours(ChatProfile.Observer), refusal.Text, StringComparison.Ordinal);
        Assert.Empty(new InboxStore(Path.Combine(_repo, ".conductor")).All());
    }
}
