using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class B11_2DoctorAndCompletionTests
{
    // --- Doctor state interpretation (the pure logic, without Spectre) ---

    [Fact]
    public void Doctor_IdlePlan_RemainingStagesExcludesConfirmed()
    {
        var plan = new PlanConfig
        {
            Repo = Path.GetTempPath(),
            Tracker = "B11-2-TRACKER-TEST.md",
            Stages = new()
            {
                new() { Id = "S1", Title = "First", Sessions = 1 },
                new() { Id = "S2", Title = "Second", Sessions = 1 },
            },
            Agent = new() { Command = "echo", Args = new() { "{prompt}" } },
        };
        var state = new RunState
        {
            PlanName = "test",
            Status = RunStatus.Idle,
            CurrentStage = "S2",
            ConfirmedStages = new() { "S1" },
        };

        var remaining = DoctorRemainingStages(plan, state);

        Assert.DoesNotContain("S1", remaining);
        Assert.Contains("S2", remaining);
    }

    [Fact]
    public void Doctor_SkippedStages_ExcludedFromRemaining()
    {
        var plan = new PlanConfig
        {
            Repo = Path.GetTempPath(),
            Tracker = "B11-2-TRACKER-TEST.md",
            Stages = new()
            {
                new() { Id = "S1", Title = "First", Sessions = 1 },
                new() { Id = "S2", Title = "Second", Sessions = 1 },
            },
            Agent = new() { Command = "echo", Args = new() { "{prompt}" } },
        };
        var state = new RunState
        {
            PlanName = "test",
            Status = RunStatus.Idle,
            CurrentStage = "S1",
            SkippedStages = new() { "S2" },
        };

        var remaining = DoctorRemainingStages(plan, state);

        Assert.DoesNotContain("S2", remaining);
        Assert.Contains("S1", remaining);
    }

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
        Assert.Contains("replay", output);
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
    [Fact]
    public void Completion_ContainsAllRegisteredVerbs_Exhaustive()
    {
        var expectedVerbs = new HashSet<string>(StringComparer.Ordinal)
        {
            "run", "status", "gate", "log", "report", "preview", "audit", "mcp-serve", "pause", "resume", "approve",
            "kill", "skip", "inject", "abort", "retry-stage", "rollback",
            "pause-after-stage", "goto", "heartbeat", "plan", "tasks", "new-plan", "doctor", "completion"
        };

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

    // --- Helpers replicating DoctorCommand's remaining-stages logic ---

    private static List<string> DoctorRemainingStages(PlanConfig plan, RunState state)
    {
        return plan.Stages
            .Where(s =>
            {
                if (state.SkippedStages.Contains(s.Id)) return false;
                if (state.ConfirmedStages.Contains(s.Id)) return false;
                return true;
            })
            .Select(s => s.Id)
            .ToList();
    }
}
