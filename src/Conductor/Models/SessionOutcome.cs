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
    /// <summary>W3.2: the credential is dead (401 / expired OAuth / invalid key). Terminal for the
    /// run — no gate battery, no retry, no backoff; the run parks until a human re-authenticates.</summary>
    AuthFailed,
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
