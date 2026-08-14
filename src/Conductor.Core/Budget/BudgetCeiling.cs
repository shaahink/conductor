using System.Globalization;
using System.Runtime.InteropServices;

namespace Conductor.Core.Budget;

/// <summary>KS5.4 — an approval's request, parsed. Both halves null means "no amount was given", which
/// is a request in its own right and not an error: see <see cref="BudgetCeiling.ParseRaise"/>.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct BudgetRaiseRequest(decimal? Usd, long? Tokens)
{
    public bool IsEmpty => Usd is null && Tokens is null;
}

/// <summary>KS5.4 — which halves of the ceiling a spend has reached. Produced only by
/// <see cref="BudgetCeiling.Standing"/>, so the spend-vs-cap comparison itself has exactly one home:
/// the cap check parks on this, the reload un-parks on it, and the approval refuses on it (computed
/// against the would-be ceiling), which is what makes the three agree about what "over" means.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct BudgetStanding(bool OverCost, bool OverTokens)
{
    public bool AnyOver => OverCost || OverTokens;
}

/// <summary>
/// KS5.4 — the ceiling a run is actually governed by, and the arithmetic that moves it.
/// <para>Pure and static for the same reason <see cref="BudgetAnalyzer"/> is: the run loop, the cap
/// check, <c>/state</c>, the report line and the tests all have to agree about what "the cap" means,
/// and the only way three surfaces cannot drift is that there is one function. The plan's
/// <c>limits.maxRunCostUsd</c> is the ceiling an operator SET; a run's ceiling is that plus everything
/// an owner has since approved on top of it. Storing the grant rather than an absolute ceiling is what
/// makes the two compose: a <c>plan reload</c> that raises the configured cap raises the effective one
/// with it, and an approval granted before that reload is not silently thrown away.</para>
/// <para>No number here comes from a rate, a model or a table — every figure is either the operator's
/// own configured cap or an amount they typed. That is the stage's house rule and this file is where
/// it would be easiest to break.</para>
/// </summary>
public static class BudgetCeiling
{
    /// <summary>The cost ceiling in force: the plan's cap plus what has been approved on top of it.
    /// No configured cap means no ceiling at all — a grant cannot invent one, because there would be
    /// nothing for it to be a grant OF.</summary>
    public static decimal? EffectiveCostCap(decimal? planCap, decimal grantUsd)
        => planCap is { } cap ? cap + grantUsd : null;

    /// <summary>The token ceiling in force. Same rule as <see cref="EffectiveCostCap"/>.</summary>
    public static long? EffectiveTokenCap(long? planCap, long grantTokens)
        => planCap is { } cap ? cap + grantTokens : null;

    /// <summary>
    /// KS5.4 — the ONE spend-vs-cap comparison. Inclusive (a run that has spent exactly its ceiling is
    /// over it), and a half with no configured cap can never be over. Every decision that hangs on
    /// "has this run reached its ceiling" — the park, the reload's un-park, the approval's refusal —
    /// reads this rather than re-deriving the comparison, because a second copy of a
    /// <c>&gt;=</c> is exactly the kind of thing that agrees today and diverges on the next edit. The
    /// round-2 verdict found that divergence live: the approval checked only the halves being raised,
    /// so a run over BOTH ceilings un-parked on a dollar amount, bought a full session, and re-parked
    /// on the token half nobody had been told about.
    /// </summary>
    public static BudgetStanding Standing(decimal? costCap, decimal spentUsd, long? tokenCap, long spentTokens)
        => new(
            OverCost: costCap is { } cc && spentUsd >= cc,
            OverTokens: tokenCap is { } tc && spentTokens >= tc);

    /// <summary>The park announcement's subject: every half the spend has reached, named with its
    /// numbers — never just the first one found. The round-2 verdict caught the single-half line in the
    /// act: a run over both ceilings printed only the money clause, so the operator typed a dollar
    /// amount, and the token half re-parked the run a full session later.</summary>
    public static string Overage(decimal? costCap, decimal spentUsd, long? tokenCap, long spentTokens)
    {
        var standing = Standing(costCap, spentUsd, tokenCap, spentTokens);
        var clauses = new List<string>(2);
        if (standing.OverCost) clauses.Add($"budget cap: {Usd(spentUsd)} >= {Usd(costCap!.Value)}");
        if (standing.OverTokens) clauses.Add($"token cap: {Tokens(spentTokens)} >= {Tokens(tokenCap!.Value)}");
        return string.Join("; ", clauses);
    }

    /// <summary>
    /// Parse the amount carried by an <c>approve</c>. The accepted forms, and nothing else:
    /// <list type="bullet">
    /// <item>empty / null — no amount. The caller decides what the default raise is and says so.</item>
    /// <item><c>5</c> or <c>$5.00</c> — dollars, the money half only.</item>
    /// <item><c>usd=5</c>, <c>tokens=500000</c>, or both separated by <c>;</c> or <c>,</c>.</item>
    /// </list>
    /// Anything else is refused with a reason rather than rounded into a guess: the one thing this verb
    /// must never do is pick a number nobody typed.
    /// </summary>
    public static (bool Ok, BudgetRaiseRequest Request, string? Error) ParseRaise(string? value)
    {
        var v = value?.Trim() ?? "";
        if (v.Length == 0) return (true, default, null);

        decimal? usd = null;
        long? tokens = null;
        foreach (var raw in v.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? "" : part[..eq].Trim().ToLowerInvariant();
            var num = (eq < 0 ? part : part[(eq + 1)..]).Trim().TrimStart('$');

            switch (key)
            {
                case "":
                case "usd":
                case "cost":
                case "amount":
                    if (!decimal.TryParse(num, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) || d <= 0)
                        return (false, default, $"'{part}' is not a positive dollar amount");
                    usd = (usd ?? 0m) + d;
                    break;
                case "tokens":
                    if (!long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) || t <= 0)
                        return (false, default, $"'{part}' is not a positive token count");
                    tokens = (tokens ?? 0L) + t;
                    break;
                default:
                    return (false, default, $"'{key}' is not something approve can raise (use usd= or tokens=)");
            }
        }
        return (true, new BudgetRaiseRequest(usd, tokens), null);
    }

    /// <summary>Dollars, in the one format every surface of this stage prints them in.</summary>
    public static string Usd(decimal amount) => FormattableString.Invariant($"${amount:0.00}");

    /// <summary>Tokens, in the one format every surface of this stage prints them in.</summary>
    public static string Tokens(long amount) => FormattableString.Invariant($"{amount / 1000.0:0.#}k");
}
