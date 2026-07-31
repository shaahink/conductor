using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// SC7.2 — the same full cycle as <see cref="HarnessTests"/>, through the real orchestrator and a real
/// agent process, proving the two halves this checkpoint owes end to end: the wire carries readable
/// one-liners, and the per-session digest is computed, written to run.db, and readable back out.
/// </summary>
/// <remarks>
/// Deliberately a live-process proof rather than a unit assertion on <c>SessionDigest.Add</c>. The
/// digest is folded inside <c>SessionRunner.TrackActivity</c> and persisted by <c>RunLoop</c>'s
/// <c>RecordSession</c>; a test that never spawns an agent proves the arithmetic and none of the
/// plumbing, and the plumbing is where every defect in this era has actually lived.
/// </remarks>
public sealed partial class HarnessTests
{
    [Fact]
    public async Task FullCycle_TheWireIsReadable_AndTheDigestIsComputedStoredAndReadBack()
    {
        var body = new string('z', 400);
        var agent = Path.Combine(_repo, "fake-claude-digest.cmd");
        await File.WriteAllTextAsync(agent, string.Join("\r\n",
            "@echo off",
            "echo {\"type\":\"system\",\"subtype\":\"init\"}",
            "echo {\"type\":\"assistant\",\"message\":{\"id\":\"m1\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"," +
                "\"input\":{\"command\":\"dotnet build Conductor.slnx -clp:ErrorsOnly\"}}]}}",
            "echo {\"type\":\"assistant\",\"message\":{\"id\":\"m2\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Write\"," +
                $"\"input\":{{\"content\":\"{body}\",\"file_path\":\"src/App.cs\"}}}}]}}}}",
            "echo {\"type\":\"assistant\",\"message\":{\"id\":\"m3\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Edit\"," +
                "\"input\":{\"file_path\":\"README.md\",\"old_string\":\"a\",\"new_string\":\"a\\nb\"}}]}}",
            "echo {\"type\":\"assistant\",\"message\":{\"id\":\"m4\",\"content\":[{\"type\":\"tool_use\"," +
                "\"name\":\"mcp__conductor-tasks__bg_start\",\"input\":{\"purpose\":\"H0.1 full solution build\"," +
                "\"command\":[\"dotnet\",\"test\",\"Conductor.slnx\"]}}]}}",
            "echo {\"type\":\"assistant\",\"message\":{\"id\":\"m5\",\"content\":[{\"type\":\"tool_use\"," +
                "\"name\":\"mcp__conductor-tasks__task_update\",\"input\":{\"taskId\":\"H0.1\",\"status\":\"done\"}}]}}",
            "echo harness digest> harness-digest.txt",
            "git add harness-digest.txt",
            "git commit -m \"feat: deliver digest checkpoint\"",
            "echo {\"type\":\"result\",\"total_cost_usd\":0.02,\"num_turns\":5," +
                "\"result\":\"SESSION-RESULT: wrote, edited, claimed.\",\"usage\":{\"input_tokens\":90,\"output_tokens\":30}}",
            "exit /b 0",
            ""));

        var plan = new PlanConfig
        {
            Name = "DigestPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig { Command = "cmd.exe", Args = { "/c", agent, "{prompt}" }, Provider = "claude" },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        Assert.Equal(0, await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None));
        Assert.Single(state.History);

        // ── the wire carries READABLE lines, not a field dump and not a JSON blob ──
        var texts = TranscriptLog.ReadAll(Path.Combine(_stateDir, "transcript.jsonl"))
            .Where(l => l.Kind == "tool").Select(l => l.Text).ToList();

        Assert.Equal(
        [
            "Bash dotnet build Conductor.slnx -clp:ErrorsOnly",
            "Write App.cs (1 lines)",
            "Edit README.md (+2/-1)",
            "conductor bg_start \"H0.1 full solution build\"",
            "conductor task_update H0.1 -> done",
        ], texts);
        // The structure is still there beside every one of them — this is a rendering, not a recapture.
        Assert.All(TranscriptLog.ReadAll(Path.Combine(_stateDir, "transcript.jsonl")).Where(l => l.Kind == "tool"),
            l => Assert.NotNull(l.Tool));

        // ── the digest is computed on the session ──
        var digest = state.History[0].Digest;
        Assert.Equal(5, digest.ToolCalls);
        Assert.Equal(5, digest.DistinctTools);
        Assert.Equal("H0.1 -> done", Assert.Single(digest.Claims));
        Assert.Equal("H0.1 full solution build", Assert.Single(digest.BackgroundJobs));
        Assert.Equal(2, digest.FilesTouched.Count);      // the Write and the Edit; the Bash is not a write
        Assert.Equal(1, digest.FilesTouched["src/App.cs"]);
        Assert.Equal(1, digest.FilesTouched["README.md"]);
        Assert.Contains("dotnet build Conductor.slnx -clp:ErrorsOnly", digest.Commands, StringComparer.Ordinal);
        // The argv ARRAY joined back into a command line — SC7.1 stored it as `[3 items]`.
        Assert.Contains("dotnet test Conductor.slnx", digest.Commands, StringComparer.Ordinal);

        // ── and it is STORED: read back out of run.db, not out of the object that wrote it ──
        var store = host.Services.GetRequiredService<IRunStore>();
        var row = Assert.Single(store.QuerySessions(state.RunId));
        Assert.NotNull(row.Digest);
        var stored = SessionDigest.FromJson(row.Digest);
        Assert.NotNull(stored);
        Assert.Equal(digest.Summary(), stored!.Summary());
        Assert.Equal(digest.Render(), stored.Render());

        // ── and the run log says it at a glance ──
        var log = await File.ReadAllTextAsync(Path.Combine(_stateDir, "conductor.log"), CancellationToken.None);
        Assert.Contains("digest: 5 tool calls · 5 tools · 2 files (2 writes) · 1 claim · 1 bg job",
            log, StringComparison.Ordinal);
    }
}
