using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>Live regression for the session #3 (U-series, 2026-07-17) verify failure: a real
/// verifier agent produced a valid, complete JSON verdict (score/findings/verdict), but
/// SessionRunner's <c>ExtractSessionResult</c> ran it through the same 700-char SESSION-RESULT:
/// crop used for Deliver/Fix sessions' one-paragraph summaries — chopping the closing brace off
/// before <see cref="Verifier.Parse"/> ever saw it. The session was recorded AgentError
/// ("verifier produced no parseable score") even though the agent's output was fine. This drives a
/// REAL Deliver-then-Verify pair through the orchestrator (same scaffolding as
/// <see cref="U03GatelessLiveTests"/> / <see cref="P2QaDialLiveTests"/>: a real git repo, a real
/// fake-agent PowerShell script, qa=everySession so the deliver session is always followed by a
/// verify session) where the verify session's output is long (>700 chars) AND contains a finding
/// with a quoted brace placeholder (<c>{model}</c>) — the second, independent way the old regex
/// broke, since <c>\{[^{}]*"score"[^{}]*\}</c> disallowed ANY brace anywhere in the match.</summary>
public sealed class VerifyLongOutputLiveTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task VerifySession_WithLongBracyVerdict_ReachesProgress_NotAgentError()
    {
        var repo = Environment.GetEnvironmentVariable("VERIFY_LONG_DEBUG_REPO")
            ?? Path.Combine(Path.GetTempPath(), $"conductor-verifylong-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        using var cts = new CancellationTokenSource();
        try
        {
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email verifylong@test");
            Git("config user.name VerifyLong");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# v", CancellationToken.None);
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| H0.1 | never done | TODO | | |\n",
                CancellationToken.None);
            // One script serves both sessions — it tells Deliver from Verify by sniffing the
            // prompt for the phrase PromptBuilder puts in every verify prompt ("VERIFICATION
            // session"). The verify branch emits a >700-char JSON verdict with a quoted `{model}`
            // placeholder inside a finding, reproducing both independent ways the old parser broke.
            var agentScript = Path.Combine(repo, "fake-agent.ps1");
            await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
                "param([string]$Repo, [string]$Prompt = \"\")",
                "function O($type, $part) {",
                "    $o = @{ type = $type; session_id = 'fake' }",
                "    if ($null -ne $part) { $o.part = $part }",
                "    Write-Output ($o | ConvertTo-Json -Compress -Depth 6)",
                "}",
                "O 'step_start' $null",
                "if ($Prompt -like '*VERIFICATION session*') {",
                "    $pad = 'x' * 900",
                "    $verdict = '{\"score\":92,\"findings\":[\"the {model} placeholder resolves correctly\",\"' + $pad + '\"],\"verdict\":\"PASS\"}'",
                "    O 'step_finish' @{ cost = 0.0002; tokens = @{ input = 20; output = 10; reasoning = 0; cache = @{ read = 0 } } }",
                "    O 'text' @{ text = $verdict }",
                "} else {",
                "    Add-Content (Join-Path $Repo 'work.txt') ([Guid]::NewGuid().ToString())",
                "    $null = git -C $Repo add -A 2>&1",
                "    $null = git -C $Repo commit -m session --no-gpg-sign --quiet 2>&1",
                "    O 'step_finish' @{ cost = 0.0001; tokens = @{ input = 10; output = 5; reasoning = 0; cache = @{ read = 0 } } }",
                "    O 'text' @{ text = 'SESSION-RESULT: delivered, awaiting verification.' }",
                "}",
                "exit 0",
                ""), Encoding.ASCII, CancellationToken.None);

            var planPath = Path.Combine(repo, "verifylong.plan.json");
            var seed = new PlanConfig
            {
                Name = "verify-long-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "H0", Title = "VerifyLong", Sessions = 6 }],
                Agent = new AgentConfig
                {
                    Command = "powershell",
                    Args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", agentScript,
                            "-Repo", repo.Replace("\\", "/"), "-Prompt", "{prompt}"],
                    Provider = "opencode",
                },
                GatePolicy = "perSession",
                Gates = [],
                Pipeline = new PipelineRules { Qa = new QaRule { Mode = "everySession" } },
            };
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), CancellationToken.None);
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0), consoleSink: false);
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!(state.History.Count >= 2 && state.History[1].Outcome is not null) && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);

            Assert.True(state.History.Count >= 2, "expected a deliver session followed by a verify session");
            Assert.Equal(SessionKind.Deliver, state.History[0].Kind);
            Assert.Equal(SessionKind.Verify, state.History[1].Kind);

            var verifySession = state.History[1];
            // The bug: this used to be AgentError with GateFailures "verifier session produced no
            // valid score JSON" because the 700-char crop ate the closing brace.
            Assert.Equal(SessionOutcome.Progress, verifySession.Outcome);
            Assert.Contains("{model}", verifySession.ResultSummary);

            await cts.CancelAsync();
            await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        finally
        {
            await cts.CancelAsync();
            if (Environment.GetEnvironmentVariable("VERIFY_LONG_DEBUG_REPO") is null)
                try { TestTemp.DeleteTree(repo); } catch (IOException) { }
        }
    }
}
