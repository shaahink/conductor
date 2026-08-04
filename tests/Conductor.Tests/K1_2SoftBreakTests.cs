using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Conductor.Core;
using Conductor.Core.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>K1.2 — the re-statement rule, on its own. Pure functions, no session, no clock, no files:
/// the rule that decides whether the agent hears the nudge again is the whole checkpoint, so it is
/// tested where it can be tested exhaustively.</summary>
public sealed class K1_2SoftBreakRuleTests
{
    private static SoftBreak.Signal Sig(long spent) => new(spent, 32_000_000, 22_400_000, "K1.2", DateTime.UtcNow);

    /// <summary>This era's real numbers: a 32M ceiling nudged at 22.4M gives a 9.6M margin, so the
    /// notice repeats every 480k tokens — about twenty times across the tail, at a couple of hundred
    /// tokens each. The cost of the rail is a rounding error against the margin it protects.</summary>
    [Fact]
    public void TheRestateStepScalesWithTheMargin_NotWithANumberSomeonePicked()
    {
        Assert.Equal(480_000, SoftBreak.RestateTokenStep(Sig(22_400_000)));
        Assert.Equal(25, SoftBreak.RestateTokenStep(new SoftBreak.Signal(500, 1000, 500, null, DateTime.UtcNow)));
        // Never zero, whatever the plan says — a zero step would restate on every single tool call.
        Assert.True(SoftBreak.RestateTokenStep(new SoftBreak.Signal(10, 10, 10, null, DateTime.UtcNow)) >= 1);
    }

    [Fact]
    public void TheFirstNoticeAlwaysGoesOut()
    {
        Assert.True(SoftBreak.ShouldRestate(Sig(22_400_000), null, DateTime.UtcNow, out var why));
        Assert.Equal("first", why);
        Assert.True(SoftBreak.ShouldRestate(Sig(22_400_000), new SoftBreak.Delivery(), DateTime.UtcNow, out _));
    }

    /// <summary>The defect this checkpoint exists for, stated as a test: the notice went out ONCE and
    /// then never again, and across eleven post-cap rollovers not one session stopped at it.</summary>
    [Fact]
    public void ItIsRestatedOnceTheSessionHasSpentAnotherStep_AndNotBefore()
    {
        var now = DateTime.UtcNow;
        var delivered = new SoftBreak.Delivery(1, now, 22_400_000, now, 22_400_000);

        Assert.False(SoftBreak.ShouldRestate(Sig(22_500_000), delivered, now, out var why));
        Assert.Equal("recent", why);

        Assert.True(SoftBreak.ShouldRestate(Sig(22_880_000), delivered, now, out why));
        Assert.Equal("tokens", why);
    }

    /// <summary>A session that stalls on one very long tool call spends nothing for minutes. The token
    /// step alone would let it out-wait the only cooperative exit it has.</summary>
    [Fact]
    public void ItIsRestatedOnTheClockToo_ForASessionThatIsSpendingSlowly()
    {
        var then = DateTime.UtcNow;
        var delivered = new SoftBreak.Delivery(1, then, 22_400_000, then, 22_400_000);
        var later = then + SoftBreak.RestateInterval + TimeSpan.FromSeconds(1);

        Assert.True(SoftBreak.ShouldRestate(Sig(22_400_100), delivered, later, out var why));
        Assert.Equal("interval", why);
    }

    /// <summary>The notice names the budget that is LEFT and the order to spend it in. "You are near a
    /// limit" is what it used to say, and it is the least actionable thing it could have said.</summary>
    [Fact]
    public void TheNoticeCarriesTheRemainingBudgetAndTheWrapUpOrder()
    {
        var notice = SoftBreak.Notice(Sig(28_800_000), 1);

        Assert.Contains("3.2M tokens", notice, StringComparison.Ordinal);   // remaining, not the ceiling
        Assert.Contains("32M tokens", notice, StringComparison.Ordinal);    // the ceiling, for scale
        Assert.Contains("10% left", notice, StringComparison.Ordinal);
        Assert.Contains("K1.2", notice, StringComparison.Ordinal);          // the checkpoint in hand

        // The order, and the reason the order is what it is.
        Assert.Contains("CLAIM FIRST", notice, StringComparison.Ordinal);
        Assert.Contains("THEN THE HANDOFF", notice, StringComparison.Ordinal);
        Assert.Contains("THEN COMMIT AND PUSH", notice, StringComparison.Ordinal);
        Assert.True(notice.IndexOf("CLAIM FIRST", StringComparison.Ordinal)
                  < notice.IndexOf("THEN THE HANDOFF", StringComparison.Ordinal));
        Assert.Contains("being cut off mid-sentence", notice, StringComparison.Ordinal);
    }

