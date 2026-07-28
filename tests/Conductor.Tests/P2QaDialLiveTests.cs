using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>P2 live gate: the QA dial steers a REAL run and rides G3.2's live reload. With
/// qa=off a committing deliver session queues no verification (deliver-only, exactly the
/// deliver-verify + skip-verification run); flipping the dial to everySession in the plan file and
/// queueing reload-plan makes the very same run start verifying at its next session boundary — no
/// restart.</summary>
public sealed class P2QaDialLiveTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task QaDialOff_RunsDeliverOnly_AndFlippingItLive_QueuesVerification()
    {
        var repo = Environment.GetEnvironmentVariable("P2_QA_DEBUG_REPO")
            ?? Path.Combine(Path.GetTempPath(), $"conductor-qa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        using var cts = new CancellationTokenSource();
        try
        {
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email qa@test");
            Git("config user.name Qa");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# q", CancellationToken.None);
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| H0.1 | never done | TODO | | |\n",
                CancellationToken.None);
            // Each session commits something, so the green verdict path advances the workflow —
            // the exact path where deliver-verify would queue a verify session. PowerShell, not a
            // .cmd: cmd.exe cannot receive the multiline {prompt} argument (the raw command line
            // breaks at the first newline and the script never runs — the same reason
            // tools/fake-agent.ps1 exists). ASCII-only on purpose: PS 5.1 reads an un-BOM'd file
            // as ANSI. The opencode-json payloads nest under `part` (flat-at-root crashes the parser).
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

            var planPath = Path.Combine(repo, "qa.plan.json");
            var seed = new PlanConfig
            {
                Name = "qa-dial-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "H0", Title = "Dial", Sessions = 6 }],
                Agent = new AgentConfig
                {
                    Command = "powershell",
                    Args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", agentScript,
                            "-Repo", repo.Replace("\\", "/"), "-Prompt", "{prompt}"],
                    Provider = "opencode",
                },
                GatePolicy = "perSession",
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
                Pipeline = new PipelineRules { Qa = new QaRule { Mode = "off" } },
            };
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), CancellationToken.None);
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0), consoleSink: false);
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

            // Gate half 1 — qa=off runs deliver-only: the session committed and went green, yet no
            // verification is queued (dial off skipped the verify step, treating it as passed).
            // A History record appears at session START — wait for its evaluated Outcome.
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while ((state.History.Count < 1 || state.History[0].Outcome is null) && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.True(state.History.Count >= 1 && state.History[0].Outcome is not null, "session 1 should complete");
            Assert.Equal(SessionKind.Deliver, state.History[0].Kind);
            Assert.True(state.History[0].NewCommits.Count > 0, "the fake agent should have committed");
            Assert.Null(state.PendingVerify);
            Assert.DoesNotContain(state.History, h => h.Kind == SessionKind.Verify);

            // Gate half 2 — flip the dial LIVE: everySession in the plan file (what the Face
            // Settings edit posts) + reload-plan. At the next boundary the swapped plan's dial
            // demands verification, so a subsequent green deliver queues (or runs) a verify.
            var edited = PlanConfig.Load(planPath);
            edited.Pipeline!.Qa!.Mode = "everySession";
            edited.Save();
            var inbox = host.Services.GetRequiredService<System.Collections.Concurrent.ConcurrentQueue<ControlCommand>>();
            inbox.Enqueue(ControlCommand.Of(ControlAction.ReloadPlan));

            deadline = DateTime.UtcNow.AddSeconds(120);
            while (state.PendingVerify is null
                   && !state.History.Any(h => h.Kind == SessionKind.Verify)
                   && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.True(state.PendingVerify is not null || state.History.Any(h => h.Kind == SessionKind.Verify),
                "flipping the dial to everySession via live reload should make the run verify");

            await cts.CancelAsync();
            var code = await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal(130, code); // clean cancellation path, state saved
        }
        finally
        {
            await cts.CancelAsync();
            if (Environment.GetEnvironmentVariable("P2_QA_DEBUG_REPO") is null)
                try { Directory.Delete(repo, recursive: true); } catch (IOException) { }
        }
    }
}
