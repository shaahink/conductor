using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// W1.3 truth gates (Category=Integration) — the U-series incident shape, re-run against the fix:
/// a fake agent that claims ONLY through the graph (`conductor task --done`'s exact code path)
/// and never touches the tracker gets <c>newlyDone = [the item]</c>; a tracker-only hand-flip is
/// accepted via the LOUD transition fallback (ledgered); and a post-advance Verify session is
/// dispatched against the stage that DELIVERED, not the loop's next stage (bug #6).
/// </summary>
public sealed class W1ClaimPathTests
{
    private static string Tracker(params string[] rows) =>
        "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
        + string.Join("\n", rows) + "\n";

    private static async Task<string> ScaffoldRepoAsync(string repo, string trackerBody, string agentScriptBody)
    {
        Directory.CreateDirectory(repo);
        ProcResult Git(string args) => ProcessRunner.Run("git",
            args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
            TimeSpan.FromSeconds(30), CancellationToken.None);
        Git("init -b main");
        Git("config user.email w13@test");
        Git("config user.name W13");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r");
        Git("add README.md");
        Git("commit -m init --no-gpg-sign");
        await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"), trackerBody);
        var agentScript = Path.Combine(repo, "fake-agent.cmd");
        await File.WriteAllTextAsync(agentScript, agentScriptBody);
        return agentScript;
    }

    private static string SleepyAgentScript() => string.Join("\r\n",
        "@echo off",
        "echo {\"type\":\"text\",\"part\":{\"text\":\"working...\"}}",
        "ping -n 6 127.0.0.1 >nul",
        "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
        "exit /b 0",
        "");

    private static async Task<PlanConfig> WritePlanAsync(string repo, string agentScript, params StageConfig[] stages)
    {
        var planPath = Path.Combine(repo, "test.plan.json");
        var seed = new PlanConfig
        {
            Name = "w13-live",
            Repo = repo.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Stages = [.. stages],
            Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", agentScript, "{prompt}"], Provider = "opencode" },
            Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
        };
        seed.Report.Commit = false;
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return PlanConfig.Load(planPath);
    }

    /// <summary>Poll the run's event log until this session's SessionStarted lands, then make the
    /// claim exactly the way `conductor task --done` does: a SECOND SqliteRunStore on the same
    /// run.db emitting the done-status graph event with agent provenance.</summary>
    private static async Task ClaimDuringSessionAsync(string repo, IRunStore engineStore, RunState state,
        int sessionNumber, string checkpointId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (engineStore.ReadAllEvents(state.RunId).OfType<SessionStarted>().Any(s => s.Number == sessionNumber))
                break;
            await Task.Delay(50, CancellationToken.None);
        }
        using var cli = new SqliteRunStore(Path.Combine(repo, ".conductor", "run.db"),
            NullLogger<SqliteRunStore>.Instance);
        cli.UpdateCheckpoint(state.RunId, checkpointId, "DONE", "fake1234", "claimed via task --done", source: "agent");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GraphOnlyClaim_NoTrackerTouch_YieldsNewlyDone()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w13a-{Guid.NewGuid():N}");
        try
        {
            var agentScript = await ScaffoldRepoAsync(repo, Tracker("| H0.1 | the item | TODO | | |"), SleepyAgentScript());
            var plan = await WritePlanAsync(repo, agentScript, new StageConfig { Id = "H0", Title = "Delivered", Sessions = 1 });

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);

            await ClaimDuringSessionAsync(repo, host.Services.GetRequiredService<IRunStore>(), state, 1, "H0.1");
            var code = await runTask.WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(0, code);

            // The U-series incident inverted: the graph claim IS the signal — no tracker edit, no
            // "newly DONE []", no fallback flag.
            var rec = Assert.Single(state.History);
            Assert.Equal(SessionOutcome.Advanced, rec.Outcome);
            Assert.Equal(["H0.1"], rec.NewlyDone);
            var log = await File.ReadAllTextAsync(Path.Combine(repo, ".conductor", "conductor.log"));
            Assert.DoesNotContain("transition fallback", log, StringComparison.OrdinalIgnoreCase);

