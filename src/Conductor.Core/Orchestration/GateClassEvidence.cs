namespace Conductor.Core.Orchestration;

/// <summary>
/// KS4.2 — one regression-class gate's finding: checks that passed earlier in this run and do not
/// pass now. A row exists only when something was actually lost, so a non-empty
/// <see cref="SessionEvidence.Regressions"/> IS the regression.
/// </summary>
/// <remarks>This is a measurement, not a judgement, so unlike <see cref="AdvisoryEvidence"/> it is
/// read by <see cref="SessionVerdict.Decide"/> — and it is read separately from
/// <see cref="SessionEvidence.GatesGreen"/> even though a regression already turns that false. The
/// separate row is what lets the verdict SAY "regression" instead of "a gate failed", which for this
/// class is the difference between a fix session that looks for a failing assertion and one that
/// looks for the check that went missing.</remarks>
public sealed record RegressionEvidence(string Gate, IReadOnlyList<string> BrokenChecks, string? Note);

/// <summary>
/// KS4.3 — one mutation-class gate's finding: the share of deliberately broken implementations the
/// suite noticed, over the files this branch changed, against the bar the plan set.
/// </summary>
/// <remarks>A row exists only when the bar was MISSED (or the score could not be read at all), for
/// the same reason <see cref="RegressionEvidence"/> only exists on a real loss — so a non-empty
/// <see cref="SessionEvidence.MutationShortfalls"/> IS the finding, and the verdict can say
/// "the tests do not test" instead of "a gate failed". Like the regression row this is a
/// measurement rather than a judgement, which is why it is read by
/// <see cref="SessionVerdict.Decide"/> and <see cref="AdvisoryEvidence"/> is not.</remarks>
public sealed record MutationEvidence(
    string Gate, double? Score, double Threshold, int Counted, IReadOnlyList<string> Survivors, string? Note);
