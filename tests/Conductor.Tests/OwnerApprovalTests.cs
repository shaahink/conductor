using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class OwnerApprovalTests
{
    [Fact]
    public void OwnerGateApprovalConfirmsStage()
    {
        // Owner-gate on a green stage: approving must confirm-and-advance.
        Assert.Equal(ApprovalOutcome.ConfirmStage, OwnerApproval.Decide(AwaitingOwnerReason.OwnerGate));
    }

    [Fact]
    public void ApprovalModeApprovalRunsSessionAndDoesNotConfirm()
    {
        // Regression: an approval-mode park is BEFORE a session — approving must run the session,
        // never confirm the stage (that would advance past unfinished checkpoints).
        var outcome = OwnerApproval.Decide(AwaitingOwnerReason.ApprovalMode);
        Assert.Equal(ApprovalOutcome.ResumeSession, outcome);
        Assert.NotEqual(ApprovalOutcome.ConfirmStage, outcome);
    }

    [Fact]
    public void BudgetApprovalResetsWindowAndDoesNotConfirm()
    {
        // Regression: a budget/token-cap park is AFTER a session with work still owed — approving must
        // reset the budget window and continue, never confirm the stage.
        var outcome = OwnerApproval.Decide(AwaitingOwnerReason.Budget);
        Assert.Equal(ApprovalOutcome.ResetBudgetAndResume, outcome);
        Assert.NotEqual(ApprovalOutcome.ConfirmStage, outcome);
    }

    [Fact]
    public void LegacyNullReasonTreatedAsOwnerGate()
    {
        // A state.json written before B3-audit (no reason) must default to the safe confirm path,
        // matching the historical single-behaviour rather than silently running work.
        Assert.Equal(ApprovalOutcome.ConfirmStage, OwnerApproval.Decide(null));
    }
}
