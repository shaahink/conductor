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
    /// <summary>SC5.1: the session could not proceed until a known future instant and said so
    /// (`conductor task --blocked-until`). Not a failure — no attempt is burned and no fix session is
    /// queued; the run loop sleeps until the timestamp and spawns one more session.</summary>
    BlockedUntil,
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
