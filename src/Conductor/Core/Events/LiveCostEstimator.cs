using Conductor.Models;

namespace Conductor.Core.Events;

/// <summary>
/// SC2.3 — turns an in-flight session's REAL token counts into a dollar figure for the live ticker,
/// and names where that figure came from. Pure: events and history in, number and label out.
/// </summary>
/// <remarks>
/// The rule this class exists to enforce is that the engine never puts a number on a surface without
/// being able to say how it knows it. There are exactly three ways a live session cost can be known,
/// and <see cref="Estimate.Basis"/> is always one of them:
/// <list type="bullet">
/// <item><see cref="BasisStreamed"/> — the provider itself put money on the wire per step (opencode's
/// <c>step_finish.cost</c>). Not an estimate at all.</item>
/// <item><see cref="BasisRunRate"/> — the provider streamed tokens but no money (claude), so the
/// tokens are priced at the dollars-per-token THIS RUN has already been billed, measured from its own
/// finished sessions.</item>
/// <item><see cref="BasisNoRate"/> — tokens are real, the cost is not knowable yet: no money on the
/// wire and no finished priced session to learn a rate from. The cost reads 0.00 and says so.</item>
/// </list>
/// <para>There is deliberately NO built-in price table. A hard-coded dollars-per-million-tokens list
/// is a doc comment about the outside world: it is right the day it is written and silently wrong the
/// day prices move or a model ships that is not in it — and a confidently wrong spend figure is worse
/// than an honest blank. Rates are learned from what this run was actually charged, or not claimed.</para>
/// </remarks>
public static class LiveCostEstimator
{
    /// <summary>No session in flight, or nothing has streamed yet.</summary>
    public const string BasisNone = "none";
    /// <summary>The session ended; this is the CLI's own recorded total, not an estimate.</summary>
    public const string BasisMeasured = "measured";
    /// <summary>The provider reported cost on the wire as the session ran.</summary>
    public const string BasisStreamed = "streamed";
    /// <summary>Live tokens priced at this run's own observed dollars-per-token.</summary>
    public const string BasisRunRate = "estimated-from-run-rate";
    /// <summary>Live tokens are real; no rate exists yet to price them with.</summary>
    public const string BasisNoRate = "no-rate-yet";

    /// <summary>A live-spend figure and the one word that says how it is known.</summary>
    public sealed record Estimate(decimal CostUsd, string Basis);

    /// <summary>Price an in-flight session's folded token totals. <paramref name="history"/> is the
    /// run's session history — only its FINISHED, priced sessions are used, and the in-flight session
    /// (which has no cost yet) contributes nothing to the rate it is priced with.</summary>
    public static Estimate ForLiveSession(LiveMetrics.SessionTokenTotals live, IEnumerable<SessionRecord> history)
    {
        ArgumentNullException.ThrowIfNull(live);

        // Streamed money beats every estimate: it is what the provider says it charged.
        if (live.CostUsd > 0) return new Estimate(live.CostUsd, BasisStreamed);

        var billable = live.Input + live.Output + live.Reasoning + live.CacheRead;
        if (billable <= 0) return new Estimate(0m, BasisNone);
        if (ObservedRatePerToken(history) is not { } rate) return new Estimate(0m, BasisNoRate);
        return new Estimate(decimal.Round(billable * rate, 4, MidpointRounding.AwayFromZero), BasisRunRate);
    }

    /// <summary>Dollars per token this run has actually been billed, blended across every finished
    /// session that recorded BOTH a cost and a token count — null when no such session exists.</summary>
    /// <remarks>Blended over all four token buckets rather than modelled per bucket: the mix of fresh
    /// input, cache reads and output is near-identical between sessions of one plan, and a blend of
    /// what this run was really charged needs no price list to stay true.</remarks>
    internal static decimal? ObservedRatePerToken(IEnumerable<SessionRecord> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        decimal cost = 0;
        long tokens = 0;
        foreach (var h in history)
        {
            if (h.EndedUtc is null || h.CostUsd is not { } c || c <= 0) continue;
            var t = h.TokensTotal;
            if (t <= 0) continue;
            cost += c;
            tokens += t;
        }
        return tokens > 0 && cost > 0 ? cost / tokens : null;
    }
}
