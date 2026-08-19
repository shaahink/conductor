using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// KS4.3 — what the mutation class found on one gate: the score the ENGINE computed from the report
/// the gate wrote, over the files this branch changed, and the bar it had to clear.
/// </summary>
/// <remarks>A finding exists on every mutation gate that ran, green or red, because the score is
/// worth recording when it passes too — it is the number the era-boundary run is made of. What makes
/// it red is <see cref="IsShortfall"/>, and a <see cref="Note"/> is the fail-closed case: the class
/// could not be evaluated at all, which is red rather than absent.</remarks>
public sealed record MutationFinding(
    double? Score, double Threshold, int Counted, int Survived, int NoCoverage,
    IReadOnlyList<string> Survivors, string? Note)
{
    /// <summary>Red. Note that <c>Score is null</c> alone is NOT a shortfall — a branch that changed
    /// no mutable source has nothing to score, and that case carries no finding at all.</summary>
    public bool IsShortfall => Note is not null || (Score is { } s && s < Threshold);
}

public sealed record GateResult(string Name, bool Passed, bool Skipped, bool Optional, int ExitCode, TimeSpan Duration, string Tail)
{
    public bool Cached { get; init; }
    /// <summary>KS4.1: this result came from a <see cref="GateVisibility.Holdout"/> gate, and is
    /// therefore already anonymous — <see cref="Name"/> is <see cref="GateVisibility.RedactedName"/>,
    /// <see cref="Tail"/> carries a fixed notice rather than the command's output, and
    /// <see cref="ExitCode"/> is normalised. Nothing downstream has the gate's identity to leak.</summary>
    public bool Holdout { get; init; }
    /// <summary>SC4.1: this result is the SECOND run of the gate — the first one failed.</summary>
    public bool Retried { get; init; }
    /// <summary>KS4.2: the checks this <see cref="GateClass.Regression"/> gate reported PASSING on
    /// this run. Empty for every other gate — and empty from a gate that PASSED is the fail-closed
    /// case, not a quiet nothing (see <see cref="GateClass.EmptyPassSetNotice"/>).</summary>
    public IReadOnlyList<string> PassSet { get; init; } = [];
    /// <summary>KS4.2: baseline check names that are no longer in <see cref="PassSet"/> — things
    /// that worked and do not now. Non-empty makes this result NOT green whatever the exit code was.</summary>
    public IReadOnlyList<string> Regressions { get; init; } = [];
    /// <summary>KS4.2: set when the class could not be evaluated at all (a passing regression gate
    /// that reported no checks). Red for the same reason and reported in the same place.</summary>
    public string? RegressionNote { get; init; }
    /// <summary>KS4.2: the one predicate the rest of the engine asks. Note that a gate can PASS and
    /// have this true — that is the whole point of the class.</summary>
    public bool HasRegressions => Regressions.Count > 0 || RegressionNote is not null;
    /// <summary>KS4.3: what the mutation class measured on this gate, or null on every other gate —
    /// and on a mutation gate whose branch changed no mutable source, which is not a failure.</summary>
    public MutationFinding? Mutation { get; init; }
    /// <summary>KS4.3: the mutation score is below its threshold, or could not be read at all.</summary>
    public bool HasMutationShortfall => Mutation is { IsShortfall: true };
    /// <summary>KS4.2/KS4.3: this gate is red for a reason its EXIT CODE does not carry. Every place
    /// that filters a battery for "what went wrong" has to ask this and not <c>!Passed</c>, or the
    /// one failure the classes exist to surface is the one the reader is not shown.</summary>
    public bool HasClassFailure => HasRegressions || HasMutationShortfall;
    /// <summary>SC4.1: wall time the discarded first attempt burned. Counted in the cost estimate,
    /// kept OUT of <see cref="Duration"/> so a duration-vs-last-pass comparison stays like-for-like.</summary>
    public TimeSpan FirstAttemptDuration { get; init; }
    // KS4.2/KS4.3: the class failures are checked FIRST because they are the case the other glyphs
    // get wrong. A regressing or under-mutation-score gate exited 0, so every branch below would
    // spell it OK. They keep separate words: a reader sent to look for a deleted test when what
    // happened is an unkilled mutant has been sent to the wrong file.
    public string Glyph => HasRegressions ? (Optional ? GateClass.Glyph + "-warn" : GateClass.Glyph)
        : HasMutationShortfall ? (Optional ? GateClass.MutationGlyph + "-warn" : GateClass.MutationGlyph)
        : Cached ? "cached" : Skipped ? "-"
        : Passed ? (Retried ? "OK-retry" : "OK")
        : Optional ? "warn" : (Retried ? "FAIL-retry" : "FAIL");
    /// <summary>Estimated overhead cost = Duration × rate (O3). Skipped or cached gates contribute zero.
    /// A retried gate is charged for both attempts — the battery really spent that time.</summary>
    public decimal EstimatedCostUsd(decimal ratePerSecond) =>
        (Skipped || Cached) ? 0m : (decimal)(Duration + FirstAttemptDuration).TotalSeconds * ratePerSecond;

    /// <summary>KS4.2 changed this line and nothing else had to change with it: a regression makes a
    /// result not-green, so the phase gate, the lane merge battery, the session verdict and every
    /// other consumer of <see cref="GateRunner.AllRequiredPassed"/> treat it as red without knowing
    /// the class exists. An <see cref="Optional"/> gate keeps its contract — it reports and never
    /// blocks — which is also the only way to declare a regression gate you are still calibrating.</summary>
    public bool IsGreen => Skipped || Cached || Optional || (Passed && !HasClassFailure);
}
