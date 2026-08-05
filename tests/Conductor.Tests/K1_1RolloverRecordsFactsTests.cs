using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>K1.1 live gate — a rolled-over session's commits and claims are recorded like any other
/// session's, and a rollover still means what it meant.
/// <para>The bug, measured over both Sarban runs: <c>commit_count</c> was 0 on 100% of rollovers
/// while git ground truth over each rolled-over session's own <c>started_utc..ended_utc</c> window
/// said 91% of them had really committed. The rollover branch of <c>SessionRunner.RunAsync</c>
/// returned before the verdict pass that fills <see cref="SessionRecord.NewCommits"/> and
/// <see cref="SessionRecord.NewlyDone"/>, so every board, REPORT.md row, digest and Telegram push
/// under-reported on every rollover.</para>
/// <para>This drives a real fake agent that commits AND claims through the real CLI, past a plan
/// token cap, and asserts on the record and on the <c>run.db</c> row the ledger actually reads —
/// then asserts the two things a rollover must still be: no attempt burned, no gate battery
/// run.</para></summary>
public sealed class K1_1RolloverRecordsFactsTests : IDisposable
{
    private readonly string _repo;

    public K1_1RolloverRecordsFactsTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-k11-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        GitRun("init", "-b", "main");
        GitRun("config", "user.email", "k11@test");
        GitRun("config", "user.name", "K11 Test");
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
    public async Task ASessionThatRollsOver_RecordsItsCommitsAndItsClaims_WithNoAttemptBurnedAndNoGateRun()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "TRACKER.md"),
            "# K1.1 Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| H0.1 | claimed by a session that then rolls over | TODO | | |\n" +
            "| H0.2 | never touched | TODO | | |\n", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_repo, "README.md"), "# K1.1 live repo", CancellationToken.None);
        GitRun("add", "-A");
        GitRun("commit", "-m", "chore: initial commit", "--no-gpg-sign");
        var startHead = (await ProcessRunner.RunAsync("git", ["rev-parse", "HEAD"], _repo,
            TimeSpan.FromSeconds(30), CancellationToken.None)).Output.Trim();

        var agentScript = Path.Combine(_repo, "k11-agent.ps1");
        var plan = new PlanConfig
        {
            Name = "K11Plan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "K11", Sessions = 4 } },
            Agent = new AgentConfig
            {
                // PowerShell, not cmd.exe: the composed session prompt arrives as one argv entry and
                // a real one runs well past cmd's 8191-character command-line ceiling — the first
                // draft of this test died on "The command line is too long" before the stand-in
                // agent executed a single line. (Bug #21 is the engine-side half of the same wall.)
                Command = "powershell",
                Args = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", agentScript,
                         "-Repo", _repo.Replace("\\", "/"), "-Prompt", "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            // A gate that would leave a mark if it ever ran. It must not run: a rollover defers the
            // battery, and asserting on the empty gate table is how we know the fix recorded the
            // facts WITHOUT quietly promoting the rollover into a verdict pass.
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
            Pipeline = new PipelineRules { Qa = new QaRule { Mode = "off" } },
        };
        // The whole point: 15 tokens of agent against a 10-token cap, set in the plan so session 1
        // itself rolls over. TokensTotal is input+output+reasoning+cacheRead.
        plan.Limits.MaxSessionTokens = 10;
        plan.Report.Commit = false;

        var planPath = Path.Combine(_repo, "k11.plan.json");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, PlanConfig.JsonOpts),
            CancellationToken.None);
        // Order matters: the deliverable, the commit and the claim ALL land before the step_finish
        // that reports the tokens, so the session has really done its work by the time the engine
        // sees it cross the cap.
        await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
        [
            "param([string]$Repo, [string]$Prompt = \"\")",
            "function O($type, $part) {",
            "    $o = @{ type = $type; session_id = 'fake' }",
            "    if ($null -ne $part) { $o.part = $part }",
            "    Write-Output ($o | ConvertTo-Json -Compress -Depth 6)",
            "}",
            "Set-Content (Join-Path $Repo 'deliverable.md') 'rolled-over deliverable'",
            "$null = git -C $Repo add deliverable.md 2>&1",
            "$null = git -C $Repo commit -m 'feat: work done before the ceiling' --no-gpg-sign --quiet 2>&1",
            "& '" + ConductorExe() + "' task --plan '" + planPath + "' --done H0.1 " +
                "-e .conductor/evidence/H0/rollover-proof.md",
            "Set-Content (Join-Path $Repo 'claim-exit.txt') \"exit=$LASTEXITCODE\"",
            "O 'text' @{ text = 'SESSION-RESULT: delivered H0.1, then hit the ceiling.' }",
            "O 'step_finish' @{ cost = 0.0001; tokens = @{ input = 10; output = 5; reasoning = 0; cache = @{ read = 0 } } }",
            "exit 0",
            "",
        ]), Encoding.ASCII, CancellationToken.None);

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
        Assert.Equal(0, code);

        // The CLI accepted the claim in its OWN process — without that nothing below means anything.
        var claimExit = Path.Combine(_repo, "claim-exit.txt");
        Assert.True(File.Exists(claimExit), "the stand-in agent never reached the CLI claim");
        Assert.Equal("exit=0", (await File.ReadAllTextAsync(claimExit, CancellationToken.None)).Trim());

        var rolled = Assert.Single(state.History);
        Assert.Equal(SessionOutcome.RolledOver, rolled.Outcome);

        // ── THE BUG, both halves ────────────────────────────────────────────────────────────────
        // git ground truth for this session's own window, then the record that used to be empty.
        var truth = (await ProcessRunner.RunAsync("git", ["log", "--oneline", $"{startHead}..HEAD"], _repo,
            TimeSpan.FromSeconds(30), CancellationToken.None)).Output.Trim();
        var endHead = (await ProcessRunner.RunAsync("git", ["rev-parse", "HEAD"], _repo,
            TimeSpan.FromSeconds(30), CancellationToken.None)).Output.Trim();
        Assert.Contains("work done before the ceiling", truth, StringComparison.Ordinal);
        Assert.NotEmpty(rolled.NewCommits);
        Assert.Contains(rolled.NewCommits, c => c.Contains("work done before the ceiling", StringComparison.Ordinal));
        Assert.Contains("H0.1", rolled.NewlyDone);

        using var store = new SqliteRunStore(Path.Combine(plan.StateDir, "run.db"),
            NullLogger<SqliteRunStore>.Instance);

        // …and the LEDGER agrees, which is the column every board, report and push reads. This is
        // the number that was 0 on 100% of rollovers.
        var row = Assert.Single(store.QuerySessions(state.RunId), s => s.Number == rolled.Number);
        Assert.Equal("RolledOver", row.Outcome);
        Assert.True(row.CommitCount > 0, $"commit_count on a rolled-over session that committed was {row.CommitCount}");

        // The engine-side stamp ran for the rollover's claim too, and — because NewCommits is no
        // longer empty — the checkpoint row carries a REAL sha instead of the "-" placeholder that
        // is all an empty commit list could ever produce.
        var cps = store.GetCheckpoints(state.RunId).ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith("DONE", cps["H0.1"].Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(".conductor/evidence/H0/rollover-proof.md", cps["H0.1"].Evidence);
        Assert.NotEqual("-", cps["H0.1"].Commit);
        Assert.StartsWith(cps["H0.1"].Commit, endHead, StringComparison.OrdinalIgnoreCase);

        // ── AND THE SEMANTICS ARE UNCHANGED ─────────────────────────────────────────────────────
        // No attempt burned: a rollover is not a failed attempt at the stage.
        Assert.Equal(0, state.AttemptsThisStage);
        // No gate battery: the rollover still returns before the verdict pass that would run it.
        Assert.Empty(rolled.GateSummary);
        Assert.Empty(store.QueryGatesForSession(state.RunId, rolled.Number));
        // Nor did it quietly become a verdict: the outcome is still RolledOver, not Advanced.
        Assert.NotEqual(SessionOutcome.Advanced, rolled.Outcome);

        // The claim is queued for confirmation rather than dropped — without this a checkpoint
        // claimed by a rolled-over session could never reach DONE ✓ from either side (SF0.2).
        Assert.Contains("H0.1", state.PendingConfirmation);
    }
}
