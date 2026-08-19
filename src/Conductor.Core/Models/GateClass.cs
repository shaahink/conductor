namespace Conductor.Models;

/// <summary>
/// KS4.2 — the gate <b>class</b>. Where <see cref="GateVisibility"/> says who may see a gate, this
/// says what KIND of assertion the gate makes, and the one kind that is not "run this and read the
/// exit code" is <see cref="Regression"/>: SWE-bench's PASS_TO_PASS semantics, <i>nothing that
/// worked before is broken now</i>.
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

    /// <summary>The accepted spellings, for the plan-load refusal message.</summary>
    public static readonly string[] Known = [Standard, Regression];

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
}
