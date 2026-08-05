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
    /// <summary>P5: set/clear the session-scoped rollover override (<c>set-rollover</c> verb).
    /// Value = a token count ("200000"), "off"/"0" (rollover disabled this run), or ""/"clear"
    /// (back to the plan's limits.maxSessionTokens). Run-state only — never writes the plan file.</summary>
    SetRollover,
}