    /// <summary>A later notice says so. An agent that reads the same paragraph twice with no marker
    /// cannot tell a repeat from a scroll-back.</summary>
    [Fact]
    public void ARestatementSaysItIsOne()
    {
        Assert.DoesNotContain("notice 2", SoftBreak.Notice(Sig(28_800_000), 1), StringComparison.Ordinal);
        Assert.Contains("notice 2", SoftBreak.Notice(Sig(28_800_000), 2), StringComparison.Ordinal);
    }

    /// <summary>A signal file written by an older engine does not parse as JSON. It must still get the
    /// agent to wrap up — silence would be the worst possible reading of "I cannot read the numbers".</summary>
    [Fact]
    public void ASignalWithNoNumbersStillTellsTheAgentToWrapUp()
    {
        var notice = SoftBreak.Notice(new SoftBreak.Signal(), 1);
        Assert.Contains("SESSION TOKEN BUDGET", notice, StringComparison.Ordinal);
        Assert.Contains("CLAIM FIRST", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("Remaining: about", notice, StringComparison.Ordinal);
    }
}

/// <summary>K1.2 live gate — the whole cooperative rail, driven end to end against a scratch run of
/// this build with a deliberately tiny ceiling.
/// <para>The stand-in agent does exactly what a real one does: it works, and after each tool call it
/// runs the REAL <c>conductor hook-budget</c> command — the same command the engine writes into the
/// agent's PostToolUse settings. When the notice arrives twice it takes it, prints its result and
/// exits. The assertions are the checkpoint: re-stated rather than announced once, quoting a budget
/// that has visibly moved between the two, and a session record that says delivered, re-delivered and
/// obeyed.</para></summary>
public sealed class K1_2SoftBreakLiveTests : IDisposable
{
    private readonly string _repo;

