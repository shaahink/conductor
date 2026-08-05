using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>Engine-side convenience over the pure <see cref="IQaPolicy"/> seam (P2): the library's
/// Project is agnostic (rules in, projection out), so the engine keeps its ergonomic
/// (plan, stage) shape here — thin adapters, no logic beyond the documented fallbacks.</summary>
public static class QaPolicyExtensions
{
    public static QaProjection Project(this IQaPolicy qa, PlanConfig plan, StageConfig stage)
        => qa.Project(plan.Pipeline?.Qa, stage.Qa);

    /// <summary>W4.4: the projection for the item a session actually claimed. <paramref name="itemQa"/>
    /// null/empty/"inherit" is identical to the stage-level projection — an item only participates
    /// when someone set its dial.</summary>
    public static QaProjection Project(this IQaPolicy qa, PlanConfig plan, StageConfig stage, string? itemQa)
        => qa.Project(plan.Pipeline?.Qa, stage.Qa, itemQa);

    /// <summary>Whether verification is skipped for this stage. Precedence, most specific first: the
    /// QA dial owns the answer when set (off → skip; everySession/phaseGate → verify, superseding a
    /// stale overrides.skipVerification); then the classic per-stage override; then, lowest,
    /// <see cref="PlanConfig.VerifyEachDelivery"/>. The operator's session-scoped skip flag is OR'd
    /// in by callers, never overridden here.
    /// <para>SF0.1 / bug 11: <c>verifyEachDelivery</c> had exactly one reader —
    /// <c>VerdictEngine.ShouldVerify</c> — and nothing had called it since M3.1 handed the next-step
    /// decision to the workflow. A plan setting it <c>false</c> ran a Verify after every delivery
    /// anyway, silently, and <c>plans/conductor-maestro.plan.json</c> has meant it since M3. Folded
    /// in HERE, at the bottom of the chain, because this is the expression the live decision already
    /// goes through: the default <c>true</c> reproduces classic behaviour byte for byte, so no plan
    /// that did not set the key changes.</para></summary>
    public static bool EffectiveSkipVerification(this IQaPolicy qa, PlanConfig plan, StageConfig stage, string? itemQa = null)
        => qa.Project(plan, stage, itemQa).SkipVerification
           ?? (stage.Overrides?.SkipVerification == true || !plan.VerifyEachDelivery);

    /// <summary>The verifier pass bar for this stage: the QA dial's threshold when set, else the
    /// plan's limits.verifierThreshold.</summary>
    public static int EffectiveVerifierThreshold(this IQaPolicy qa, PlanConfig plan, StageConfig stage, string? itemQa = null)
        => qa.Project(plan, stage, itemQa).VerifierThreshold ?? plan.Limits.VerifierThreshold;
}
