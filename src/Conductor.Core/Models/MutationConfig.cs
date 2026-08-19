namespace Conductor.Models;

/// <summary>
/// KS4.3 — how a <see cref="GateClass.Mutation"/> gate's run is read for a mutation score, and what
/// score is good enough. Stryker.NET first, because it is the mutation runner for .NET and this
/// engine's own suite is .NET; the format is named rather than assumed so a second one can be added
/// without changing the class.
/// </summary>
/// <remarks>
/// <para><b>What this class is for.</b> Every other gate in this engine asks "do the tests pass".
/// When the same agent writes the code and the tests, that question is answerable by writing tests
/// that cannot fail — an assertion on a constant, a mock that returns the value under test, a test
/// that exercises no branch. A mutation score asks the one question that move cannot survive: break
/// the implementation on purpose, and does anything go red. It is deterministic, it is not a
/// judgement, and unlike a coverage percentage it cannot be raised by executing a line without
/// asserting on it.</para>
/// <para><b>Why the engine computes the score and not the gate.</b> A gate that decides its own
/// threshold is a gate whose threshold lives in the repo the agent edits, next to a runner with a
/// dozen flags that each narrow what gets mutated. So the gate's job here is only to PRODUCE a
/// report; the arithmetic, the diff scoping and the comparison against
/// <see cref="Threshold"/> all happen in the engine, from the report as written.</para>
/// <para><b>Why diff-scoped.</b> A whole-repository mutation score is both unaffordable — every
/// mutant is a test run — and useless as a per-checkpoint signal: a hundred thousand already-killed
/// mutants elsewhere drown the twenty a session just introduced, so a checkpoint that adds
/// assertion-free tests moves the number by nothing. Scoring only the files the branch changed makes
/// the measurement small enough to run and sharp enough to fail.</para>
/// </remarks>
public sealed class MutationConfig
{
    /// <summary>One of <see cref="StrykerJson"/>.</summary>
    public string Format { get; set; } = StrykerJson;

    /// <summary>The report file, relative to the gate's working directory. A <c>*</c> is allowed and
    /// resolves to the NEWEST match, because Stryker writes into a timestamped run directory
    /// (<c>StrykerOutput/&lt;timestamp&gt;/reports/mutation-report.json</c>) unless the plan pins an
    /// output path.</summary>
    public string? Path { get; set; }

    /// <summary>The score, in percent, the changed files must reach. Required: a mutation gate with
    /// no threshold is a report nobody reads.</summary>
    public double Threshold { get; set; }

    /// <summary>The git revision the changed-file set is computed against — the gate scores the
    /// files that differ between it and the working tree. Defaults to <see cref="DefaultDiffBase"/>,
    /// which is "what is uncommitted right now"; a plan whose sessions commit their work should name
    /// the integration branch instead.</summary>
    public string? DiffBase { get; set; }

    /// <summary>Set to score every file in the report rather than only the changed ones. Off by
    /// default and expected to stay off for per-session batteries — see the diff-scoping paragraph
    /// in the remarks. Its use is the era-boundary run, where the whole point is the wide number.
    /// </summary>
    public bool WholeReport { get; set; }

    /// <summary>Stryker.NET's <c>mutation-report.json</c> (mutation-testing-elements schema).</summary>
    public const string StrykerJson = "stryker-json";

    /// <summary>The accepted spellings, for the plan-load refusal message.</summary>
    public static readonly string[] Formats = [StrykerJson];

    /// <summary>Uncommitted work: the diff between HEAD and the working tree. The right default for
    /// a gate that runs inside a session before its work is committed, and the wrong one for a run
    /// whose sessions commit as they go — which is why a plan can override it.</summary>
    public const string DefaultDiffBase = "HEAD";

    /// <summary>Only these extensions are scored, and only these count as "the branch changed
    /// mutable source". A branch that touched nothing but markdown has no mutation score to clear,
    /// and saying that out loud is not the same as passing.</summary>
    public static readonly string[] MutableExtensions = [".cs"];

    public static bool IsKnownFormat(string? value)
        => value is not null && Formats.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    public bool Is(string format) => Format.Trim().Equals(format, StringComparison.OrdinalIgnoreCase);

    public string BaseRev => string.IsNullOrWhiteSpace(DiffBase) ? DefaultDiffBase : DiffBase.Trim();

    public static bool IsMutableSource(string path)
        => MutableExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
