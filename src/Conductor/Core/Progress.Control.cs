namespace Conductor.Core;

public enum ControlAction
{
    PauseAfterSession,
    ResumeRun,
    AbortNow,
    SkipStage,
    KillSession,
    StopAfterSession,
    RetryStage,
    Rollback,
    PauseAfterStage,
    Goto,
    Heartbeat,
    ReloadPlan,
}
