namespace Conductor.Models;

/// <summary>
/// KS5.4 — one owner approval past a budget park, recorded as what it CHANGED rather than as the
/// fact that it happened.
/// <para>Until this existed an approval left one number behind it: <c>BudgetApprovals++</c>. Everything
/// else it did was a deletion — <c>PerRunCostUsd</c>, <c>PerRunTokens</c> and
/// <c>PerRunOverheadCostUsd</c> were set to zero — so the run could not afterwards say what ceiling it
/// was being governed by, and neither could anyone reading it. The field log's 19:03 entry is that
/// hole: a $3.00 cap that had just permitted $7.00, with nobody able to name the number in force.</para>
/// <para>Each raise names the ceiling before and after, the spend standing at that instant (which no
/// approval forgives) and which approval it was. The last entry's <see cref="SpentUsd"/> is also the
/// baseline the "spend since your last approval" figure is measured from, so that reading survives an
/// engine restart without a second bookkeeping field.</para>
/// <para>A half the approval did not touch is null, not zero: an approval that raised only the money
/// ceiling must not read as one that set the token ceiling to nothing.</para>
/// </summary>
public sealed class BudgetRaise
{
    /// <summary>Which approval this was — the value of <c>RunState.BudgetApprovals</c> after it.</summary>
    public int Approval { get; set; }

    public DateTime WhenUtc { get; set; }

    /// <summary>The effective cost ceiling before and after, in dollars. Null when this approval did
    /// not move the money half (the run parked on tokens, or the plan sets no cost cap).</summary>
    public decimal? FromCostUsd { get; set; }
    public decimal? ToCostUsd { get; set; }

    /// <summary>The effective token ceiling before and after. Null when this approval did not move the
    /// token half.</summary>
    public long? FromTokens { get; set; }
    public long? ToTokens { get; set; }

    /// <summary>Billed spend and tokens standing at the instant of the raise. Recorded because the
    /// approval does not un-spend them — and because "what has it spent since I last approved" has no
    /// other anchor once the counters stop being zeroed.</summary>
    public decimal SpentUsd { get; set; }
    public long SpentTokens { get; set; }
}
