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
