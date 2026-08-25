using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Store;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// DV2.4 — what a restarting engine finds in the store. Two field defects meet on this one path.
///
/// <para><b>Bug #68.</b> Budget counters restarted at zero on every engine process start, which made
/// <c>limits.maxRunCostUsd</c> a per-PROCESS cap rather than a per-run one: a run stopped and
/// restarted enough times could spend without bound. Measured after a <c>--once</c> exit —
/// <c>run_state</c> held cost and tokens as literal 0 while <c>overheadCostUsd</c> carried sub-cents,
/// which is why "restored budget: $0.00 …" printed at all.</para>
///
/// <para><b>Bug #71</b> (recovered karvan #27). A brand-new <c>run.db</c> logged
/// <c>FOREIGN KEY constraint failed</c> on the first <c>run_state</c> write, because <c>run_state</c>
/// references <c>runs(run_id)</c> and the loop saved state before it initialised the run. The error
/// was swallowed and the write was LOST. This asserts the consequence rather than the log line: on a
/// database that has never been written, the first state a run saves is readable back.</para>
/// </summary>
public sealed partial class HarnessTests
{
    [Fact]
    public async Task OnceExit_LeavesTheBudgetInTheStore_AndTheFirstStateWriteOnAFreshDbSurvives()
    {
        var plan = new PlanConfig
        {
            Name = "BudgetPersistencePlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", _agentScript, "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };

        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);
        Assert.Equal(0, code);

        // The session was billed, and the live counters saw it.
        Assert.Single(state.History);
        Assert.True(state.PerRunCostUsd > 0, $"in-memory PerRunCostUsd={state.PerRunCostUsd}");
        Assert.True(state.PerRunTokens > 0, $"in-memory PerRunTokens={state.PerRunTokens}");

        // Bug #71: this database was created by this run. If the first run_state write had been
        // rejected by the foreign key there would be nothing to read.
        var json = host.Services.GetRequiredService<IRunStore>().LoadRunStateJson(state.RunId);
        Assert.False(string.IsNullOrWhiteSpace(json), "the store holds no run_state for this run at all");

        // Bug #68: and what it holds is what a restart restores. RunContext.RestoreBudget reads
        // exactly these four fields off the state it loaded, so zero here IS the per-process cap.
        var persisted = JsonSerializer.Deserialize<RunState>(json!, PlanConfig.JsonOpts);
        Assert.NotNull(persisted);
        Assert.True(persisted!.PerRunCostUsd > 0,
            $"a --once exit persisted PerRunCostUsd={persisted.PerRunCostUsd} — a restart would resume from zero");
        Assert.True(persisted.PerRunTokens > 0,
            $"a --once exit persisted PerRunTokens={persisted.PerRunTokens} — a restart would resume from zero");
        Assert.Equal(state.PerRunCostUsd, persisted.PerRunCostUsd);
        Assert.Equal(state.PerRunTokens, persisted.PerRunTokens);
    }

    /// <summary>
    /// DV2.4, FU-F1-06 — the "immortal running" record. <c>runs.status</c> was written twice in a
    /// run's life: <c>running</c> at every process start and a terminal word at completion, so a run
    /// that stopped <c>Paused</c> or <c>NeedsHuman</c> — the two commonest ways a run stops — said
    /// <c>running</c> for ever, and the row is what every other machine reads.
    ///
    /// <para>KS0.2 closed it with <c>IRunStore.UpdateRunStatus</c> and a call from
    /// <c>RunContext.Save</c>, and <c>KS0_2RunRecordTests</c> pins the writer and the vocabulary. What
    /// nothing pinned is the ROUTE: that a real engine parking a real run actually reaches the writer.
    /// This drives the park end to end and reads the row back.</para>
    /// </summary>
    [Fact]
    public async Task ARunParkedByTheEngine_ReadsBackAsParkedFromTheStore_NotAsRunning()
    {
        var plan = new PlanConfig
        {
            Name = "ParkedStatusPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", _agentScript, "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };

        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0, StartPaused: true), consoleSink: false);

        var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (state.Status != RunStatus.Paused && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(RunStatus.Paused, state.Status);

        var store = host.Services.GetRequiredService<SqliteRunStore>();
        var deadline2 = DateTime.UtcNow.AddSeconds(10);
        IReadOnlyDictionary<string, object?>? row = null;
        while (DateTime.UtcNow < deadline2)
        {
            row = store.Query($"SELECT status, ended_utc FROM runs WHERE run_id = '{state.RunId}'").FirstOrDefault();
            if (row is not null && (row["status"] as string) == "paused") break;
            await Task.Delay(100);
        }

        Assert.NotNull(row);
        Assert.Equal("paused", row!["status"]);
        // A park is not an ending: a resumable run may not carry an ended_utc.
        Assert.True(row["ended_utc"] is null or DBNull, $"a parked run was stamped as ended: {row["ended_utc"]}");

        host.Services.GetRequiredService<System.Collections.Concurrent.ConcurrentQueue<ControlCommand>>()
            .Enqueue(ControlCommand.Of(ControlAction.ResumeRun));
        Assert.Equal(0, await runTask.WaitAsync(TimeSpan.FromSeconds(60)));
    }
}
