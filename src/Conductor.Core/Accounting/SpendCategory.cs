namespace Conductor.Core.Accounting;

/// <summary>
/// KS5.2 — the <c>costs.category</c> vocabulary, in one place.
/// <para>The column is a free string and <c>MoneyAnalyzer.Categories</c> groups by it, so two spellings
/// of the same lane are two rows in "where the money goes" and a run's history splits in half the day
/// somebody types <c>"lanes"</c>. One constant per spender, and the money verb renders whatever it
/// finds — no surface has to be taught a new category.</para>
/// <para>Every category here except <see cref="Gate"/> carries a figure the PROVIDER reported. Gate
/// rows are the one estimate the table has ever held: gate wall-clock priced by the plan's own
/// <c>limits.overheadCostPerSecond</c>, which an operator sets and can read back. Nothing in this
/// codebase prices tokens (see <c>LiveCostEstimator</c>).</para>
/// </summary>
public static class SpendCategory
{
    /// <summary>The delivery agent's own stream. Written by <c>RunLoop.EmitSessionFinished</c> from the
    /// session record, which is why <c>AgentSession</c> itself records nothing.</summary>
    public const string Agent = "agent";

    /// <summary>Gate wall-clock, priced by <c>limits.overheadCostPerSecond</c>. Estimated, not billed —
    /// a gate is a shell command, not a model, and no provider reports anything for it.</summary>
    public const string Gate = "gate";

    /// <summary>The second-brain consult at an ambiguous session end.</summary>
    public const string Advisor = "advisor";

    /// <summary>A read-only analysis lane (B12.1/B12.2).</summary>
    public const string Lane = "lane";

    /// <summary>A mutating fix-lane consuming a closed follow-up (B12.4).</summary>
    public const string FixLane = "fix-lane";

    /// <summary>The parallel audit lane (P2), which runs a full agent against a pinned worktree.</summary>
    public const string Audit = "audit";

    /// <summary>The <c>conductor watch</c> supervisor hook (SF5.1) — a model invocation in a DIFFERENT
    /// process from the engine, recorded best-effort against the run it was watching.</summary>
    public const string Supervisor = "supervisor";

    /// <summary>The one-token credential probe at run start (W3.2).</summary>
    public const string AuthProbe = "auth-probe";

    /// <summary>Every category the engine can write. Used by the tests that assert the vocabulary is
    /// distinct — a duplicate constant would silently merge two spenders into one row.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Agent, Gate, Advisor, Lane, FixLane, Audit, Supervisor, AuthProbe];

    /// <summary>True when this category's dollars came off a provider's wire. False for
    /// <see cref="Gate"/> alone, and a surface that mixes the two should say which is which.</summary>
    public static bool IsBilled(string category)
        => !string.Equals(category, Gate, StringComparison.OrdinalIgnoreCase);
}
