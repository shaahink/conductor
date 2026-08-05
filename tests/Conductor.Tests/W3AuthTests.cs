using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Hosting;
using Conductor.Core.Orchestration;
using Conductor.Core.Providers;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// W3.2 truth gates — an expired credential is its own failure, not a generic agent error.
///
/// The anchor is a real artifact: <c>fixtures/session-013-auth-401.jsonl</c> is the U-series run's
/// thirteenth session, verbatim apart from redacted local paths in the init envelope. It carries a
/// <c>401 / authentication_failed</c> retry envelope and a terminal result reading "Failed to
/// authenticate: OAuth session expired and could not be refreshed" — and the engine recorded it as
/// <c>AgentError</c>, then burned the stage's remaining attempts retrying a dead token.
/// </summary>
public sealed class W3AuthTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "session-013-auth-401.jsonl");

    // ---------------------------------------------------------------- the classifier

    [Theory]
    [InlineData("Failed to authenticate: OAuth session expired and could not be refreshed")]
    [InlineData("{\"error_status\":401,\"error\":\"authentication_failed\"}")]
    [InlineData("Invalid API key · Please run /login")]
    [InlineData("HTTP 401 Unauthorized")]
    [InlineData("Your credential is invalid. Run claude setup-token.")]
    public void AuthPhrasings_AreClassifiedAsAuthFailures(string evidence)
    {
        Assert.True(ProviderText.DetectsAuthFailure(evidence));
    }

    [Theory]
    [InlineData("Claude AI usage limit reached|1763000000")]
    [InlineData("429 too many requests — please retry")]
    [InlineData("You have exceeded your weekly limit")]
    [InlineData("build succeeded in 401 ms")]      // a bare number is not an HTTP status
    [InlineData("")]
    public void NonAuthEvidence_IsNotAnAuthFailure(string evidence)
    {
        Assert.False(ProviderText.DetectsAuthFailure(evidence));
    }

    [Fact]
    public void UsageLimitAndAuth_AreDistinctVerdicts()
    {
        // The two refusals must never collapse into each other: one is waited out, one is fatal.
        const string quota = "Claude AI usage limit reached";
        const string auth = "authentication_failed";
        Assert.True(ProviderText.DetectsUsageLimit(quota));
        Assert.False(ProviderText.DetectsAuthFailure(quota));
        Assert.True(ProviderText.DetectsAuthFailure(auth));
        Assert.False(ProviderText.DetectsUsageLimit(auth));
    }

    // ---------------------------------------------------------------- replaying session #13

    [Fact]
    public async Task Session13_RawLog_ClassifiesAsAuthFailure_OnTheFirstRetryEnvelope()
    {
        var provider = new ClaudeProvider();
        var emitted = new List<(string Kind, string Text)>();
        var state = new AgentStreamState((kind, text) => emitted.Add((kind, text)));

        var lines = await File.ReadAllLinesAsync(FixturePath);
        Assert.NotEmpty(lines);

        provider.ParseLine(lines[0], state);
        Assert.Null(state.AuthFailure);          // the init envelope says nothing about the token

        provider.ParseLine(lines[1], state);     // {"subtype":"api_retry","error_status":401,…}
        Assert.NotNull(state.AuthFailure);
        Assert.Contains("401", state.AuthFailure, StringComparison.Ordinal);
        // …and it reaches the transcript instead of being flattened to the bare subtype.
        Assert.Contains(emitted, e => e.Kind == "system" && e.Text.Contains("authentication_failed", StringComparison.Ordinal));

        foreach (var line in lines.Skip(2)) provider.ParseLine(line, state);
        Assert.True(state.ResultIsError);
        Assert.Contains("OAuth session expired", state.ResultText, StringComparison.Ordinal);
        Assert.True(provider.DetectsAuthFailure(state.ResultText!));
    }

    [Fact]
    public void HealthyClaudeStream_LeavesAuthUnflagged()
    {
        var provider = new ClaudeProvider();
        var state = new AgentStreamState((_, _) => { });
        provider.ParseLine("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-opus-4-8\"}", state);
        provider.ParseLine("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"working\"}]}}", state);
        provider.ParseLine("{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"done\",\"total_cost_usd\":0.01}", state);
        Assert.Null(state.AuthFailure);
        Assert.False(state.ResultIsError);
    }

    [Fact]
    public void ReauthHint_NamesTheCommandPerProvider()
    {
        Assert.Contains("setup-token", SessionRunner.ReauthHint("claude"), StringComparison.Ordinal);
        Assert.Contains("opencode auth login", SessionRunner.ReauthHint("opencode"), StringComparison.Ordinal);
        Assert.Contains("re-authenticate", SessionRunner.ReauthHint("text"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the smoke test's scope

    [Fact]
    public async Task AuthSmokeTest_SkipsCommandsItCannotMeaningfullyPing()
    {
        Assert.False(AuthSmokeTest.CanProbe(new AgentConfig { Command = "cmd.exe" }));
        Assert.False(AuthSmokeTest.CanProbe(new AgentConfig { Command = "" }));
        Assert.True(AuthSmokeTest.CanProbe(new AgentConfig { Command = "claude" }));
        Assert.True(AuthSmokeTest.CanProbe(new AgentConfig { Command = "/usr/local/bin/opencode" }));

        // A fake agent is never spawned by the probe — no cost, no seconds, no invented verdict.
        var plan = new PlanConfig { Name = "p", Repo = ".", Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", "echo", "{prompt}"] } };
        var result = await AuthSmokeTest.RunAsync(plan, TimeSpan.FromSeconds(5));
        Assert.True(result.Passed);
        Assert.Contains("skipped", result.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the live gate

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveRun_ReplayingSession13_ParksForReauth_WithNoGateBatteryAndNoRetry()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w32-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(repo);
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email w32@test");
            Git("config user.name W32");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r");
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| H0.1 | the item | TODO | | |\n");

            // The agent for this run IS session #13: the real log, replayed line for line.
            var replay = Path.Combine(repo, "session-013.jsonl");
            File.Copy(FixturePath, replay);
            var agentScript = Path.Combine(repo, "fake-agent.cmd");
            await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
                "@echo off",
                $"type \"{replay}\"",
                "exit /b 1",
                ""));

            // A gate that leaves a footprint. An auth park must not run the battery: the agent never
            // touched the repo, and the gates cost minutes that prove nothing about a dead token.
            var gateMarker = Path.Combine(repo, "gate-ran.txt");
            var planPath = Path.Combine(repo, "test.plan.json");
            var seed = new PlanConfig
            {
                Name = "w32-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "H0", Title = "Auth", Sessions = 1 }],
                Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", agentScript, "{prompt}"], Provider = "claude" },
                Gates = [new GateConfig { Name = "smoke", Command = $"cmd /c echo ran > \"{gateMarker}\"", Tier = "fast", TimeoutMinutes = 1 }],
            };
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 3), consoleSink: false);
            // Three session slots and thirty seconds of loop: a run that retries a dead credential
            // has every opportunity to. The park is what stops it, not the budget.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
            var code = await runTask.WaitAsync(TimeSpan.FromSeconds(120), CancellationToken.None);
            Assert.Equal(130, code);   // cancelled while parked, as designed

            // ONE session, parked — not three sessions burned against a credential that cannot work.
            var rec = Assert.Single(state.History);
            Assert.Equal(SessionOutcome.AuthFailed, rec.Outcome);
            Assert.Equal(RunStatus.NeedsHuman, state.Status);
            Assert.Contains("re-auth", state.AttentionReason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("setup-token", state.AttentionReason, StringComparison.Ordinal);
            Assert.False(File.Exists(gateMarker), "the gate battery ran against a dead credential");

            var log = await File.ReadAllTextAsync(Path.Combine(repo, ".conductor", "conductor.log"), CancellationToken.None);
            Assert.Contains("auth: the agent backend rejected the credential", log, StringComparison.Ordinal);
            Assert.DoesNotContain("usage limit detected", log, StringComparison.Ordinal);
        }
        finally { try { TestTemp.DeleteTree(repo); } catch (IOException) { } }
    }
}
