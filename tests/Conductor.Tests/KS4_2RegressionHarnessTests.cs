using Conductor.Core;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// KS4.2's headline exit, driven live: a seeded agent deletes a check that was passing, its suite
/// exits 0 because the deleted check cannot fail, it claims its checkpoint and commits — and the
/// session comes back RED, with the class named in the record and in the brief the next session is
/// handed.
/// </summary>
/// <remarks>
/// <para>Three real sessions of the real orchestrator over a real temp repo, through a real plan
/// load. Session 1 is honest and its pass set becomes the baseline; session 2 deletes one check;
/// session 3 is the fix session, and what it is HANDED is the point — a fix brief that names the
/// class and the missing check, rather than an output tail from a command that reported success.</para>
/// <para>The contrast leg is what makes it falsifiable: the same rig where session 2 ADDS a check
/// instead of removing one runs green all the way through. So the red is the regression class's
/// doing, not the second session's.</para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class KS4_2RegressionHarnessTests : IDisposable
{
    /// <summary>The check the gaming session deletes. Distinctive enough that finding it in the fix
    /// brief cannot be a coincidence.</summary>
    private const string DeletedCheck = "Suite.TheKarvansaraInvariant";

    private readonly string _repo;

    public KS4_2RegressionHarnessTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"ks42-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);

        Git("init", "-b", "main");
        Git("config", "user.email", "harness@test");
        Git("config", "user.name", "Harness Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# KS4.2 regression rig");
        // Keeps the tree clean across the run, so the verdict rests on the class and not on a dirty
        // working tree — and keeps the session markers out of the agent's own commits.
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), ".conductor/\nstep*.marker\n");
        for (var i = 1; i <= 3; i++)
            File.WriteAllText(Path.Combine(_repo, $"tracker-{i}.md"), Tracker(i));
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), Tracker(0));
        Git("add", "-A");
        Git("commit", "-m", "chore: initial commit", "--no-gpg-sign");
    }

    public void Dispose() => TestTemp.DeleteTree(_repo);

    /// <summary>The checkpoint's exit criterion. Session 2 does everything a delivering session does
    /// — its suite exits 0, it claims a checkpoint, it commits — and it is red, because a check that
    /// passed in session 1 is not passing now.</summary>
    [Fact]
    public async Task DeletingAPassingCheckFlipsTheVerdictEvenThoughTheSuiteExitsZero()
    {
        var history = await RunAsync(deleteTheCheck: true);

        // Session 1: honest. The baseline is whatever this battery saw pass.
        Assert.NotEqual(SessionOutcome.GatesRed, history[0].Outcome);
        Assert.Contains("suite:OK", history[0].GateSummary, StringComparison.Ordinal);

        // Session 2: the gaming move. Everything a delivery is made of is present…
        Assert.NotEmpty(history[1].NewlyDone);
        Assert.NotEmpty(history[1].NewCommits);
        // …and the verdict is red anyway, spelled as the class and not as a failure.
        Assert.Equal(SessionOutcome.GatesRed, history[1].Outcome);
        Assert.Contains($"suite:{GateClass.Glyph}", history[1].GateSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("suite:FAIL", history[1].GateSummary, StringComparison.Ordinal);
    }

    /// <summary>What the next session is TOLD. A fix brief that said "gate suite failed" would send it
    /// looking for a failing assertion that does not exist — the gate exited 0. This is the composed
    /// prompt the third session's process was actually handed, read off disk.</summary>
    [Fact]
    public async Task TheFixSessionIsHandedTheClassByNameAndTheCheckThatWentMissing()
    {
        await RunAsync(deleteTheCheck: true);

        var promptPath = Path.Combine(_repo, ".conductor", "logs", BgLogs.PromptName(3));
        Assert.True(File.Exists(promptPath), $"no composed prompt at {promptPath}");
        var prompt = await File.ReadAllTextAsync(promptPath);

        Assert.Contains(GateClass.Glyph, prompt, StringComparison.Ordinal);
        Assert.Contains("PASS-TO-PASS", prompt, StringComparison.Ordinal);
        Assert.Contains(DeletedCheck, prompt, StringComparison.Ordinal);
        Assert.Contains("EXITED 0", prompt, StringComparison.Ordinal);
    }

    /// <summary>And the loop closes: session 3 puts the check back and the run is green again — the
    /// class is a measurement that can be satisfied, not a trap that stays sprung.</summary>
    [Fact]
    public async Task RestoringTheCheckMakesTheRunGreenAgain()
    {
        var history = await RunAsync(deleteTheCheck: true);

        Assert.Equal(3, history.Count);
        // The whole history in the failure message: a rig this long fails in ways that are only
        // legible from the sequence (a verify session taking a slot, an agent that never ran).
        var trace = string.Join(" | ", history.Select(h => $"#{h.Number} {h.Kind} {h.Outcome} [{h.GateSummary}] done={string.Join(",", h.NewlyDone)}"));
        Assert.True(history[2].Outcome != SessionOutcome.GatesRed, trace);
        Assert.True(history[2].GateSummary.Contains("suite:OK", StringComparison.Ordinal), trace);
    }

    /// <summary>The falsifier. Same rig, same gate, same three sessions — session 2 ADDS a check
    /// instead of deleting one, and nothing goes red. Without this leg the test above would pass just
    /// as well against a gate that fails every second session.</summary>
    [Fact]
    public async Task TheSameRigStaysGreenForASessionThatAddsACheckInsteadOfDeletingOne()
    {
        var history = await RunAsync(deleteTheCheck: false);

        Assert.All(history, r => Assert.NotEqual(SessionOutcome.GatesRed, r.Outcome));
        Assert.All(history, r => Assert.Contains("suite:OK", r.GateSummary, StringComparison.Ordinal));
    }

    // ── the rig ──

    private async Task<List<SessionRecord>> RunAsync(bool deleteTheCheck)
    {
        var plan = new PlanConfig
        {
            Name = "RegressionRig",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 6 } },
            Agent = new AgentConfig
            {
                // MEASURED, and it cost a debugging round: NOT cmd.exe. The prompt is passed as an
                // argument, cmd.exe caps a command line at 8191 characters, and a FIX prompt in this
                // rig is 8.1k before the gate block is added - so the third session died with "The
                // command line is too long" and looked like a broken rig. powershell.exe takes the
                // CreateProcess limit (32767) instead.
                Command = "powershell.exe",
                Args = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(_repo, "fake-agent.ps1"), "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            // MEASURED: on by default, and a multi-session rig has to say so. A verify session runs
            // after every delivery, and it is a session — it takes a slot, it runs this same fake
            // agent, and it buys no gate battery. Left on, "session 2" in the history below is the
            // verifier and the rig tests nothing it claims to.
            VerifyEachDelivery = false,
            Gates =
            {
                // The suite: its checks are the lines of a file, so deleting a check is deleting a
                // line — and the command exits 0 either way, exactly as a real runner does when the
                // test it would have failed on is no longer there to run.
                new GateConfig
                {
                    Name = "suite",
                    Command = "if (Test-Path checks.txt) { Get-Content checks.txt; exit 0 } else { exit 1 }",
                    TimeoutMinutes = 1,
                    Class = GateClass.Regression,
                    PassSet = new PassSetConfig { Format = PassSetConfig.Lines },
                },
            },
        };
        plan.Report.Commit = false;

        // Through the FILE, so PlanConfig.Load runs and the class's load-time rules with it.
        var planPath = Path.Combine(_repo, "conductor.plan.json");
        await File.WriteAllTextAsync(planPath, System.Text.Json.JsonSerializer.Serialize(plan, PlanConfig.JsonOpts));
        var loaded = PlanConfig.Load(planPath);
        Assert.True(loaded.Gates.Single().IsRegression);

        await File.WriteAllTextAsync(Path.Combine(_repo, "fake-agent.ps1"), AgentScript(deleteTheCheck));
        Git("add", "-A");
        Git("commit", "-m", "chore: rig", "--no-gpg-sign");

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(loaded, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: false, MaxSessions: 3), consoleSink: false);

        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);
        Assert.Equal(0, code);
        return state.History;
    }

    /// <summary>Three sessions in one script, branching on markers the run leaves behind. Every
    /// session does what a delivering session does: writes the suite's checks, claims its checkpoint,
    /// commits, reports a SESSION-RESULT. The ONLY difference in session 2 is which checks survive.
    /// </summary>
    private static string AgentScript(bool deleteTheCheck) => string.Join("\r\n",
    [
        "param([string]$Prompt)",
        "function Write-Checks {",
        $"  @('Suite.Alpha','{DeletedCheck}','Suite.Omega') | Set-Content -Encoding ascii checks.txt",
        "}",
        "function Claim([int]$n) {",
        "  Write-Output ('{\"type\":\"text\",\"part\":{\"text\":\"Delivering H0.' + $n + '. Suite green.\"}}')",
        "  Write-Output '{\"type\":\"step_finish\",\"part\":{\"cost\":0.00042,\"tokens\":{\"input\":350,\"output\":120,\"reasoning\":80,\"cache\":{\"read\":0}}}}'",
        "  Copy-Item \"tracker-$n.md\" TRACKER.md -Force",
        "  git add -A 2>&1 | Out-Null",
        "  git commit -m \"feat: deliver H0.$n\" --no-gpg-sign 2>&1 | Out-Null",
        "}",
        "if (Test-Path step3.marker) {",
        "  Write-Checks",                                   // the fix session puts the check back
        // …and in the contrast leg it must also keep the check session 2 ADDED: the baseline grew
        // when that battery passed clean, so dropping it here would be a regression of its own.
        .. deleteTheCheck ? [] : new[] { "  Add-Content -Encoding ascii checks.txt 'Suite.Extra'" },
        "  Claim 3",
        "} elseif (Test-Path step2.marker) {",
        "  New-Item -ItemType File step3.marker | Out-Null",
        // The gaming move, or its harmless twin. Either way the suite exits 0.
        .. deleteTheCheck
            ? new[] { "  @('Suite.Alpha','Suite.Omega') | Set-Content -Encoding ascii checks.txt" }
            : ["  Write-Checks", "  Add-Content -Encoding ascii checks.txt 'Suite.Extra'"],
        "  Claim 2",
        "} else {",
        "  New-Item -ItemType File step2.marker | Out-Null",
        "  Write-Checks",
        "  Claim 1",
        "}",
        "exit 0",
        "",
    ]);

    /// <summary>The tracker after <paramref name="done"/> checkpoints have been claimed.</summary>
    private static string Tracker(int done)
    {
        var rows = string.Concat(Enumerable.Range(1, 3).Select(i =>
            $"| H0.{i} | harness checkpoint {i} | {(i <= done ? "DONE" : "TODO")} | | |\n"));
        return "# Harness Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
               "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" + rows;
    }

    private void Git(params string[] args)
    {
        var r = ProcessRunner.Run("git", args, _repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed ({r.ExitCode}): {r.Output} {r.StdErr}");
    }
}
