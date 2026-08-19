namespace Conductor.Models;

/// <summary>
/// KS4.2/KS4.3 — the gate <b>class</b>. Where <see cref="GateVisibility"/> says who may see a gate,
/// this says what KIND of assertion the gate makes. Two of the three are not "run this and read the
/// exit code": <see cref="Regression"/> is SWE-bench's PASS_TO_PASS semantics, <i>nothing that
/// worked before is broken now</i>, and <see cref="Mutation"/> is <i>the tests you just wrote can
/// actually tell a broken implementation from a working one</i>.
/// </summary>
/// <remarks>
/// <para><b>Why an exit code cannot say it.</b> A test command answers one question — is anything
/// failing right now — and it answers "no" just as happily when the failing test has been deleted,
/// renamed away, skipped, filtered out of the run, or quietly excluded from the project file. Every
/// one of those is a green battery over broken work, and every one of them is a move an agent
/// optimising against a readable measurement can make without ever writing a line of a lie. The
/// deleted test is not a hypothetical here: this repo's own session rules name it first among the
/// forbidden moves, which is an admission that the rule had nothing mechanical behind it.</para>
/// <para><b>What the class actually compares.</b> A regression gate declares how to read the set of
/// checks that PASSED out of its own run (see <see cref="PassSetConfig"/>). The engine keeps the
/// last such set per run and per gate as the <i>baseline</i>, and a name in the baseline that is not
/// in the current set is a regression — whatever the exit code says. So the signal is a set
/// difference, not a count: a session that deletes one test and adds two still regresses, and it
/// says which one it lost.</para>
/// <para><b>The baseline only ever grows.</b> It advances when a regression gate passes CLEAN, and
/// it is deliberately left where it was when the gate regresses. Overwriting it with the smaller set
/// would launder the regression in exactly one session: red once, and green forever after, with the
/// deleted check gone from the record that would have remembered it. There is deliberately no verb
/// that resets a baseline, because every verb this engine has is one the coding agent can call.</para>
/// <para><b>Fail closed on an unreadable pass set.</b> A regression gate that exits 0 and yields no
/// pass set at all is RED, not green: "the trx file is not where the plan says" and "the suite ran
/// and everything passed" are indistinguishable from the exit code, and one of them is the cheapest
/// way there is to switch this class off from inside the repo.</para>
/// </remarks>
public static class GateClass
{
    /// <summary>The default: the gate's exit code is the whole of its verdict.</summary>
    public const string Standard = "standard";

    /// <summary>PASS_TO_PASS. The gate's exit code AND the set of checks it reports passing, against
    /// the last set it reported. See the remarks on <see cref="GateClass"/>.</summary>
    public const string Regression = "regression";

    /// <summary>KS4.3. The gate's exit code AND the mutation score the engine computes from the
    /// gate's report, over the files THIS BRANCH CHANGED. See <see cref="MutationConfig"/>.</summary>
    public const string Mutation = "mutation";

    /// <summary>The accepted spellings, for the plan-load refusal message.</summary>
    public static readonly string[] Known = [Standard, Regression, Mutation];

    public static bool IsKnown(string? value)
        => value is null || Known.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>How a regression is spelled everywhere a battery is summarised. Deliberately not
    /// "FAIL": a fix session that reads FAIL goes looking for a failing assertion and finds none,
    /// because the gate exited 0. The word has to carry the whole shape of what happened.</summary>
    public const string Glyph = "REGRESSION";

    /// <summary>What the engine says when a regression gate passes but reports no checks at all.
    /// See the fail-closed paragraph in the remarks on <see cref="GateClass"/>.</summary>
    public const string EmptyPassSetNotice =
        "exited 0 but reported no passing checks at all — a regression gate that cannot be read is " +
        "treated as red, because 'the pass set moved' and 'everything passed' are the same exit code";

    /// <summary>KS4.3, and deliberately not "FAIL" for the same reason <see cref="Glyph"/> is not: a
    /// mutation gate that is red EXITED 0. A reader who sees FAIL looks for a failing assertion and
    /// finds a green suite, which is precisely the state this class exists to call out.</summary>
    public const string MutationGlyph = "MUTANTS";

    /// <summary>KS4.3 fail-closed: the branch changed mutable source and the gate's report covers
    /// none of it. Indistinguishable from a perfect score by exit code alone, and the cheapest way
    /// there is to switch this class off from inside the repo — point the report path at a stale
    /// file, or narrow the mutate glob until the changed file falls outside it.</summary>
    public const string UnreadableMutationNotice =
        "exited 0, but its mutation report scores none of the source files this branch changed — a " +
        "mutation gate that cannot be read over the diff is treated as red, because 'the report is " +
        "stale or mis-scoped' and 'every mutant died' are the same exit code";
}
