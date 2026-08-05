using Conductor.Core;
using Conductor.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// K4.1 — the context measurement driven through the WHOLE engine, not asserted off a parser.
///
/// <para>A unit test on <c>ClaudeProvider</c> proves the arithmetic; it proves nothing about whether
/// the number survives the four hops between the stream and the database — stream state, session
/// record, RecordSession, run.db. Every one of those has silently dropped a token field in this
/// repo's history (B13.6 is the one where live cache-read stayed NULL for a whole session and the
/// rails read zero). So this drives a real orchestrator against a real temp repo with a fake agent
/// whose turns have a deliberately uneven context profile, and reads the answer back out of the
/// database the run actually wrote.</para>
/// </summary>
public sealed partial class HarnessTests
{
    /// <summary>Three API calls with a growing prefix — 5,350 / 20,120 / 40,090 tokens of context —
    /// so a high water, a mean and a turn count are all distinguishable from each other and from the
    /// 65,970-token integral the three add up to.</summary>
    private static string ContextAgentScript() => string.Join("\r\n",
        "@echo off",
        "echo {\"type\":\"step_start\"}",
        "echo {\"type\":\"text\",\"part\":{\"text\":\"Delivering harness checkpoint H0.1.\"}}",
        "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":350,\"output\":40,\"cache\":{\"read\":5000}}}}",
        "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0002,\"tokens\":{\"input\":120,\"output\":30,\"cache\":{\"read\":20000}}}}",
        "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0003,\"tokens\":{\"input\":90,\"output\":20,\"cache\":{\"read\":40000}}}}",
        "echo {\"type\":\"text\",\"part\":{\"text\":\"Session complete.\"}}",
        "echo harness done> context-output.txt",
        "git add context-output.txt",
        "git commit -m \"feat: deliver context checkpoint\"",
        "exit /b 0",
        "");

    [Fact]
    public async Task FullCycle_RecordsHowFullTheContextWindowRan()
    {
        var script = Path.Combine(_repo, "context-agent.cmd");
        await File.WriteAllTextAsync(script, ContextAgentScript());

        var plan = new PlanConfig
        {
            Name = "ContextPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", script, "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        Assert.Equal(0, await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None));

        // 1. The session record carries the profile the stream measured.
        var session = Assert.Single(state.History);
        Assert.NotNull(session.Context);
        Assert.Equal(3, session.Context!.Turns);
        Assert.Equal(40_090, session.Context!.HighWaterTokens);
        Assert.Equal((5_350 + 20_120 + 40_090) / 3, session.Context!.MeanTurnTokens);

        // 2. And it is a different fact from the integral, which is what the engine recorded before.
        Assert.Equal(350 + 120 + 90, session.TokensInput);
        Assert.Equal(65_000, session.TokensCacheRead);
        Assert.True(session.TokensTotal > session.Context!.HighWaterTokens);

        // 3. It survived the write. This is the hop unit tests cannot see.
        var store = (SqliteRunStore)host.Services.GetRequiredService<IRunStore>();
        var rows = store.Query(
            "SELECT context_high_water, context_mean_turn, context_turns FROM sessions " +
            "WHERE run_id = @runId AND number = 1", ("@runId", state.RunId));
        var row = Assert.Single(rows);
        Assert.Equal(40_090L, Convert.ToInt64(row["context_high_water"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(21_853L, Convert.ToInt64(row["context_mean_turn"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(3L, Convert.ToInt64(row["context_turns"], System.Globalization.CultureInfo.InvariantCulture));
    }
}
