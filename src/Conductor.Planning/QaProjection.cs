namespace Conductor.Planning;

/// <summary>What the QA dial (P2) resolves to: the workflow it demands, whether verification is
/// skipped, and the effective verifier threshold. All-null (<see cref="Classic"/>) means the dial is
/// absent and the classic machinery — stage workflow + overrides + limits — decides, byte-for-byte
/// today's behavior.</summary>
public sealed class QaProjection
{
    public static readonly QaProjection Classic = new();

    /// <summary>The workflow the dial projects onto ("deliver-verify" / "big-dev-then-big-audit").
    /// null = dial absent — the stage/plan workflow decides.</summary>
    public string? WorkflowName { get; init; }

    /// <summary>Whether verification steps are skipped. true only for mode=off. false whenever the
    /// dial is set to a verifying mode — the dial owns QA frequency, so a stale
    /// overrides.skipVerification must not silently disable the QA the dial promises. null = dial
    /// absent, stage overrides decide.</summary>
    public bool? SkipVerification { get; init; }

    /// <summary>Effective verifier threshold (0–100). null = the plan's limits.verifierThreshold.</summary>
    public int? VerifierThreshold { get; init; }

    /// <summary>phaseGate: the audit's diff base covers every session since the stage started
    /// (true, the classic behavior) or only the latest delivery session (false).</summary>
    public bool AuditCoversPriorSessions { get; init; } = true;
}
