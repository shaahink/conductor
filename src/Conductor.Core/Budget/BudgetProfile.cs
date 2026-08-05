using System.Globalization;

namespace Conductor.Core.Budget;

/// <summary>
/// K4.2 — one stretch of a run governed by one ceiling (or by none). A run that had its cap changed
/// mid-flight is two windows, and that split is the whole point: "what did the cap buy me" is a
/// comparison between windows, and it is unanswerable from a single lifetime average.
/// </summary>
/// <param name="Label">How the window states its ceiling, e.g. "no ceiling observed" or "8.0M / 0.76".</param>
/// <param name="FirstSession">First session number in the window.</param>
/// <param name="LastSession">Last session number in the window.</param>
/// <param name="Sessions">Session rows in the window, costed or not.</param>
/// <param name="Costed">Sessions that recorded any agent tokens — the denominator of every rate here.</param>
/// <param name="CapTokens">The ceiling in force, null when none was observed.</param>
/// <param name="CapMeasured">True when the ceiling came from a recorded event, false when inferred.</param>
/// <param name="NudgeTokens">Where the rail actually fired, measured; null when it never fired.</param>
/// <param name="Tokens">Agent tokens over the window.</param>
/// <param name="Checkpoints">Checkpoints closed by sessions of the window.</param>
/// <param name="Rollovers">Sessions the ceiling killed.</param>
/// <param name="Nudged">Sessions the rail nudged.</param>
/// <param name="NudgedAndClean">Sessions that were nudged and then ended on their own terms.</param>
/// <param name="RolloversNudgedFirst">Killed sessions that HAD been nudged and did not stop. The
/// difference between a rail that is missing and a rail that is being ignored.</param>
/// <param name="Closers">Sessions that closed at least one checkpoint.</param>
/// <param name="Floor">The smallest closing session — the repo's session floor over this window.</param>
/// <param name="ClosingMedian">Median closing session.</param>
/// <param name="ClosingMax">Largest closing session.</param>
/// <param name="WrapUp">Final tokens minus the measured nudge point, for clean enders: min/median/max.</param>
public sealed record BudgetWindow(
    string Label, int FirstSession, int LastSession, int Sessions, int Costed,
    long? CapTokens, bool CapMeasured, long? NudgeTokens,
    long Tokens, int Checkpoints, int Rollovers, int Nudged, int NudgedAndClean,
    int RolloversNudgedFirst, int Closers, long Floor, long ClosingMedian, long ClosingMax,
    (long Min, long Median, long Max, int Samples)? WrapUp)
{
    /// <summary>Tokens per delivered checkpoint — the only productivity number that survives a cap
    /// change, because a rollover's tokens and its successor's checkpoint fall in the same window.</summary>
    public double? TokensPerCheckpoint => Checkpoints > 0 ? (double)Tokens / Checkpoints : null;

    /// <summary>Rollovers over costed sessions.</summary>
    public double RolloverRate => Costed > 0 ? (double)Rollovers / Costed : 0;

    /// <summary>What is left after the nudge to land the work, commit and write the handoff.</summary>
    public long? Headroom => CapTokens is { } c && NudgeTokens is { } n && c > n ? c - n : null;

    /// <summary>The effective ratio, measured rather than declared: nudge point over ceiling.</summary>
    public double? NudgeRatio => CapTokens is { } c and > 0 && NudgeTokens is { } n ? (double)n / c : null;

    /// <summary>How often the cooperative rail actually ended a session, rather than the hard kill.</summary>
    public double? NudgeConversion => Nudged > 0 ? (double)NudgedAndClean / Nudged : null;
}

/// <summary>
/// K4.2 — the numbers <c>TOKEN-BUDGET-TUNING.md</c> §7 prescribes, computed rather than quoted.
/// </summary>
/// <param name="MaxSessionTokens">The prescribed ceiling.</param>
/// <param name="SoftBreakRatio">The prescribed ratio, chosen so the nudge clears the largest closer.</param>
/// <param name="NudgeTokens">Where that pair puts the nudge.</param>
/// <param name="Headroom">What that pair leaves for the wrap-up.</param>
/// <param name="WrapUpBasis">The wrap-up figure used, measured or assumed.</param>
/// <param name="WrapUpMeasured">False when no session was ever nudged and the §7 default stood in.</param>
/// <param name="Findings">One line per diagnosis, worst first.</param>
/// <param name="Verdict">The one sentence an operator acts on.</param>
public sealed record BudgetPrescription(
    long MaxSessionTokens, double SoftBreakRatio, long NudgeTokens, long Headroom,
    long WrapUpBasis, bool WrapUpMeasured, IReadOnlyList<string> Findings, string Verdict)
{
    /// <summary>The prescription as it would be pasted into a plan's <c>limits</c> block.</summary>
    public string AsJsonc =>
        "\"limits\": {\n" +
        $"  \"maxSessionTokens\": {MaxSessionTokens.ToString(CultureInfo.InvariantCulture)},\n" +
        $"  \"softBreakRatio\":   {SoftBreakRatio.ToString("0.##", CultureInfo.InvariantCulture)}\n" +
        "}";
}

/// <summary>K4.2 — one run's budget, as measured. <see cref="Current"/> is the window whose numbers
/// describe the configuration in force now; the prescription is computed from it.</summary>
public sealed record BudgetProfile(
    string RunId, string PlanName, IReadOnlyList<BudgetWindow> Windows, BudgetPrescription Prescription)
{
    /// <summary>The window the run is in — the last one. A single-window run is its own current.</summary>
    public BudgetWindow Current => Windows[^1];

    /// <summary>What the last cap change bought, as a ratio of tokens-per-checkpoint. Null unless the
    /// run has two windows that both delivered something.</summary>
    public double? CapPayoff => Windows.Count >= 2
        && Windows[^2].TokensPerCheckpoint is { } before && Current.TokensPerCheckpoint is { } after and > 0
            ? before / after
            : null;
}
