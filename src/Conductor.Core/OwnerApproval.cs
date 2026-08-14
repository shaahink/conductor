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
    /// <summary>Budget/token cap tripped — approve RAISES the ceiling by a stated amount and continues.
    /// <para>KS5.4 renamed this from <c>ResetBudgetAndResume</c>, because "reset" is what it used to do
    /// and what made a $3.00 cap permit $7.00 with nobody able to name the number in force. The
    /// counters are not touched now: the run keeps one monotone spend and gets a bigger ceiling to
    /// measure it against. Not to be confused with <c>AdvisorAction.ResetBudget</c>, which resets
    /// attempts, not money.</para></summary>
    RaiseCeilingAndResume,
}

public static class OwnerApproval
{
    /// <summary>Legacy/unknown (null) reasons are treated as an owner-gate: the historical behaviour
    /// and the safe default (require an explicit confirm rather than silently running work).</summary>
    public static ApprovalOutcome Decide(AwaitingOwnerReason? reason) => reason switch
    {
        AwaitingOwnerReason.ApprovalMode => ApprovalOutcome.ResumeSession,
        AwaitingOwnerReason.Budget => ApprovalOutcome.RaiseCeilingAndResume,
        _ => ApprovalOutcome.ConfirmStage,
    };
}
