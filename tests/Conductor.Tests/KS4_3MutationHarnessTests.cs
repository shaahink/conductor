using Conductor.Core;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// KS4.3's headline exit, driven live: a seeded agent writes code and writes its own tests, the
/// suite passes, the mutation gate EXITS 0 — and the session comes back RED, because three of the
/// four deliberate breakages it planted in that code went unnoticed.
/// </summary>
/// <remarks>
/// <para>Two real sessions of the real orchestrator over a real temp repo, through a real plan load.
/// Session 1 is the gaming move — the whole point of the class is that it is a move nobody has to
/// lie to make: the tests exist, they run, they pass. Session 2 is the fix session, and what it is
/// HANDED is the checkpoint: a brief that names the class, the score, the bar and every surviving
/// mutant by file and line, rather than a tail from a command that reported success.</para>
/// <para><b>The mutation runner here is a stand-in and says so.</b> It reads the assertions the agent
/// wrote and marks a mutant killed when one covers it, which is what Stryker does and takes minutes
/// to do. What is NOT stand-in is everything the checkpoint is about: the plan load, the class, the
/// diff scoping against a real git history, the arithmetic, the verdict and both fix-brief
/// renderers. The real Stryker run over this repo's own source is recorded separately in the
/// checkpoint's evidence, because it belongs in a background child and not in a unit test.</para>
/// <para>The contrast leg is what makes it falsifiable: the same rig where session 1 writes the
/// assertions that kill all four mutants runs green. So the red is the class's doing, not the rig's.</para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class KS4_3MutationHarnessTests : IDisposable
{
    /// <summary>The file the agent changes, and therefore the only file in the gate's scope.</summary>
    private const string Source = "src/Calc.cs";

    /// <summary>The mutants nobody kills in the gaming leg — that session's one assertion covers the
    /// operator on line 7 and no other. Lines, not just a filename, because "which line asserts
    /// nothing" is the whole of what the fix session needs.</summary>
    private static readonly string[] SurvivorLines = ["src/Calc.cs:8", "src/Calc.cs:9", "src/Calc.cs:10"];

    private readonly string _repo;

    public KS4_3MutationHarnessTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"ks43-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);

        Git("init", "-b", "main");
        Git("config", "user.email", "harness@test");
        Git("config", "user.name", "Harness Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# KS4.3 mutation rig");
        // The report is an artifact of the gate's own run, and the markers are the rig's. Both out of
        // the way, so the verdict rests on the class and not on a dirty working tree.
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), ".conductor/\nstep*.marker\nmutation-report.json\n");
        File.WriteAllText(Path.Combine(_repo, "mutate.ps1"), MutationRunner);
        for (var i = 1; i <= 2; i++)
            File.WriteAllText(Path.Combine(_repo, $"tracker-{i}.md"), Tracker(i));
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), Tracker(0));
        Git("add", "-A");
        Git("commit", "-m", "chore: initial commit", "--no-gpg-sign");
        // The gate's diff base. A real plan names its integration branch here; the rig needs a fixed
        // point that predates every session, because the agent COMMITS its work and an uncommitted
        // diff would be empty by the time the battery runs.
        Git("tag", "rig-base");
    }

    public void Dispose() => TestTemp.DeleteTree(_repo);

    /// <summary>The checkpoint's exit criterion. Session 1 does everything a delivering session does
    /// — it writes code, it writes tests, the suite passes, it claims a checkpoint, it commits — and
    /// it is red, because the tests it wrote cannot tell that code from broken code.</summary>
    [Fact]
    public async Task TestsThatCannotFailFlipTheVerdictEvenThoughEveryGateExitsZero()
    {
        var history = await RunAsync(gameIt: true);

        // Everything a delivery is made of is present…
        Assert.NotEmpty(history[0].NewlyDone);
        Assert.NotEmpty(history[0].NewCommits);
        // …the suite really did pass…
        Assert.Contains("suite:OK", history[0].GateSummary, StringComparison.Ordinal);
        // …and the verdict is red anyway, spelled as the class and not as a failure.
        Assert.Equal(SessionOutcome.GatesRed, history[0].Outcome);
        Assert.Contains($"mutation:{GateClass.MutationGlyph}", history[0].GateSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation:FAIL", history[0].GateSummary, StringComparison.Ordinal);
    }

    /// <summary>What the next session is TOLD. A brief that said "gate mutation failed" would send it
    /// looking for a failing assertion that does not exist — the gate exited 0, and so did the suite.
    /// This is the composed prompt the second session's process was actually handed, read off disk.
    /// </summary>
    [Fact]
    public async Task TheFixSessionIsHandedTheClassByNameTheScoreTheBarAndEveryLivingMutant()
    {
        await RunAsync(gameIt: true);

        var promptPath = Path.Combine(_repo, ".conductor", "logs", BgLogs.PromptName(2));
        Assert.True(File.Exists(promptPath), $"no composed prompt at {promptPath}");
        var prompt = await File.ReadAllTextAsync(promptPath);

        Assert.Contains(GateClass.MutationGlyph, prompt, StringComparison.Ordinal);
        Assert.Contains("EXITED 0", prompt, StringComparison.Ordinal);
        Assert.Contains("25%", prompt, StringComparison.Ordinal);            // the score it got
        Assert.Contains("60%", prompt, StringComparison.Ordinal);            // the bar it missed
        Assert.All(SurvivorLines, l => Assert.Contains(l, prompt, StringComparison.Ordinal));
        // …and the one it DID cover is not in the list, or the brief would send it to a line that is
        // already asserted on.
        Assert.DoesNotContain("src/Calc.cs:7", prompt, StringComparison.Ordinal);
        // And it is NOT told the other class's story: the fix a regression asks for is the opposite
        // one — put the check back — and this session removed nothing.
        Assert.DoesNotContain("PASS-TO-PASS", prompt, StringComparison.Ordinal);
    }

    /// <summary>The loop closes: session 2 writes assertions that kill the mutants and the run is
    /// green — the class is a bar that can be cleared, not a trap that stays sprung.</summary>
    [Fact]
    public async Task AssertionsThatKillTheMutantsMakeTheRunGreenAgain()
    {
        var history = await RunAsync(gameIt: true);

        Assert.Equal(2, history.Count);
        var trace = string.Join(" | ", history.Select(h => $"#{h.Number} {h.Kind} {h.Outcome} [{h.GateSummary}]"));
        Assert.True(history[1].Outcome != SessionOutcome.GatesRed, trace);
        Assert.True(history[1].GateSummary.Contains("mutation:OK", StringComparison.Ordinal), trace);
    }

    /// <summary>The falsifier. Same rig, same gate, same threshold — session 1 writes the assertions
    /// that kill all four mutants, and nothing goes red. Without this leg the tests above would pass
    /// just as well against a gate that fails every first session.</summary>
    [Fact]
    public async Task TheSameRigStaysGreenForASessionWhoseTestsCanActuallyFail()
    {
        var history = await RunAsync(gameIt: false);

        Assert.All(history, r => Assert.NotEqual(SessionOutcome.GatesRed, r.Outcome));
        Assert.All(history, r => Assert.Contains("mutation:OK", r.GateSummary, StringComparison.Ordinal));
    }

    // ── the rig ──

    private async Task<List<SessionRecord>> RunAsync(bool gameIt)
    {
        var plan = new PlanConfig
        {
            Name = "MutationRig",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 6 } },
            Agent = new AgentConfig
            {
                // KS4.2 measured this and it cost a debugging round: NOT cmd.exe. The prompt is an
                // argument, cmd.exe caps a command line at 8191 characters, and a fix prompt here is
                // past that before the gate block is added.
                Command = "powershell.exe",
                Args = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(_repo, "fake-agent.ps1"), "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            // KS4.2 measured this too: on by default, and a verify session takes a slot and buys no
            // gate battery — so "session 2" would be the verifier and the rig would test nothing.
            VerifyEachDelivery = false,
            Gates =
            {
                // An ordinary suite, and it PASSES in both legs. Its whole job here is to be the gate
                // everybody already trusts, saying OK while the code underneath is untested.
                new GateConfig { Name = "suite", Command = "exit 0", TimeoutMinutes = 1 },
                new GateConfig
                {
                    Name = "mutation",
                    Command = "powershell -NoProfile -ExecutionPolicy Bypass -File mutate.ps1",
                    TimeoutMinutes = 2,
                    Class = GateClass.Mutation,
                    Mutation = new MutationConfig
                    {
                        Format = MutationConfig.StrykerJson,
                        Path = "mutation-report.json",
                        Threshold = 60,
                        DiffBase = "rig-base",
                    },
                },
            },
        };
        plan.Report.Commit = false;

        // Through the FILE, so PlanConfig.Load runs and the class's load-time rules with it.
        var planPath = Path.Combine(_repo, "conductor.plan.json");
        await File.WriteAllTextAsync(planPath, System.Text.Json.JsonSerializer.Serialize(plan, PlanConfig.JsonOpts));
        var loaded = PlanConfig.Load(planPath);
        Assert.True(loaded.Gates.Single(g => g.Name == "mutation").IsMutation);

        await File.WriteAllTextAsync(Path.Combine(_repo, "fake-agent.ps1"), AgentScript(gameIt));
        Git("add", "-A");
        Git("commit", "-m", "chore: rig", "--no-gpg-sign");

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(loaded, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: false, MaxSessions: 2), consoleSink: false);

        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);
        Assert.Equal(0, code);
        return state.History;
    }

    /// <summary>The stand-in mutation runner: four mutants planted in <see cref="Source"/>, each
    /// killed when the agent's own assertions cover it. It exits 0 whatever the score, exactly as a
    /// real Stryker invocation does when the plan does not pass <c>--break-at</c> — the bar lives in
    /// the engine, not in the command the agent can edit.</summary>
    private static string MutationRunner => string.Join("\r\n",
    [
        "$ErrorActionPreference = 'Stop'",
        "$names = @('add','sub','mul','div')",
        "$lines = @{ 'add' = 7; 'sub' = 8; 'mul' = 9; 'div' = 10 }",
        "$killed = @()",
        "if (Test-Path assertions.txt) { $killed = @(Get-Content assertions.txt) }",
        "$parts = @()",
        "foreach ($n in $names) {",
        "  if ($killed -contains $n) { $status = 'Killed' } else { $status = 'Survived' }",
        "  $parts += ('{\"id\":\"' + $n + '\",\"mutatorName\":\"Arithmetic operator\",\"status\":\"' + $status + '\",\"location\":{\"start\":{\"line\":' + $lines[$n] + ',\"column\":1}}}')",
        "}",
        "$json = '{\"schemaVersion\":\"1\",\"files\":{\"" + Source + "\":{\"language\":\"cs\",\"mutants\":[' + ($parts -join ',') + ']}}}'",
        "Set-Content -Encoding ascii mutation-report.json $json",
        "Write-Output ('mutation run complete: ' + $names.Count + ' mutants planted')",
        "exit 0",
        "",
    ]);

    /// <summary>Two sessions in one script, branching on a marker. BOTH do what a delivering session
    /// does — write the source, write tests, claim the checkpoint, commit, report a SESSION-RESULT.
    /// The only difference is whether the tests they wrote can fail.</summary>
    private static string AgentScript(bool gameIt) => string.Join("\r\n",
    [
        "param([string]$Prompt)",
        "function Write-Source {",
        "  New-Item -ItemType Directory -Force src | Out-Null",
        "  @('namespace Rig;','','public static class Calc','{'," +
            "'    // four arithmetic operators, four mutants','    public static int Add(int a, int b) => a + b;'," +
            "'    public static int Sub(int a, int b) => a - b;','    public static int Mul(int a, int b) => a * b;'," +
            "'    public static int Div(int a, int b) => a / b;','}') | Set-Content -Encoding ascii src/Calc.cs",
        "}",
        "function Claim([int]$n) {",
        "  Write-Output ('{\"type\":\"text\",\"part\":{\"text\":\"Delivering H0.' + $n + '. Suite green.\"}}')",
        "  Write-Output '{\"type\":\"step_finish\",\"part\":{\"cost\":0.00042,\"tokens\":{\"input\":350,\"output\":120,\"reasoning\":80,\"cache\":{\"read\":0}}}}'",
        "  Copy-Item \"tracker-$n.md\" TRACKER.md -Force",
        "  git add -A 2>&1 | Out-Null",
        "  git commit -m \"feat: deliver H0.$n\" --no-gpg-sign 2>&1 | Out-Null",
        "}",
        "if (Test-Path step2.marker) {",
        // The fix session: assertions that actually distinguish the code from broken code.
        "  Write-Source",
        "  @('add','sub','mul','div') | Set-Content -Encoding ascii assertions.txt",
        "  Claim 2",
        "} else {",
        "  New-Item -ItemType File step2.marker | Out-Null",
        "  Write-Source",
        // The gaming move, or its honest twin. Either way the suite exits 0 and the tests exist.
        .. gameIt
            ? new[] { "  @('add') | Set-Content -Encoding ascii assertions.txt" }
            : ["  @('add','sub','mul','div') | Set-Content -Encoding ascii assertions.txt"],
        "  Claim 1",
        "}",
        "exit 0",
        "",
    ]);

    /// <summary>The tracker after <paramref name="done"/> checkpoints have been claimed.</summary>
    private static string Tracker(int done)
    {
        var rows = string.Concat(Enumerable.Range(1, 2).Select(i =>
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
