namespace Conductor.Core;

/// <summary>Where a judge's opinion stands against what the deterministic signals measured.</summary>
public enum JudgeAgreement
{
    /// <summary>The judge and the gates say the same thing about this session.</summary>
    Agrees,

    /// <summary>They do not — the row this checkpoint exists to record. A judge that approves a red
    /// session, or condemns a green one, is INFORMATION about one of the two; it is never a verdict.</summary>
    Disagrees,

    /// <summary>The judge hedged (or could not be read), so there is nothing to disagree with.</summary>
    Inconclusive,
}

/// <summary>
/// KS4.5 — one second-model review of a delivered session. Structured, so it can be a row in the
/// taxonomy rather than a paragraph in a log.
/// </summary>
/// <param name="Verdict">The judge's own word, as it wrote it — kept verbatim for the record.</param>
/// <param name="Score">0-100 when the judge gave one. Optional by design: a review that says "this
/// looks wrong and here is why" is worth more than one that says 62.</param>
/// <param name="Findings">What it would tell the next session.</param>
/// <param name="Summary">One sentence, when offered.</param>
public sealed record JudgeReview(string Verdict, int? Score, IReadOnlyList<string> Findings, string? Summary)
{
    /// <summary>Words that mean "ship it". A judge writes prose, so the vocabulary is matched rather
    /// than demanded — but only these, because an unrecognised word must land in
    /// <see cref="JudgeAgreement.Inconclusive"/> and not be guessed into an opinion.</summary>
    public static IReadOnlyList<string> PassWords { get; } = ["pass", "approve", "approved", "ok", "green", "accept"];

    /// <summary>Words that mean "do not ship it". Same rule as <see cref="PassWords"/>.</summary>
    public static IReadOnlyList<string> FailWords { get; } = ["fail", "failed", "reject", "rejected", "red", "block"];

    public bool IsPass => PassWords.Contains(Verdict.Trim(), StringComparer.OrdinalIgnoreCase);

    public bool IsFail => FailWords.Contains(Verdict.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>How this review stands against the deterministic outcome. Pure, and deliberately
    /// one-way: it reads the measurement and reports on the JUDGE, never the other way round.</summary>
    public JudgeAgreement Against(bool deterministicGreen)
        => IsPass ? (deterministicGreen ? JudgeAgreement.Agrees : JudgeAgreement.Disagrees)
         : IsFail ? (deterministicGreen ? JudgeAgreement.Disagrees : JudgeAgreement.Agrees)
         : JudgeAgreement.Inconclusive;
}
