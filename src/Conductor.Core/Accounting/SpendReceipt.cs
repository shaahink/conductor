namespace Conductor.Core.Accounting;

/// <summary>
/// KS5.2 — what a provider said ONE model invocation cost, on its way to a <c>costs</c> row.
/// <para>A receipt exists only when the wire reported a figure: <see cref="BilledSpend"/> is the only
/// thing in the engine that makes one, and it returns null when the provider said nothing. That is the
/// house rule expressed as a type — a spend surface either quotes what it was billed or says it does
/// not know, and there is no third state in which a guess is wearing a billed row's clothes. The
/// advisor's <c>0.0005 × seconds</c> was exactly that third state for eleven months.</para>
/// </summary>
/// <param name="Category">One of <see cref="SpendCategory"/>'s constants — the lane that spent it.</param>
/// <param name="CostUsd">The provider's own figure. Zero is a legitimate answer (a cached turn); it is
/// the ABSENCE of a figure that must never be recorded as zero.</param>
/// <param name="TokensIn">Uncached input, cache writes included — the column's meaning in
/// <c>ArchivedCost</c>.</param>
/// <param name="TokensOut">Output tokens.</param>
/// <param name="TokensThink">Reasoning tokens, where the backend reports them at all.</param>
/// <param name="TokensCacheRead">Cache reads.</param>
/// <param name="WallMs">Wall time of the invocation, measured by the caller.</param>
public sealed record SpendReceipt(
    string Category, decimal CostUsd,
    long TokensIn, long TokensOut, long TokensThink, long TokensCacheRead, long WallMs)
{
    /// <summary>Every token on the receipt — the same sum <c>ArchivedCost.Tokens</c> reports.</summary>
    public long Tokens => TokensIn + TokensOut + TokensThink + TokensCacheRead;

    /// <summary>The same billed figure, filed under a different lane. Used where one helper spawns for
    /// several callers (the advisor CLI answers a verdict consult and a plan import through one code
    /// path) so the money lands under the caller that asked for it.</summary>
    public SpendReceipt As(string category) => this with { Category = category };
}
