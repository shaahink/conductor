using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class B11_2DoctorAndCompletionTests
{
    // --- RunState pending-field behavior (kept for coverage; not specific to DoctorCommand,
    // which M8.1 repurposed into a health check — see DoctorCommandTests.cs) ---

    [Fact]
    public void Doctor_AwaitingOwner_HasCorrectReasonText()
    {
        var state = new RunState
        {
            PlanName = "test",
            Status = RunStatus.AwaitingOwner,
            CurrentStage = "S1",
            AwaitingOwnerReason = AwaitingOwnerReason.Budget,
        };

        Assert.Equal(RunStatus.AwaitingOwner, state.Status);
        Assert.Equal(AwaitingOwnerReason.Budget, state.AwaitingOwnerReason);
    }

    [Fact]
    public void Doctor_NonePending_NoFixNoResume()
    {
        var state = new RunState
        {
            PlanName = "test",
            Status = RunStatus.Idle,
            CurrentStage = "S1",
        };

        Assert.Null(state.PendingFix);
        Assert.Null(state.PendingResume);
        Assert.Null(state.PendingPhaseGate);
        Assert.Null(state.PendingAudit);
    }

    [Fact]
    public void Doctor_HasPendingFixAndResume_InStateCorrectly()
    {
        var state = new RunState
        {
            PlanName = "test",
            Status = RunStatus.Idle,
            CurrentStage = "S1",
            PendingFix = new PendingFix { FromSession = 5, GateFailures = "build, tests" },
            PendingResume = new PendingResume { FromSession = 3, Reason = "timed out", ResumeCount = 1 },
        };

        Assert.NotNull(state.PendingFix);
        Assert.Equal(5, state.PendingFix.FromSession);
        Assert.NotNull(state.PendingResume);
        Assert.Equal(3, state.PendingResume.FromSession);
    }

    [Fact]
    public void Doctor_HasPendingPhaseGateAndAudit()
    {
        var state = new RunState
        {
            PlanName = "test",
            Status = RunStatus.Idle,
            CurrentStage = "S1",
            PendingPhaseGate = new PendingPhaseGate { StageId = "S1", StageStartHead = "abc" },
            PendingAudit = new PendingAudit { StageId = "S1", StageStartHead = "abc" },
        };

        Assert.NotNull(state.PendingPhaseGate);
        Assert.Equal("S1", state.PendingPhaseGate.StageId);
        Assert.NotNull(state.PendingAudit);
    }

    // --- Completion output ---

    [Fact]
    public void Completion_Powershell_ContainsAllVerbs()
    {
        var output = Conductor.Commands.CompletionCommand.GeneratePowerShell();

        Assert.Contains("Register-ArgumentCompleter", output);
        Assert.Contains("run", output);
        Assert.Contains("doctor", output);
        Assert.Contains("completion", output);
        Assert.Contains("new-plan", output);
        Assert.Contains("tasks", output);
        Assert.Contains("journey", output);
        Assert.Contains("@('powershell','bash')", output);
        Assert.Contains("$newPlanOpts =", output);
        Assert.Contains("'new-plan'", output);
    }

    [Fact]
    public void Completion_Bash_ContainsCompleteDirective()
    {
        var output = Conductor.Commands.CompletionCommand.GenerateBash();

        Assert.Contains("complete -F _conductor_completion conductor", output);
        Assert.Contains("compgen -W", output);
    }

    // FU-B11-1: Completion verb list must be exhaustive — every command registered in Program.cs
    // must appear in the completion output so tab-complete doesn't silently break when a new command
    // is added. Also verifies the count stays in parity.
    //
    // SC8.3: the expected set is now READ OFF Program.cs instead of hand-typed here. It was a
    // hand-maintained list, which meant it measured nothing: `version` shipped in SC8.1 missing from
    // BOTH completion lists AND from this set, and the test stayed green the whole time. A new verb
    // was three places; it is now two, and the third is enforced.
    [Fact]
    public void Completion_ContainsAllRegisteredVerbs_Exhaustive()
    {
        var expectedVerbs = RegisteredVerbs();
        Assert.True(expectedVerbs.Count > 30, $"only {expectedVerbs.Count} verbs parsed out of Program.cs — the scan is broken, not the completion");

        var ps = Conductor.Commands.CompletionCommand.GeneratePowerShell();
        var bash = Conductor.Commands.CompletionCommand.GenerateBash();

        // Extract the PowerShell verb string: "$verbs = @('run status ...')"
        // and verify every expected verb is present.
        var regexTimeout = TimeSpan.FromSeconds(2);
        var regexOpts = System.Text.RegularExpressions.RegexOptions.ExplicitCapture;
        var psMatch = System.Text.RegularExpressions.Regex.Match(ps,
            @"\$verbs\s*=\s*@\('(?<verbs>[^']+)'", regexOpts, regexTimeout);
        Assert.True(psMatch.Success, "PowerShell completion missing $verbs definition");
        var psVerbs = psMatch.Groups["verbs"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var verb in expectedVerbs)
            Assert.Contains(verb, psVerbs);

        // Verify no stale verbs in completion that aren't registered
        var stale = psVerbs.Where(v => !expectedVerbs.Contains(v)).ToList();
        Assert.Empty(stale);

        // Verify PowerShell count matches expected
        Assert.Equal(expectedVerbs.Count, psVerbs.Length);

        // Bash completion: verify all verbs in the compgen -W list
        foreach (var verb in expectedVerbs)
            Assert.Contains(verb, bash);

        // Extract bash verb count from compgen -W
        var bashMatch = System.Text.RegularExpressions.Regex.Match(bash,
            @"compgen\s+-W\s+""(?<verbs>[^""]+)""", regexOpts, regexTimeout);
        Assert.True(bashMatch.Success, "Bash completion missing compgen -W definition");
        var bashVerbs = bashMatch.Groups["verbs"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expectedVerbs.Count, bashVerbs.Length);
    }

    /// <summary>Every verb <c>Program.cs</c> registers and does not hide. Source-scanned rather than
    /// reflected because Spectre's <c>CommandApp</c> keeps its configuration private, and the source
    /// is the thing a future session will edit.</summary>
    private static HashSet<string> RegisteredVerbs()
    {
        var program = Path.Combine(RepoRoot(), "src", "Conductor", "Program.cs");
        var verbs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(program))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line,
                @"AddCommand<\w+>\(""(?<verb>[a-z][a-z0-9-]*)""\)",
                System.Text.RegularExpressions.RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
            if (!m.Success) continue;
            if (line.Contains(".IsHidden()", StringComparison.Ordinal)) continue;
            verbs.Add(m.Groups["verb"].Value);
        }
        return verbs;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate repo root (Conductor.slnx)");
    }
}
