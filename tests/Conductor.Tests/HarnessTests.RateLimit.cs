using Conductor.Core;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// DV2.4, bug #69 — a backend rate limit must park the run, not spend its attempts.
///
/// <para>Measured 2026-08-15, run <c>9647f1b8</c>, in this repo's own log
/// (<c>.conductor/logs/conductor-20260815.log:2154-2280</c>): <c>session #N exited (code 1, 0m,
/// $0.00)</c> every ~19 seconds, NO "usage limit detected" line anywhere, attempts 2→8 gone in three
/// minutes, the circuit breaker firing on "identical failure pattern (AgentError ×2)", the advisor
/// 429ing as well, and the stage ending NEEDS HUMAN over an account limit that would have cleared
/// itself.</para>
///
/// <para>The seam: the raw stream was only consulted when <c>ResultText</c> was NULL, and a refused
/// session does not look like that. It comes back with a result envelope whose text is EMPTY and
/// whose cost is zero — which is why the log line could print <c>$0.00</c> at all — so the tail
/// carrying the backend's own words was never read and the session was filed as a plain
/// <c>AgentError</c>.</para>
///
/// <para>This fake agent reproduces exactly that shape.</para>
/// </summary>
public sealed partial class HarnessTests
{
    private static string RateLimitedAgentScript() => string.Join("\r\n",
        "@echo off",
        // A parsed line, so ResultText becomes the EMPTY buffer rather than null — the whole point.
        "echo {\"type\":\"step_start\"}",
        "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0,\"tokens\":{\"input\":0,\"output\":0,\"reasoning\":0,\"cache\":{\"read\":0}}}}",
        // The backend's refusal, on the raw stream where it actually arrives.
        "echo Claude AI usage limit reached - try again in 45m",
        "exit /b 1",
        "");

    [Fact]
    public async Task ABackendRateLimitParksTheRunAsBackoff_WithoutSpendingAnAttempt()
    {
        var script = Path.Combine(_repo, "fake-agent-429.cmd");
        await File.WriteAllTextAsync(script, RateLimitedAgentScript(), CancellationToken.None);

        var plan = new PlanConfig
        {
            Name = "RateLimitPlan",
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
        plan.Limits.BackoffMinutes = 30;   // the plan's flat guess, which the backend's 45m must beat

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };

        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        var code = await host.Services.GetRequiredService<Orchestrator>()
            .RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(120));
        Assert.Equal(0, code);

        var session = Assert.Single(state.History);
        Assert.Equal(SessionOutcome.LimitBackoff, session.Outcome);
        Assert.Equal(RunStatus.Backoff, state.Status);
        Assert.Equal(1, state.ConsecutiveBackoffs);

        // The heart of the bug: an account limit is not the stage's fault and may not cost it a try.
        Assert.Equal(0, state.AttemptsThisStage);

        // And the wait is the backend's stated one, not the plan's flat 30 minutes.
        var log = await File.ReadAllTextAsync(Path.Combine(_repo, ".conductor", "conductor.log"), CancellationToken.None);
        Assert.Contains("usage limit detected — backing off 45m", log, StringComparison.Ordinal);
        Assert.Contains("reset time given by the backend", log, StringComparison.Ordinal);
    }
}