    public K1_2SoftBreakLiveTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-k12-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        GitRun("init", "-b", "main");
        GitRun("config", "user.email", "k12@test");
        GitRun("config", "user.name", "K12 Test");
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("CONDUCTOR_TEST_KEEP_SCRATCH") is { Length: > 0 }) return;
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception) { }
    }

    private void GitRun(params string[] args)
    {
        var r = ProcessRunner.Run("git", args, _repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed ({r.ExitCode}): {r.Output} {r.StdErr}");
    }

    private static string ConductorExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "conductor.exe");
        Assert.True(File.Exists(exe), $"the freshly-built CLI must sit beside the test assembly: {exe}");
        return exe;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheNudgeIsRestatedWithAFreshBudget_AndASessionThatTakesItExitsCleanAndIsRecordedAsObeying()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "TRACKER.md"),
            "# K1.2 Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| H0.1 | the checkpoint in the agent's hands | TODO | | |\n", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_repo, "README.md"), "# K1.2 live repo", CancellationToken.None);
        GitRun("add", "-A");
        GitRun("commit", "-m", "chore: initial commit", "--no-gpg-sign");

        var agentScript = Path.Combine(_repo, "k12-agent.ps1");
        var stateDir = Path.Combine(_repo, ".conductor");
        var hookOut = Path.Combine(_repo, "hook-notices.txt");

        var plan = new PlanConfig
        {
            Name = "K12Plan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "K12", Sessions = 4 } },
            Agent = new AgentConfig
            {
                Command = "powershell",
                Args = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", agentScript,
                         "-Repo", _repo.Replace("\\", "/"), "-StateDir", stateDir.Replace("\\", "/"),
                         "-Cli", ConductorExe().Replace("\\", "/"), "-Prompt", "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
            Pipeline = new PipelineRules { Qa = new QaRule { Mode = "off" } },
        };
        // A deliberately tiny ceiling: 1000 tokens, nudged at half of it. The agent spends 100 a step,
        // so it crosses the threshold on step 5 and — if it takes the nudge — stops well under 1000.
        plan.Limits.MaxSessionTokens = 1000;
        plan.Limits.SoftBreakRatio = 0.5;
        plan.Report.Commit = false;

        await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
        [
            "param([string]$Repo, [string]$StateDir, [string]$Cli, [string]$Prompt = \"\")",
            "function O($type, $part) {",
            "    $o = @{ type = $type; session_id = 'fake' }",
            "    if ($null -ne $part) { $o.part = $part }",
            "    Write-Output ($o | ConvertTo-Json -Compress -Depth 6)",
            "}",
            "$notices = 0",
            "for ($i = 1; $i -le 9; $i++) {",
            "    O 'step_finish' @{ cost = 0.0001; tokens = @{ input = 100; output = 0; reasoning = 0; cache = @{ read = 0 } } }",
            // The PostToolUse hook, run exactly as the agent CLI would run it: after the tool call,
            // against this run's state dir, through the freshly built exe.
            "    for ($j = 0; $j -lt 8; $j++) {",
            "        Start-Sleep -Milliseconds 350",
            "        $out = (& $Cli hook-budget --state-dir $StateDir) -join ''",
            "        if ($out) { Add-Content -Path (Join-Path $Repo 'hook-notices.txt') -Value $out; $notices++; break }",
            "    }",
            "    if ($notices -ge 2) { break }",
            "}",
            "O 'text' @{ text = 'SESSION-RESULT: took the nudge on its second statement and stopped clean.' }",
            "exit 0",
            "",
        ]), Encoding.ASCII, CancellationToken.None);

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
        Assert.Equal(0, code);

        // ── RE-STATED, not announced once ───────────────────────────────────────────────────────
        Assert.True(File.Exists(hookOut), "the hook never spoke: the agent reached no notice at all");
        var notices = (await File.ReadAllLinesAsync(hookOut, CancellationToken.None))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement
                .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString() ?? "")
            .ToList();
        Assert.True(notices.Count >= 2, $"the nudge was delivered {notices.Count} time(s) — K1.2 is that once is not enough");
        Assert.Contains("notice 2", notices[1], StringComparison.Ordinal);

        // ── CARRYING THE ACTUAL REMAINING BUDGET, refreshed between statements ───────────────────
        var remaining = notices
            .Select(n => Regex.Match(n, @"Remaining: about (?<left>\d+) tokens",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5)))
            .ToList();
        Assert.All(remaining, m => Assert.True(m.Success, "a notice quoted no remaining budget"));
        var first = int.Parse(remaining[0].Groups["left"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var second = int.Parse(remaining[1].Groups["left"].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(second < first,
            $"the second notice repeated the first notice's budget ({first} -> {second}) — the signal was never refreshed");
        Assert.True(first <= 500, $"the nudge fired before the threshold: {first} tokens still remained");

        // ── STATING THE ORDER, and naming the work in hand ───────────────────────────────────────
        Assert.Contains("CLAIM FIRST", notices[0], StringComparison.Ordinal);
        Assert.Contains("THEN THE HANDOFF", notices[0], StringComparison.Ordinal);
        Assert.Contains("H0.1", notices[0], StringComparison.Ordinal);

        // ── AND THE SESSION EXITED CLEAN, AND THE RECORD SAYS SO ─────────────────────────────────
        var rec = Assert.Single(state.History);
        Assert.NotEqual(SessionOutcome.RolledOver, rec.Outcome);
        Assert.True(rec.TokensTotal < plan.Limits.MaxSessionTokens,
            $"the session was supposed to stop under its ceiling and spent {rec.TokensTotal}");

        var sb = rec.SoftBreak;
        Assert.NotNull(sb);
        Assert.Equal(500, sb.ThresholdTokens);
        Assert.Equal(1000, sb.CeilingTokens);
        Assert.True(sb.Delivered);
        Assert.True(sb.Restated, $"delivered {sb.DeliveredCount} time(s)");
        Assert.True(sb.Obeyed, "a session that took the nudge and exited under its ceiling must record as obeying");
        Assert.NotNull(sb.FirstUtc);
        Assert.True(sb.LastAtTokens > sb.FirstAtTokens);

        // …and it is in the ledger, which is where the next tuning pass will read it.
        using var store = new SqliteRunStore(Path.Combine(plan.StateDir, "run.db"),
            NullLogger<SqliteRunStore>.Instance);
        var row = store.QuerySessionByNumber(state.RunId, rec.Number);
        Assert.NotNull(row);
        Assert.NotNull(row.SoftBreak);
        using var stored = JsonDocument.Parse(row.SoftBreak);
        Assert.True(stored.RootElement.GetProperty("obeyed").GetBoolean());
        Assert.True(stored.RootElement.GetProperty("deliveredCount").GetInt32() >= 2);
    }
}
