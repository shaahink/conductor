using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>Engine-side convenience over the pure <see cref="IQaPolicy"/> seam (P2): the library's
/// Project is agnostic (rules in, projection out), so the engine keeps its ergonomic
/// (plan, stage) shape here — thin adapters, no logic beyond the documented fallbacks.</summary>
public static class QaPolicyExtensions
{
    public static QaProjection Project(this IQaPolicy qa, PlanConfig plan, StageConfig stage)
        => qa.Project(plan.Pipeline?.Qa, stage.Qa);

    /// <summary>Whether verification is skipped for this stage. Precedence: the QA dial owns the
    /// answer when set (off → skip; everySession/phaseGate → verify, superseding a stale
    /// overrides.skipVerification); dial absent → the classic per-stage override decides. The
    /// operator's session-scoped skip flag is OR'd in by callers, never overridden here.</summary>
    public static bool EffectiveSkipVerification(this IQaPolicy qa, PlanConfig plan, StageConfig stage)
        => qa.Project(plan, stage).SkipVerification ?? stage.Overrides?.SkipVerification == true;

    /// <summary>The verifier pass bar for this stage: the QA dial's threshold when set, else the
    /// plan's limits.verifierThreshold.</summary>
    public static int EffectiveVerifierThreshold(this IQaPolicy qa, PlanConfig plan, StageConfig stage)
        => qa.Project(plan, stage).VerifierThreshold ?? plan.Limits.VerifierThreshold;
}
