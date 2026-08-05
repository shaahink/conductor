using System.Globalization;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>K5.4 — money with headroom. Every push rendered cost as <c>$0.4242</c>: four decimal
/// places, no cap, no remaining, no share of the budget spent. Four decimals are precision the owner
/// cannot use and the one number they can — how much of the run's budget is left — was not there at
/// all, though the plan has carried <c>limits.maxRunCostUsd</c> the whole time and the run parks on
/// it. A run at $97 of a $100 cap and a run at $97 of no cap rendered identically.</summary>
public static class MoneyLine
{
    /// <summary>The cost line for a session-end push: what this session cost, what the run has cost,
    /// and — when the plan sets a cap — what is left before the run parks for owner approval.</summary>
    public static string ForSession(decimal? sessionCost, decimal runCost, decimal? cap) =>
        "cost: " + Usd(sessionCost ?? 0m) + " · run " + Headroom(runCost, cap);

    /// <summary>The same figures without a session — for a run-complete or run-start push.</summary>
    public static string ForRun(decimal runCost, decimal? cap) => "cost: " + Headroom(runCost, cap);

    /// <summary>Two decimals above a dollar, four below it: <c>$97.46</c> is what the owner reads on
    /// a statement, and <c>$0.0042</c> is a real session that would otherwise render as <c>$0.00</c>.</summary>
    public static string Usd(decimal amount) =>
        amount == 0m || amount >= 1m || amount <= -1m
            ? amount.ToString("$0.00", CultureInfo.InvariantCulture)
            : amount.ToString("$0.0000", CultureInfo.InvariantCulture);

    private static string Headroom(decimal runCost, decimal? cap)
    {
        if (cap is not { } limit || limit <= 0m) return Usd(runCost) + " (no cap set)";
        var left = limit - runCost;
        var pct = (int)Math.Round(runCost / limit * 100m, MidpointRounding.AwayFromZero);
        return left > 0m
            ? $"{Usd(runCost)} of {Usd(limit)} ({pct.ToString(CultureInfo.InvariantCulture)}%, {Usd(left)} left)"
            : $"{Usd(runCost)} of {Usd(limit)} — cap reached, the run parks for approval";
    }
}
