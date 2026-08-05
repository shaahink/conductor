using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>P5 live gate: rollover is OFF by default (a session ends normally with no cap in the
/// plan), and flipping it on THIS RUN ONLY via the set-rollover control verb makes the very next
/// session past the token count end <see cref="SessionOutcome.RolledOver"/> — with no attempt
/// burned and the plan file byte-identical (the override lives in run state, never in the plan).</summary>
public sealed class P5RolloverLiveTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RolloverOffByDefault_AndThisRunOverride_RollsOver_WithoutWritingThePlan()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-p5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        using var cts = new CancellationTokenSource();
        try
        {
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email p5@test");
            Git("config user.name P5");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# p5", CancellationToken.None);
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| H0.1 | never done | TODO | | |\n",
                CancellationToken.None);

            // The credential-free fake agent (see P2QaDialLiveTests for the shape rationale) —
            // emits 15 tokens per session, so a this-run cap of 10 forces a rollover.
            var agentScript = Path.Combine(repo, "fake-agent.ps1");
            await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
                "param([string]$Repo, [string]$Prompt = \"\")",
                "function O($type, $part) {",
                "    $o = @{ type = $type; session_id = 'fake' }",
                "    if ($null -ne $part) { $o.part = $part }",
                "    Write-Output ($o | ConvertTo-Json -Compress -Depth 6)",
                "}",
                "O 'step_start' $null",
                "Add-Content (Join-Path $Repo 'work.txt') ([Guid]::NewGuid().ToString())",
                "$null = git -C $Repo add -A 2>&1",
                "$null = git -C $Repo commit -m session --no-gpg-sign --quiet 2>&1",
                "O 'step_finish' @{ cost = 0.0001; tokens = @{ input = 10; output = 5; reasoning = 0; cache = @{ read = 0 } } }",
                "O 'text' @{ text = 'SESSION-RESULT: delivered.' }",
                "exit 0",
                ""), Encoding.ASCII, CancellationToken.None);

            var planPath = Path.Combine(repo, "p5.plan.json");
            var seed = new PlanConfig
            {
                Name = "rollover-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "H0", Title = "Roll", Sessions = 6 }],
                Agent = new AgentConfig
                {
                    Command = "powershell",
                    Args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", agentScript,
                            "-Repo", repo.Replace("\\", "/"), "-Prompt", "{prompt}"],
                    Provider = "opencode",
                },
                GatePolicy = "perSession",
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
                // Deliver-only so the loop keeps producing deliver sessions to roll over.
                Pipeline = new PipelineRules { Qa = new QaRule { Mode = "off" } },
            };
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), CancellationToken.None);
            var planBytes = await File.ReadAllBytesAsync(planPath, CancellationToken.None);
            var plan = PlanConfig.Load(planPath);
            Assert.Null(plan.Limits.MaxSessionTokens); // OFF is the default, honestly

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0), consoleSink: false);
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

            // Gate half 1 — no cap anywhere: session 1 completes with a normal outcome.
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while ((state.History.Count < 1 || state.History[0].Outcome is null) && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.True(state.History.Count >= 1 && state.History[0].Outcome is not null, "session 1 should complete");
            Assert.NotEqual(SessionOutcome.RolledOver, state.History[0].Outcome);

            // Gate half 2 — flip the rollover ON for THIS RUN ONLY: 10 tokens < the fake agent's 15.
            var inbox = host.Services.GetRequiredService<System.Collections.Concurrent.ConcurrentQueue<ControlCommand>>();
            inbox.Enqueue(new ControlCommand(ControlAction.SetRollover, false, null, null, false, "10"));

            deadline = DateTime.UtcNow.AddSeconds(120);
            while (!state.History.Any(h => h.Outcome == SessionOutcome.RolledOver) && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            var rolled = state.History.FirstOrDefault(h => h.Outcome == SessionOutcome.RolledOver);
            Assert.True(rolled is not null, "a session past the this-run token cap should end RolledOver");
            Assert.Equal(10, state.MaxSessionTokensThisRun);

            // No attempt burned: the rollover path returns before the verdict engine, so the
            // stage's attempt counter is exactly where the normal green sessions left it.
            Assert.Equal(0, state.AttemptsThisStage);

            // And the override never wrote the plan: the file is byte-identical.
            var planBytesAfter = await File.ReadAllBytesAsync(planPath, CancellationToken.None);
            Assert.Equal(planBytes, planBytesAfter);

            await cts.CancelAsync();
            var code = await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal(130, code);
        }
        finally
        {
            await cts.CancelAsync();
            try { TestTemp.DeleteTree(repo); } catch (IOException) { }
        }
    }
}
