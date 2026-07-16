namespace Conductor.Planning;

/// <summary>The QA frequency dial (P2): a friendly projection onto the existing workflows —
/// "off" (no verification), "everySession" (deliver-verify), "phaseGate" (deliver repeatedly, one
/// consolidated audit + fix sweep). Resolving a dial value must produce exactly the same run as
/// picking the corresponding workflow by hand.</summary>
public sealed class QaRule
{
    /// <summary>off | everySession | phaseGate. Default everySession (the classic deliver-verify).</summary>
    public string Mode { get; set; } = "everySession";

    /// <summary>Verifier pass threshold (0–100). null = the plan's limits.verifierThreshold.</summary>
    public int? VerifierThreshold { get; set; }

    /// <summary>phaseGate only: the audit covers all sessions accumulated since the stage started
    /// (true, the default) rather than just the latest one.</summary>
    public bool AuditCoversPriorSessions { get; set; } = true;
}
