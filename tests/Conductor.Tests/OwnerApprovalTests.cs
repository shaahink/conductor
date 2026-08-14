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
    public void BudgetApprovalRaisesTheCeilingAndDoesNotConfirm()
    {
        // Regression: a budget/token-cap park is AFTER a session with work still owed — approving must
        // continue the run, never confirm the stage.
        // KS5.4: and what it does to continue is RAISE THE CEILING. The outcome used to be called
        // ResetBudgetAndResume and it meant it — PerRunCostUsd and PerRunTokens were zeroed — which is
        // how a $3.00 cap came to permit $7.00 with no surface naming a ceiling anywhere between. The
        // rename is the point of this arm: the name is what a reader of the switch sees first.
        var outcome = OwnerApproval.Decide(AwaitingOwnerReason.Budget);
        Assert.Equal(ApprovalOutcome.RaiseCeilingAndResume, outcome);
        Assert.NotEqual(ApprovalOutcome.ConfirmStage, outcome);
        Assert.NotEqual(ApprovalOutcome.ResumeSession, outcome);
    }

    [Fact]
    public void LegacyNullReasonTreatedAsOwnerGate()
    {
        // A state.json written before B3-audit (no reason) must default to the safe confirm path,
        // matching the historical single-behaviour rather than silently running work.
        Assert.Equal(ApprovalOutcome.ConfirmStage, OwnerApproval.Decide(null));
    }
}
