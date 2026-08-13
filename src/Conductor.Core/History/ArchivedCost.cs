namespace Conductor.Core.History;

/// <summary>
/// K4.3 — one row of the <c>costs</c> table, as recorded. A row is a category's spend for one session:
/// the agent stream, the gate battery, or any of the other model processes a run pays for. Kept per-row
/// rather than pre-summed because the question "where does the money go" is exactly the split that
/// summing destroys.
/// </summary>
/// <param name="SessionNumber">The session the spend was recorded against. 0 where the spend happened
/// before the run's first session (the auth probe) — a key, not a lie about which session it was.</param>
/// <param name="Category">The lane that spent it — see <c>Conductor.Core.Accounting.SpendCategory</c>
/// for the vocabulary. It was agent / gate / advisor until KS5.2 taught the other six model-spawning
/// paths (lanes, fix-lanes, the parallel audit, the supervisor, the auth probe) to write rows too.
/// Every category carries a figure the PROVIDER reported except <c>gate</c>, which is gate wall-clock
/// priced by the plan's own <c>limits.overheadCostPerSecond</c> and is the table's one estimate.</param>
/// <param name="TokensIn">Uncached input, INCLUDING cache writes: the provider reports cache creation
/// inside the input count and <c>ClaudeProvider</c> passes it through, so this column is
/// "everything the prompt cost that was not a cache hit".</param>
/// <param name="TokensOut">Output tokens.</param>
/// <param name="TokensThink">Reasoning tokens. Zero on every row this project has ever written —
/// Claude bundles reasoning into output — and carried only so the arithmetic stays total.</param>
/// <param name="TokensCacheRead">Cache reads. The two-thirds of the bill, and the reason this record
/// exists at all.</param>
/// <param name="CostUsd">What the provider billed for this row.</param>
/// <param name="WallMs">Wall time, where the recorder knew it.</param>
public sealed record ArchivedCost(
    int SessionNumber, string Category,
    long TokensIn, long TokensOut, long TokensThink, long TokensCacheRead,
    decimal CostUsd, long WallMs)
{
    /// <summary>Every token on the row, the same sum <c>RunArchive</c> reports as a session's tokens.</summary>
    public long Tokens => TokensIn + TokensOut + TokensThink + TokensCacheRead;
}
