using Conductor.Core;

namespace Conductor.Tests;

public class ConfirmGateTests
{
    [Fact]
    public void SingleDestructivePressReturnsNull()
    {
        ControlAction? pending = null;
        var result = ConfirmGate.ProcessDestructive(ControlAction.AbortNow, ref pending);
        Assert.Null(result);
        Assert.Equal(ControlAction.AbortNow, pending);
    }

    [Fact]
    public void DoublePressSameActionReturnsActionAndClearsPending()
    {
        ControlAction? pending = ControlAction.AbortNow;
        var result = ConfirmGate.ProcessDestructive(ControlAction.AbortNow, ref pending);
        Assert.Equal(ControlAction.AbortNow, result);
        Assert.Null(pending);
    }

    [Fact]
    public void DifferentActionReplacesPending()
    {
        ControlAction? pending = ControlAction.AbortNow;
        var result = ConfirmGate.ProcessDestructive(ControlAction.SkipStage, ref pending);
        Assert.Null(result);
        Assert.Equal(ControlAction.SkipStage, pending);
    }

    [Fact]
    public void CancelClearsPending()
    {
        ControlAction? pending = ControlAction.AbortNow;
        ConfirmGate.Cancel(ref pending);
        Assert.Null(pending);
    }

    [Fact]
    public void MessageReturnsPromptForDestructiveActions()
    {
        Assert.Equal("Press A again to confirm ABORT (any other key cancels)",
            ConfirmGate.Message(ControlAction.AbortNow));
        Assert.Equal("Press S again to confirm SKIP (any other key cancels)",
            ConfirmGate.Message(ControlAction.SkipStage));
        Assert.Equal("Press K again to confirm KILL (any other key cancels)",
            ConfirmGate.Message(ControlAction.KillSession));
    }

    [Fact]
    public void MessageReturnsNullForNonDestructiveActions()
    {
        Assert.Null(ConfirmGate.Message(ControlAction.PauseAfterSession));
        Assert.Null(ConfirmGate.Message(ControlAction.ResumeRun));
        Assert.Null(ConfirmGate.Message(null));
    }

    [Fact]
    public void FingerSlipDoesNotAct()
    {
        // B3.1 gate: "finger-slip without confirm does not skip"
        ControlAction? pending = null;
        var result = ConfirmGate.ProcessDestructive(ControlAction.SkipStage, ref pending);
        Assert.Null(result); // no action returned
    }

    [Fact]
    public void ConfirmedPathDoesAct()
    {
        // B3.1 gate: "confirmed path does"
        ControlAction? pending = ControlAction.SkipStage;
        var result = ConfirmGate.ProcessDestructive(ControlAction.SkipStage, ref pending);
        Assert.Equal(ControlAction.SkipStage, result);
    }
}
