namespace Conductor.Models;

public enum SessionOutcome
{
    Advanced,
    Progress,
    NoProgress,
    GatesRed,
    Stalled,
    TimedOut,
    AgentError,
    LimitBackoff,
    KilledByUser,
    Interrupted,
    RolledOver,
}

public enum AuditFindingSeverity
{
    None,
    Low,
    Medium,
    High,
}
