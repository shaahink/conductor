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