            // The tracker view caught up from the graph (TrackerGenerator is its only writer).
            var tracker = await File.ReadAllTextAsync(Path.Combine(repo, "TRACKER.md"));
            Assert.Contains("DONE", tracker, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TrackerOnlyHandFlip_IsAcceptedViaTheLoudFallback()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w13b-{Guid.NewGuid():N}");
        try
        {
            // The fake agent's ONLY report is an old-habit hand-edit of the tracker markdown.
            var flipScript = Path.Combine(repo, "flip.ps1");
            var agentBody = string.Join("\r\n",
                "@echo off",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"editing the tracker by hand...\"}}",
                $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{flipScript}\"",
                "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
                "exit /b 0",
                "");
            await ScaffoldRepoAsync(repo, Tracker("| H0.1 | the item | TODO | | |"), agentBody);
            await File.WriteAllTextAsync(flipScript,
                $"(Get-Content -Raw '{Path.Combine(repo, "TRACKER.md")}') -replace 'TODO', 'DONE' | " +
                $"Set-Content -Encoding utf8 '{Path.Combine(repo, "TRACKER.md")}'\r\n");
            var plan = await WritePlanAsync(repo, Path.Combine(repo, "fake-agent.cmd"),
                new StageConfig { Id = "H0", Title = "Delivered", Sessions = 1 });

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
            var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(0, code);

            // Old habits degrade GRACEFULLY, not silently: the flip counts, flagged and ledgered.
            var rec = Assert.Single(state.History);
            Assert.Equal(["H0.1"], rec.NewlyDone);
            var log = await File.ReadAllTextAsync(Path.Combine(repo, ".conductor", "conductor.log"));
            Assert.Contains("transition fallback", log, StringComparison.OrdinalIgnoreCase);
            var store = host.Services.GetRequiredService<IRunStore>();
            Assert.Contains(store.QueryLedger(state.RunId), l => l.Kind == "legacy-claim");
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PostAdvanceVerify_IsDispatchedAgainstTheDeliveredStage()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w13c-{Guid.NewGuid():N}");
        try
        {
            var agentScript = await ScaffoldRepoAsync(repo,
                Tracker("| H0.1 | delivered item | TODO | | |", "| H1.1 | future item | TODO | | |"),
                SleepyAgentScript());
            var plan = await WritePlanAsync(repo, agentScript,
                new StageConfig { Id = "H0", Title = "Delivered Stage", Sessions = 1 },
                new StageConfig { Id = "H1", Title = "Next Stage", Sessions = 1 });

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 2), consoleSink: false);
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);

            // Session 1 delivers H0 and claims its only item through the graph → Advanced → the
            // workflow queues a Verify; the loop's stage moves on to H1 before it runs.
            await ClaimDuringSessionAsync(repo, host.Services.GetRequiredService<IRunStore>(), state, 1, "H0.1");
            var code = await runTask.WaitAsync(TimeSpan.FromSeconds(180));
            Assert.Equal(0, code);

            Assert.Equal(SessionOutcome.Advanced, state.History[0].Outcome);

            // Bug #6's truth gate: the verify session is dispatched against the DELIVERED stage.
            var store = host.Services.GetRequiredService<IRunStore>();
            var starts = store.ReadAllEvents(state.RunId).OfType<SessionStarted>().ToList();
            var verify = Assert.Single(starts, s => s.Kind == "Verify");
            Assert.Equal(2, verify.Number);
            Assert.Equal("H0", verify.StageId);

            var prompt = await File.ReadAllTextAsync(
                Path.Combine(repo, ".conductor", "logs", "session-002.prompt.md"));
            Assert.Contains("H0", prompt, StringComparison.Ordinal);
            Assert.Contains("Delivered Stage", prompt, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch (IOException) { }
        }
    }
}
