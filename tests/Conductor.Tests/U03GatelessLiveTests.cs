using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>U0.3 live gate: a plan with <c>"gates": []</c> must run a REAL fake-agent session to a
/// truthful, non-lying verdict — no gate battery to fail, no gate battery to silently produce a
/// blank summary either. Mirrors <see cref="P2QaDialLiveTests"/>'s scaffolding (real git repo, real
/// orchestrator, a PowerShell fake agent emitting Claude-shaped stream-json) with gates entirely
/// absent instead of dialled.</summary>
public sealed class U03GatelessLiveTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task GatelessPlan_DeliverSessionThatCommits_ReachesProgress_WithHonestGateSummary()
    {
        var repo = Environment.GetEnvironmentVariable("U03_GATELESS_DEBUG_REPO")
            ?? Path.Combine(Path.GetTempPath(), $"conductor-gateless-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        using var cts = new CancellationTokenSource();
        try
        {
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email gateless@test");
            Git("config user.name Gateless");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# g", CancellationToken.None);
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| H0.1 | never done | TODO | | |\n",
                CancellationToken.None);
            // Commits without flipping the tracker row — the exact shape of a `Progress` verdict
            // (gates green + new commits, nothing newly DONE). Same script shape as
            // P2QaDialLiveTests: PowerShell, not a .cmd (cmd.exe cannot receive the multiline
            // {prompt} argument), ASCII-only (PS 5.1 reads a BOM-less file as ANSI).
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
                "O 'text' @{ text = 'SESSION-RESULT: delivered, no gates configured.' }",
                "exit 0",
                ""), Encoding.ASCII, CancellationToken.None);

            var planPath = Path.Combine(repo, "gateless.plan.json");
            var seed = new PlanConfig
            {
                Name = "gateless-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "H0", Title = "Gateless", Sessions = 6 }],
                Agent = new AgentConfig
                {
                    Command = "powershell",
                    Args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", agentScript,
                            "-Repo", repo.Replace("\\", "/"), "-Prompt", "{prompt}"],
                    Provider = "opencode",
                },
                GatePolicy = "perSession",
                Gates = [], // U0.3: the whole point — no gates configured at all
            };
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), CancellationToken.None);
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: true, MaxSessions: 1), consoleSink: false);
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

            var deadline = DateTime.UtcNow.AddSeconds(90);
            while ((state.History.Count < 1 || state.History[0].Outcome is null) && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);

            Assert.True(state.History.Count >= 1 && state.History[0].Outcome is not null, "session 1 should complete");
            var record = state.History[0];
            Assert.True(record.NewCommits.Count > 0, "the fake agent should have committed");
            Assert.Equal(SessionOutcome.Progress, record.Outcome);
            // The real point of U0.3: an empty gate list reads as an honest verdict, never a blank
            // or lying summary — GateRunner.Summary([]) feeds directly into this recorded field.
            Assert.Equal("gates green (none configured)", record.GateSummary);

            await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        finally
        {
            await cts.CancelAsync();
            if (Environment.GetEnvironmentVariable("U03_GATELESS_DEBUG_REPO") is null)
                try { Directory.Delete(repo, recursive: true); } catch (IOException) { }
        }
    }
}
