namespace Conductor.Core.Money;

/// <summary>
/// K4.3 — one row of the money report: a scope of spend with the columns the research doc's headline
/// table used (<c>docs/dev/NEXT-ERA-FINDINGS-2026-08-04.md</c> "Headline"), every one of which was a
/// hand-written SQL query until now.
/// <para>The derived figures are properties rather than stored fields so a row cannot be internally
/// inconsistent: there is exactly one place that divides cost by checkpoints, and every surface —
/// the verb, the JSON, the report — reads it.</para>
/// </summary>
/// <param name="Label">What this row is: a run, a stage, a month, a cap window, a cost category.</param>
/// <param name="Sessions">Sessions that fall in the row, costed or not.</param>
/// <param name="Tokens">Every token the row spent: input (cache writes included) + output + reasoning + cache reads.</param>
/// <param name="CacheReadTokens">The cache-read share of those tokens, in absolute terms.</param>
/// <param name="InputTokens">Uncached input plus cache writes, as the provider reported it.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="Cost">Billed dollars for the row, as the provider reported them — never a price table.</param>
/// <param name="Checkpoints">Checkpoints closed by the row's sessions. Zero where the question does
/// not apply (a cost category does not close checkpoints), which prints as "-" rather than as 0.</param>
public sealed record MoneyLine(
    string Label, int Sessions, long Tokens, long CacheReadTokens,
    long InputTokens, long OutputTokens, decimal Cost, int Checkpoints)
{
    /// <summary>The headline number of this whole era: what fraction of every token was a cache read.</summary>
    public double CacheReadShare => Tokens > 0 ? (double)CacheReadTokens / Tokens : 0;

    /// <summary>Tokens per delivered checkpoint — the productivity figure that survives a cap change.</summary>
    public double? TokensPerCheckpoint => Checkpoints > 0 ? (double)Tokens / Checkpoints : null;

    /// <summary>Dollars per delivered checkpoint. The number the owner keeps asking for.</summary>
    public decimal? CostPerCheckpoint => Checkpoints > 0 ? Cost / Checkpoints : null;

    /// <summary>The blended rate actually paid, in dollars per million tokens. Blended because the
    /// engine has no price table by design (<c>LiveCostEstimator</c>): this is billed dollars over
    /// billed tokens, so a cache-heavy row reads cheap per token, which is the point.</summary>
    public decimal? CostPerMillionTokens => Tokens > 0 ? Cost / ((decimal)Tokens / 1_000_000m) : null;

    /// <summary>Sums two rows. Used to roll runs up into a project total without a second code path.</summary>
    public MoneyLine Plus(MoneyLine other, string label)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new MoneyLine(label, Sessions + other.Sessions, Tokens + other.Tokens,
            CacheReadTokens + other.CacheReadTokens, InputTokens + other.InputTokens,
            OutputTokens + other.OutputTokens, Cost + other.Cost, Checkpoints + other.Checkpoints);
    }

    /// <summary>An empty row, the identity for <see cref="Plus"/>.</summary>
    public static MoneyLine Empty(string label) => new(label, 0, 0, 0, 0, 0, 0m, 0);
}

/// <summary>
/// K4.3 — one run's money, in the four cuts the owner asked for: lifetime, per cap window ("what did
/// the cap buy me"), per stage, per month, and per spending lane.
/// </summary>
/// <param name="RunId">The run.</param>
/// <param name="PlanName">Its plan.</param>
/// <param name="RepoLabel">The repo it ran in, as the catalogue labels it.</param>
/// <param name="StartedUtc">When it started, as the row spells it.</param>
/// <param name="LastActivityUtc">Its last recorded activity.</param>
/// <param name="Total">The run's lifetime row.</param>
/// <param name="Windows">One row per ceiling window, from <c>BudgetAnalyzer</c>'s split — the same
/// axis <c>conductor budget</c> reports on, so the two verbs cannot disagree about where the cap changed.</param>
/// <param name="Stages">One row per stage, in the order the stages were worked.</param>
/// <param name="Months">One row per calendar month of session start, oldest first.</param>
/// <param name="Categories">One row per cost category: agent, gate, advisor.</param>
public sealed record MoneyRun(
    string RunId, string PlanName, string RepoLabel, string? StartedUtc, string? LastActivityUtc,
    MoneyLine Total, IReadOnlyList<MoneyLine> Windows, IReadOnlyList<MoneyLine> Stages,
    IReadOnlyList<MoneyLine> Months, IReadOnlyList<MoneyLine> Categories)
{
    /// <summary>What the last ceiling change bought, in tokens per delivered checkpoint. Greater than
    /// one means the later window delivers a checkpoint for fewer tokens.</summary>
    public double? CapTokenPayoff => Windows.Count >= 2
        && Windows[^2].TokensPerCheckpoint is { } before && Windows[^1].TokensPerCheckpoint is { } after and > 0
            ? before / after
            : null;

    /// <summary>The same comparison in dollars per checkpoint — which is the one an owner pays.</summary>
    public decimal? CapCostPayoff => Windows.Count >= 2
        && Windows[^2].CostPerCheckpoint is { } before && Windows[^1].CostPerCheckpoint is { } after and > 0
            ? before / after
            : null;
}

/// <summary>
/// K4.3 — the whole answer to "what did this cost": every run in scope, plus the project-level
/// rollups that only mean something across runs (lifetime spend, month-to-date, where the money goes).
/// </summary>
/// <param name="Scope">What was measured, in the words the operator used.</param>
/// <param name="Runs">One entry per run, newest run last.</param>
/// <param name="Total">Every run summed — the project's lifetime.</param>
/// <param name="Months">Calendar months across all runs in scope, oldest first.</param>
/// <param name="Categories">Spending lanes across all runs in scope, biggest first.</param>
public sealed record MoneyReport(
    string Scope, IReadOnlyList<MoneyRun> Runs, MoneyLine Total,
    IReadOnlyList<MoneyLine> Months, IReadOnlyList<MoneyLine> Categories);
