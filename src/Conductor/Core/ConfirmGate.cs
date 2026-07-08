namespace Conductor.Core;

public static class ConfirmGate
{
    public static ControlAction? ProcessDestructive(ControlAction action, ref ControlAction? pending)
    {
        if (pending == action)
        {
            pending = null;
            return action;
        }
        pending = action;
        return null;
    }

    public static void Cancel(ref ControlAction? pending) => pending = null;

    public static string? Message(ControlAction? pending) => pending switch
    {
        ControlAction.AbortNow => "Press A again to confirm ABORT (any other key cancels)",
        ControlAction.SkipStage => "Press S again to confirm SKIP (any other key cancels)",
        ControlAction.KillSession => "Press K again to confirm KILL (any other key cancels)",
        _ => null,
    };
}
