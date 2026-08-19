using System.Text.Json;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS4.1 — the holdout gate class: a gate the coding agent cannot see, name, discover or run.
/// </summary>
/// <remarks>
/// <para>These tests are written against the runner's OUTPUT, not against a list of redaction call
/// sites, because that is where the guarantee actually lives. KS4.1's surface map counted thirty-odd
/// places a gate name or command reaches the agent — the fix prompt, <c>conductor gate</c>,
/// <c>conductor journey</c>, <c>session_detail</c>, arbitrary <c>run_query</c> SQL over the gates
/// table, REPORT.md, state.json, the spill filenames, conductor.log, the tools block in every
/// composed prompt. Auditing thirty surfaces produces thirty tests and one forgotten surface. So the
/// runner returns a <see cref="GateResult"/> that never had the secret in it, and the tests below
/// assert THAT, plus the two surfaces that read <c>plan.Gates</c> directly and therefore bypass it.</para>
/// <para>The live end-to-end proof — a gaming agent that passes the visible gates, fails the holdout
/// and goes red, with the composed prompt and the whole working tree grepped afterwards — is in
/// <c>HarnessTests.Holdout.cs</c>.</para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class KS4_1HoldoutGatesTests
{
    private const string SecretName = "hidden-invariant-check";
    private const string SecretCommand = "if (Test-Path zzz-unicorn-artifact.txt) { exit 0 } else { exit 1 }";

    private static GateConfig Holdout(string command = "exit 0", string name = SecretName) => new()
    {
        Name = name,
        Command = command,
        Visibility = GateVisibility.Holdout,
        TimeoutMinutes = 1,
    };

    private static PlanConfig Plan(params GateConfig[] gates) => new()
    {
        Repo = Path.GetTempPath(),
        Gates = [.. gates],
    };

    // ── the class exists, and it is engine-only ──

    /// <summary>The checkpoint in one assertion: the DEFAULT of every route into the runner excludes
    /// holdout gates, and only an explicit opt-in runs them. <c>conductor gate</c>, the lane merge
    /// battery and every test helper take the default.</summary>
    [Fact]
    public async Task HoldoutGatesDoNotRunUnlessTheEngineAsksForThem()
    {
        var plan = Plan(
            new GateConfig { Name = "visible", Command = "exit 0", TimeoutMinutes = 1 },
            Holdout());

        var agentReachable = await GateRunner.RunAllAsync(plan);
        Assert.Equal("visible", Assert.Single(agentReachable).Name);

        var engine = await GateRunner.RunAllAsync(plan, includeHoldout: true);
        Assert.Equal(2, engine.Count);
        Assert.Contains(engine, r => r.Holdout);
    }

    /// <summary>The engine's verdict-time battery is the ONLY caller that opts in. Asserted against
    /// the source, because a second <c>includeHoldout: true</c> added anywhere else would silently
    /// hand the class away and no behavioural test would notice.</summary>
    [Fact]
    public void OnlyTheVerdictTimeBatteryOptsIntoHoldoutGates()
    {
        var src = Directory.EnumerateFiles(RepoRoot("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("includeHoldout:", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();

        Assert.Equal("GateOrchestrator.cs", Assert.Single(src));
    }

    // ── the result carries no secret ──

    [Fact]
    public async Task AFailingHoldoutIsAnonymousInItsOwnResult()
    {
        var plan = Plan(Holdout("Write-Output 'the assertion that failed'; exit 7"));
        var r = Assert.Single(await GateRunner.RunAllAsync(plan, includeHoldout: true));

        Assert.True(r.Holdout);
        Assert.False(r.Passed);
        Assert.Equal(GateVisibility.RedactedName, r.Name);
        Assert.DoesNotContain(SecretName, r.Name, StringComparison.Ordinal);
        // The command's own output is the loudest leak there is: it is the assertion message.
        Assert.DoesNotContain("the assertion that failed", r.Tail, StringComparison.Ordinal);
        Assert.Equal(GateVisibility.FailureNotice, r.Tail);
        // Exit code is normalised: 7 vs 3 vs 1 distinguishes which check fired.
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public async Task APassingHoldoutSaysNothingBeyondHavingRun()
    {
        var plan = Plan(Holdout("Write-Output 'checked 412 invariants in module X'; exit 0"));
        var r = Assert.Single(await GateRunner.RunAllAsync(plan, includeHoldout: true));

        Assert.True(r.Passed);
        Assert.Equal(GateVisibility.RedactedName, r.Name);
        Assert.Equal(GateVisibility.PassNotice, r.Tail);
        Assert.DoesNotContain("412", r.Tail, StringComparison.Ordinal);
    }

    /// <summary>The retry pass (SC4.1) re-runs every failed required gate and prepends the FIRST
    /// attempt's exit code to the tail. For a holdout that put the redacted exit code straight back.</summary>
    [Fact]
    public async Task TheRetryPreambleDoesNotPutTheExitCodeBack()
    {
        var plan = Plan(Holdout("Write-Output 'boom'; exit 9"));
        var r = Assert.Single(await GateRunner.RunAllAsync(plan, includeHoldout: true));

        Assert.True(r.Retried);
        Assert.Equal(GateVisibility.FailureNotice, r.Tail);
        Assert.DoesNotContain("exit 9", r.Tail, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", r.Tail, StringComparison.Ordinal);
    }

    // ── the run log is inside the agent's working tree ──

    /// <summary>conductor.log lives in <c>.conductor/</c> — inside the repo the agent is editing, and
    /// one <c>Read</c> away. It is also the ONE place the exact command line was printed verbatim
    /// (<c>gate {name}: {command}</c>). A holdout runs silently and logs one redacted line.</summary>
    [Fact]
    public async Task TheProgressLogNeverNamesAHoldoutOrItsCommand()
    {
        var lines = new List<string>();
        var plan = Plan(
            new GateConfig { Name = "visible", Command = "exit 0", TimeoutMinutes = 1 },
            Holdout(SecretCommand));

        await GateRunner.RunAllAsync(plan, lines.Add, includeHoldout: true);
        var log = string.Join("\n", lines);

        Assert.DoesNotContain(SecretName, log, StringComparison.Ordinal);
        Assert.DoesNotContain("zzz-unicorn-artifact", log, StringComparison.Ordinal);
        // …while the visible gate is logged exactly as before: this must not be a blanket gag.
        Assert.Contains("gate visible: exit 0", log, StringComparison.Ordinal);
        Assert.Contains($"gate {GateVisibility.RedactedName}: FAIL", log, StringComparison.Ordinal);
    }

    // ── every renderer downstream inherits the anonymity ──

    /// <summary>The four name-bearing renderers and the fix-prompt spill, over a battery whose
    /// holdout failed. None of them is redaction-aware; none of them needs to be.</summary>
    [Fact]
    public async Task NoDownstreamRendererCanLeakWhatTheResultDoesNotCarry()
    {
        var plan = Plan(
            new GateConfig { Name = "visible", Command = "exit 0", TimeoutMinutes = 1 },
            Holdout(SecretCommand));
        var results = await GateRunner.RunAllAsync(plan, includeHoldout: true);

        var stateDir = Path.Combine(Path.GetTempPath(), $"ks41-spill-{Guid.NewGuid():N}");
        try
        {
            var rendered = string.Join("\n\n", [
                GateRunner.Summary(results),
                GateRunner.Token(results),
                GateRunner.ConfirmationBasis(2, results),
                GateRunner.FailureDetails(results),
                GateFailureSpill.Render(results, stateDir, 1),
            ]);

            Assert.DoesNotContain(SecretName, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("zzz-unicorn-artifact", rendered, StringComparison.Ordinal);
            // The battery is still honestly RED, and the session is still told a holdout failed.
            Assert.Contains("gates RED", rendered, StringComparison.Ordinal);
            Assert.Contains(GateVisibility.FailureNotice, rendered, StringComparison.Ordinal);

            // The spill writes the full output to a file named after the gate. Both halves redacted.
            var spilled = Directory.Exists(Path.Combine(stateDir, GateFailureSpill.DirName))
                ? Directory.GetFiles(Path.Combine(stateDir, GateFailureSpill.DirName))
                : [];
            foreach (var f in spilled)
            {
                var body = await File.ReadAllTextAsync(f);
                Assert.DoesNotContain(SecretName, Path.GetFileName(f), StringComparison.Ordinal);
                Assert.DoesNotContain(SecretName, body, StringComparison.Ordinal);
                Assert.DoesNotContain("zzz-unicorn-artifact", body, StringComparison.Ordinal);
            }
        }
        finally { TestTemp.DeleteTree(stateDir); }
    }

    /// <summary>The verdict still goes red. The whole point is that anonymity costs the measurement
    /// nothing: <see cref="GateRunner.AllRequiredPassed"/> is what
    /// <c>SessionEvidence.GatesGreen</c> is computed from.</summary>
    [Fact]
    public async Task AFailedHoldoutStillTurnsTheBatteryRed()
    {
        var plan = Plan(
            new GateConfig { Name = "visible", Command = "exit 0", TimeoutMinutes = 1 },
            Holdout("exit 1"));

        Assert.True(GateRunner.AllRequiredPassed(await GateRunner.RunAllAsync(plan)));
        Assert.False(GateRunner.AllRequiredPassed(await GateRunner.RunAllAsync(plan, includeHoldout: true)));
    }

    // ── the two surfaces that read plan.Gates directly ──

    /// <summary>The tools block goes into EVERY composed prompt, and it pastes a gate's command
    /// verbatim as the <c>conductor bg start</c> sample — picked by searching gate commands for
    /// "test". A holdout whose command contains "test" was one substring away from being published
    /// in the one block every template includes.</summary>
    [Fact]
    public void TheToolsBlockNeverSamplesAHoldoutCommand()
    {
        var plan = Plan(Holdout("dotnet test ./Secret.slnx --filter HoldoutOnly"));
        var block = ToolContract.Render(plan);

        Assert.DoesNotContain("Secret.slnx", block, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretName, block, StringComparison.Ordinal);
        Assert.Contains("<your long command>", block, StringComparison.Ordinal);
    }

    // ── the plan file itself: the one thing the runner cannot redact ──

    [Fact]
    public void AHoldoutFileInsideTheRepoIsRefusedByName()
    {
        using var rig = new PlanRig();
        var inside = Path.Combine(rig.Repo, "holdouts.json");
        File.WriteAllText(inside, "[]");

        var ex = Assert.Throws<InvalidOperationException>(() => rig.Load(holdoutGates: inside));
        Assert.Contains("inside the repo working tree", ex.Message, StringComparison.Ordinal);
        Assert.Contains(inside, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInlineHoldoutGateInAnInRepoPlanIsRefusedByName()
    {
        using var rig = new PlanRig();
        var ex = Assert.Throws<InvalidOperationException>(() => rig.Load(inlineHoldout: true));
        Assert.Contains(SecretName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("visibility=holdout inside a plan file that lives in the repo", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Fail closed. A holdout file that has been moved or deleted must stop the run, not
    /// quietly reduce the battery to its visible gates and report green — that is the vacuous-gate
    /// shape KS6.2 and KS6.3 each caught once already.</summary>
    [Fact]
    public void AMissingHoldoutFileIsRefusedRatherThanTreatedAsEmpty()
    {
        using var rig = new PlanRig();
        var ex = Assert.Throws<InvalidOperationException>(
            () => rig.Load(holdoutGates: Path.Combine(rig.Outside, "not-there.json")));
        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHoldoutFileOutsideTheRepoLoadsAndItsGatesAreHoldouts()
    {
        using var rig = new PlanRig();
        var outside = Path.Combine(rig.Outside, "holdouts.json");
        // No "visibility" key in the file: the FILE is the declaration, so a forgotten key cannot
        // quietly produce a visible gate carrying a secret command.
        File.WriteAllText(outside, $$"""[{ "name": "{{SecretName}}", "command": "exit 0" }]""");

        var plan = rig.Load(holdoutGates: outside);
        var gate = plan.Gates.Single(g => g.Name == SecretName);
        Assert.True(gate.IsHoldout);
    }

    [Fact]
    public void AnUnknownVisibilityIsRefusedByName()
    {
        var plan = Plan(new GateConfig { Name = "g", Command = "exit 0", Visibility = "hidden" });
        Assert.Contains(plan.CollectErrors(),
            e => e.Contains("visibility 'hidden'", StringComparison.Ordinal));
    }

    [Fact]
    public void AVisibleGateMayNotWearTheRedactedName()
    {
        var plan = Plan(new GateConfig { Name = GateVisibility.RedactedName, Command = "exit 0" });
        Assert.Contains(plan.CollectErrors(),
            e => e.Contains("reserved for redacted holdout results", StringComparison.Ordinal));
    }

    // ── helpers ──

    private static string RepoRoot(string sub)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, sub);
    }

    /// <summary>A git-less repo dir, a sibling dir outside it, and a plan file written into the repo
    /// — the exact geometry the location rule is about.</summary>
    private sealed class PlanRig : IDisposable
    {
        public string Repo { get; }
        public string Outside { get; }

        public PlanRig()
        {
            var id = Guid.NewGuid().ToString("N");
            Repo = Path.Combine(Path.GetTempPath(), $"ks41-repo-{id}");
            Outside = Path.Combine(Path.GetTempPath(), $"ks41-outside-{id}");
            Directory.CreateDirectory(Repo);
            Directory.CreateDirectory(Outside);
            File.WriteAllText(Path.Combine(Repo, "TRACKER.md"), "# t\n");
        }

        public PlanConfig Load(string? holdoutGates = null, bool inlineHoldout = false)
        {
            var plan = new PlanConfig
            {
                Name = "rig",
                Repo = Repo,
                Tracker = "TRACKER.md",
                Stages = { new StageConfig { Id = "S0", Title = "s", Sessions = 1 } },
                Agent = new AgentConfig { Command = "cmd.exe", Args = { "/c", "{prompt}" } },
                HoldoutGates = holdoutGates,
            };
            if (inlineHoldout) plan.Gates.Add(Holdout());

            var path = Path.Combine(Repo, "conductor.plan.json");
            File.WriteAllText(path, JsonSerializer.Serialize(plan, PlanConfig.JsonOpts));
            return PlanConfig.Load(path);
        }

        public void Dispose()
        {
            TestTemp.DeleteTree(Repo);
            TestTemp.DeleteTree(Outside);
        }
    }
}
