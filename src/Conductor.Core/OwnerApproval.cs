using Conductor.Models;

namespace Conductor.Core;

/// <summary>What an owner approval should do, derived purely from why the run parked. Kept as a
/// pure function so the (untestable-in-isolation) orchestrator branch is locked by a unit test:
/// an approval-mode/budget park must NOT confirm the stage (that would advance past unfinished work).</summary>
public enum ApprovalOutcome
{
    /// <summary>Owner-gate on a green stage — approve confirms the stage and advances.</summary>
    ConfirmStage,
    /// <summary>Approval-mode park before a session — approve runs exactly the next session.</summary>
    ResumeSession,
    /// <summary>Budget/token cap tripped — approve resets the budget window and continues.</summary>
    ResetBudgetAndResume,
}

public static class OwnerApproval
{
    /// <summary>Legacy/unknown (null) reasons are treated as an owner-gate: the historical behaviour
    /// and the safe default (require an explicit confirm rather than silently running work).</summary>
    public static ApprovalOutcome Decide(AwaitingOwnerReason? reason) => reason switch
    {
        AwaitingOwnerReason.ApprovalMode => ApprovalOutcome.ResumeSession,
        AwaitingOwnerReason.Budget => ApprovalOutcome.ResetBudgetAndResume,
        _ => ApprovalOutcome.ConfirmStage,
    };
}
