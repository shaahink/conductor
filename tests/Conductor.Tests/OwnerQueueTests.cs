using Conductor.Http;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;
using CheckpointRow = Conductor.Core.CheckpointRow;

namespace Conductor.Tests;

/// <summary>
/// SF4.1 — the owner queue. Two properties matter more than the individual entries: every source is
/// DERIVED from live state (so an entry cannot outlive its condition), and every entry carries what
/// it unblocks plus the command that clears it. The clearing half is asserted in pairs — collect,
/// change the one piece of state that resolves it, collect again and watch the entry go — because
/// that is precisely what the hand-written SHAHIN.md could not do.
/// </summary>
public sealed class OwnerQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-ownerqueue-{Guid.NewGuid():N}");
    private readonly PlanConfig _plan;
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    public OwnerQueueTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, ".conductor"));
        _plan = new PlanConfig
        {
            Name = "owner-queue-test",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Stages =
            [
                new StageConfig { Id = "S1", Title = "Stage One", Sessions = 1 },
                new StageConfig { Id = "S2", Title = "Ship it", Sessions = 1, OwnerGate = true },
            ],
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static TrackerSnapshot Track(string handoff = "", params CheckpointRow[] rows)
        => new() { HandoffBlock = handoff, Checkpoints = [.. rows] };

    // ---- nothing owed ---------------------------------------------------------------------------

    [Fact]
    public void Collect_IsEmpty_WhenTheRunIsRunningAndNobodyAskedForAnything()
    {
        var state = new RunState { Status = RunStatus.Running, CurrentStage = "S1" };
        var items = OwnerQueue.Collect(_plan, state, Track("last: all good\nnext: S1.2"), Now);

        // S2 is owner-gated and unapproved, so the queue is not empty — but it must say "ahead", not
        // "now". Anything ranked as blocking here would cry wolf on every run with a gate in it.
        Assert.All(items, i => Assert.Equal("ownerGate", i.Kind));
        Assert.Contains("ahead of the run", Assert.Single(items).Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_SaysNothingIsWaiting_OutLoud_WhenTheQueueIsEmpty()
    {
        var plan = new PlanConfig { Name = "p", Repo = _dir, Tracker = "TRACKER.md" };
        var md = OwnerQueue.Render(plan, new RunState { Status = RunStatus.Running }, [], Now);

        // A file that just stops after the header reads identically to a stale one.
        Assert.Contains("Nothing is waiting on you", md, StringComparison.Ordinal);
        Assert.Contains("rewritten every", md, StringComparison.Ordinal);
    }

    // ---- HUMAN: lines ---------------------------------------------------------------------------

    [Fact]
    public void Collect_TakesHumanLinesFromTheHandoff_EvenBulleted_AndClearsWhenTheLineGoes()
    {
        var state = new RunState
        {
            Status = RunStatus.NeedsHuman,
            CurrentStage = "S1",
            AttentionSinceUtc = Now.AddHours(-2),
        };
        var handoff = "last: landed the parser\n- **HUMAN:** which registry do we publish to?\nnext: S1.3\n";

        var before = OwnerQueue.Collect(_plan, state, Track(handoff), Now);
        var human = Assert.Single(before, i => i.Kind == "human");
        Assert.Equal("which registry do we publish to?", human.Title);
        Assert.Equal("conductor resume", human.Command);
        Assert.Contains("S1", human.Unblocks, StringComparison.Ordinal);
        Assert.Equal(7200, human.AgeSeconds(Now));
        Assert.Contains("TRACKER.md", human.Detail, StringComparison.Ordinal);

        // Resolve it the way the entry says to: take the line out and resume.
        state.Status = RunStatus.Running;
        state.AttentionSinceUtc = null;
        var after = OwnerQueue.Collect(_plan, state, Track("last: landed the parser\nnext: S1.3\n"), Now);
        Assert.DoesNotContain(after, i => i.Kind == "human");
    }

    [Fact]
    public void Collect_HonoursThePlansOwnHumanToken()
    {
        _plan.Conventions.HumanToken = "OWNER?:";
        var state = new RunState { Status = RunStatus.NeedsHuman, CurrentStage = "S1" };

        var items = OwnerQueue.Collect(_plan, state, Track("OWNER?: pick a domain name"), Now);

        var human = Assert.Single(items, i => i.Kind == "human");
        Assert.Equal("pick a domain name", human.Title);
        // The park entry must not ALSO fire: one obligation, one line.
        Assert.DoesNotContain(items, i => i.Kind == "park");
    }

    // ---- owner gates ----------------------------------------------------------------------------

    [Fact]
    public void Collect_RanksAParkedOwnerGateAboveAWait_AndTheApprovalClearsIt()
    {
        var state = new RunState
        {
            Status = RunStatus.AwaitingOwner,
            AwaitingOwnerReason = AwaitingOwnerReason.OwnerGate,
            CurrentStage = "S2",
            AttentionSinceUtc = Now.AddMinutes(-30),
            BlockedUntilUtc = Now.AddMinutes(20),
            BlockedSinceUtc = Now.AddMinutes(-5),
            BlockedReason = "rate-limit window",
        };
        state.SetAttention("owner gate on S2", Now.AddMinutes(-30));

        var before = OwnerQueue.Collect(_plan, state, Track(), Now);
        var gate = Assert.Single(before, i => i.Kind == "ownerGate");
        Assert.Equal("conductor approve", gate.Command);
        Assert.Contains("parked on it now", gate.Title, StringComparison.Ordinal);
        Assert.Equal(1800, gate.AgeSeconds(Now));
        Assert.Contains("S2", gate.Unblocks, StringComparison.Ordinal);
        // The gate is the specific statement of this park — the generic park entry must stand down.
        Assert.DoesNotContain(before, i => i.Kind == "park");
        var order = before.ToList();
        Assert.True(order.IndexOf(gate) < order.FindIndex(i => i.Kind == "wait"));

        state.OwnerApprovedStages.Add("S2");
        state.Status = RunStatus.Running;
        var after = OwnerQueue.Collect(_plan, state, Track(), Now);
        Assert.DoesNotContain(after, i => i.Kind == "ownerGate");
    }

    // ---- parks ----------------------------------------------------------------------------------

    [Fact]
    public void Collect_ReportsABudgetParkWithItsReasonAgeAndApproveCommand()
    {
        var state = new RunState { Status = RunStatus.AwaitingOwner, AwaitingOwnerReason = AwaitingOwnerReason.Budget, CurrentStage = "S1" };
        state.SetAttention("run cost $12.40 over maxRunCostUsd $10.00", Now.AddMinutes(-90));

        var park = Assert.Single(OwnerQueue.Collect(_plan, state, Track(), Now), i => i.Kind == "park");

        Assert.Equal("run cost $12.40 over maxRunCostUsd $10.00", park.Title);
        Assert.Equal("conductor approve", park.Command);
        Assert.Equal(5400, park.AgeSeconds(Now));
        Assert.Contains("budget window", park.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Collect_DropsTheParkEntry_WhenTheRunIsResumed()
    {
        var state = new RunState { Status = RunStatus.Paused, CurrentStage = "S1" };
        state.SetAttention("paused by the operator", Now.AddMinutes(-3));
        Assert.Contains(OwnerQueue.Collect(_plan, state, Track(), Now), i => i.Kind == "park");

        state.Status = RunStatus.Running;
        state.SetAttention(null);
        Assert.DoesNotContain(OwnerQueue.Collect(_plan, state, Track(), Now), i => i.Kind == "park");
    }

    // ---- blocked-until wait ---------------------------------------------------------------------

    [Fact]
    public void Collect_ShowsALiveWaitWithNoCommand_AndDropsItOnceTheWindowOpens()
    {
        var state = new RunState
        {
            Status = RunStatus.Waiting,
            BlockedUntilUtc = Now.AddMinutes(42),
            BlockedSinceUtc = Now.AddMinutes(-8),
            BlockedReason = "vercel deploy window 100/100",
        };

        var wait = Assert.Single(OwnerQueue.Collect(_plan, state, Track(), Now), i => i.Kind == "wait");
        // `conductor resume` does NOT clear a wait — the loop re-checks the clock after the park
        // check (RunLoop.cs) — so the entry must not pretend a command exists.
        Assert.Equal("", wait.Command);
        Assert.Contains("12:42:00Z", wait.Title, StringComparison.Ordinal);
        Assert.Equal(480, wait.AgeSeconds(Now));

        Assert.DoesNotContain(OwnerQueue.Collect(_plan, state, Track(), Now.AddHours(1)), i => i.Kind == "wait");
    }

    // ---- blocked checkpoints and skipped stages ---------------------------------------------------

    [Fact]
    public void Collect_ListsABlockedCheckpoint_AndTheCommandThatPutsItBack()
    {
        var blocked = new CheckpointRow("S1.2", "Wire the payment provider", "BLOCKED", "", "");
        var state = new RunState { Status = RunStatus.Running, CurrentStage = "S1" };

        var item = Assert.Single(OwnerQueue.Collect(_plan, state, Track("", blocked), Now), i => i.Kind == "checkpoint");

        Assert.Equal("conductor task --todo S1.2", item.Command);
        Assert.Contains("stage S1", item.Unblocks, StringComparison.Ordinal);
        // The tracker's markdown rows carry no timestamp; the queue must say so rather than
        // inventing "just now" out of a null.
        Assert.Null(item.AgeSeconds(Now));

        var unblocked = new CheckpointRow("S1.2", "Wire the payment provider", "TODO", "", "");
        Assert.DoesNotContain(OwnerQueue.Collect(_plan, state, Track("", unblocked), Now), i => i.Kind == "checkpoint");
    }

    [Fact]
    public void Collect_ListsASkippedStage_BecauseNothingElseEverComesBackToIt()
    {
        var state = new RunState { Status = RunStatus.Running, CurrentStage = "S2" };
        state.SkippedStages.Add("S1");

        var items = OwnerQueue.Collect(_plan, state, Track(), Now);
        var skipped = Assert.Single(items, i => i.Kind == "skippedStage");

        Assert.Equal("conductor goto S1", skipped.Command);
        Assert.Contains("Stage One", skipped.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Collect_DoesNotAskForApprovalOnAStageTheOperatorSkipped()
    {
        var state = new RunState { Status = RunStatus.Running, CurrentStage = "S1" };
        state.SkippedStages.Add("S2");

        var items = OwnerQueue.Collect(_plan, state, Track(), Now);

        Assert.DoesNotContain(items, i => i.Kind == "ownerGate");
        Assert.Contains(items, i => i.Kind == "skippedStage");
    }

    // ---- ordering, rendering, the file ------------------------------------------------------------

    [Fact]
    public void Collect_OrdersByUrgency_ParkThenHumanThenGateThenWaitThenOwedWork()
    {
        var state = new RunState
        {
            Status = RunStatus.Paused,
            CurrentStage = "S1",
            BlockedUntilUtc = Now.AddMinutes(10),
        };
        state.SetAttention("paused by the operator", Now.AddMinutes(-1));
        state.SkippedStages.Add("S3");
        var track = Track("HUMAN: choose a licence", new CheckpointRow("S1.4", "t", "BLOCKED", "", ""));

        var kinds = OwnerQueue.Collect(_plan, state, track, Now).Select(i => i.Kind).ToList();

        Assert.Equal(["park", "human", "wait", "checkpoint", "skippedStage", "ownerGate"], kinds);
    }

    [Fact]
    public void Render_GivesEveryEntryAnUnblocksAnAgeAndACommand()
    {
        var state = new RunState { Status = RunStatus.NeedsHuman, CurrentStage = "S1", AttentionSinceUtc = Now.AddMinutes(-45) };
        var items = OwnerQueue.Collect(_plan, state, Track("HUMAN: choose a licence"), Now);

        var md = OwnerQueue.Render(_plan, state, items, Now);

        Assert.Contains("# Owner queue — owner-queue-test", md, StringComparison.Ordinal);
        Assert.Contains("2 items need you", md, StringComparison.Ordinal);
        Assert.Contains("### 1. choose a licence", md, StringComparison.Ordinal);
        Assert.Contains("**Clears with:** `conductor resume`", md, StringComparison.Ordinal);
        Assert.Contains("**Age:** 45m (since 2026-08-01 11:15:00Z)", md, StringComparison.Ordinal);
        // The unowned-age case is stated, not faked.
        Assert.Contains("**Unblocks:**", md, StringComparison.Ordinal);
        // And the one entry with no command says the truth about itself.
        var wait = OwnerQueue.Render(_plan,
            new RunState { Status = RunStatus.Waiting, BlockedUntilUtc = Now.AddMinutes(5) },
            OwnerQueue.Collect(_plan, new RunState { Status = RunStatus.Waiting, BlockedUntilUtc = Now.AddMinutes(5) }, Track(), Now),
            Now);
        Assert.Contains("nothing to type — it clears itself", wait, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_PutsOwnerQueueMdInTheStateDir_AndRewritesItInPlace()
    {
        var state = new RunState { Status = RunStatus.Paused, CurrentStage = "S1" };
        state.SetAttention("paused by the operator", Now.AddMinutes(-2));

        OwnerQueue.Write(_plan, state, Track(), _ => { }, Now);
        var path = OwnerQueue.QueuePath(_plan);
        Assert.True(File.Exists(path), $"expected {path}");
        Assert.Contains("paused by the operator", File.ReadAllText(path), StringComparison.Ordinal);

        // Regenerated, not appended: the resolved park must not survive in the file.
        state.Status = RunStatus.Running;
        state.SetAttention(null);
        OwnerQueue.Write(_plan, state, Track(), _ => { }, Now);
        Assert.DoesNotContain("paused by the operator", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Reporter_WritesTheOwnerQueueAlongsideTheReport()
    {
        var state = new RunState { RunId = "r1", Status = RunStatus.NeedsHuman, CurrentStage = "S1" };
        state.SetAttention("agent asked for a human", Now.AddMinutes(-10));

        Reporter.WriteReport(_plan, state, Track("HUMAN: approve the schema change"), null, _ => { });

        var queue = File.ReadAllText(OwnerQueue.QueuePath(_plan));
        Assert.Contains("approve the schema change", queue, StringComparison.Ordinal);
    }

    // ---- the wire ---------------------------------------------------------------------------------

    [Fact]
    public async Task GetOwnerQueue_ServesTheSameEntriesWithAgesAndCommands()
    {
        var stateDir = _plan.StateDir;
        await File.WriteAllTextAsync(Path.Combine(_dir, "TRACKER.md"),
            "| id | title | status | commit | evidence |\n|---|---|---|---|---|\n" +
            "| S1.2 | Wire the provider | BLOCKED | | |\n\n" +
            "## Handoff\n\nHUMAN: choose a licence\n").ConfigureAwait(true);
        using var store = new SqliteRunStore(Path.Combine(stateDir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        store.SetRunId("run-owner-queue");

        var state = new RunState { RunId = "run-owner-queue", Status = RunStatus.NeedsHuman, CurrentStage = "S1" };
        state.SetAttention("agent asked for a human in the tracker handoff", DateTime.UtcNow.AddMinutes(-15));

        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var probe = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        using var server = new ControlPlaneServer(_plan, state, store, new ConcurrentQueue<ControlCommand>(),
            new NoOpTelegramService(), NullLogger.Instance, probe);
        Assert.True(server.Start(), "control plane failed to bind");

        using var http = new HttpClient();
        var body = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/owner/queue");

        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(items.Count, doc.RootElement.GetProperty("count").GetInt32());

        var human = items.Find(i => i.GetProperty("kind").GetString() == "human");
        Assert.Equal("choose a licence", human.GetProperty("title").GetString());
        Assert.Equal("conductor resume", human.GetProperty("command").GetString());
        Assert.InRange(human.GetProperty("ageSeconds").GetInt64(), 890, 910);
        Assert.Contains("S1", human.GetProperty("unblocks").GetString(), StringComparison.Ordinal);

        var blocked = items.Find(i => i.GetProperty("kind").GetString() == "checkpoint");
        Assert.Equal("conductor task --todo S1.2", blocked.GetProperty("command").GetString());
        // A source with no timestamp reports null, never 0 — a face must be able to tell them apart.
        Assert.Equal(JsonValueKind.Null, blocked.GetProperty("ageSeconds").ValueKind);
    }
}
