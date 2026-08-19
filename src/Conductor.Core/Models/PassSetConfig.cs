namespace Conductor.Models;

/// <summary>
/// KS4.2 — how a <see cref="GateClass.Regression"/> gate's run is read for the set of checks that
/// PASSED. Three formats, chosen because between them they cover every suite this engine drives
/// today and the escape hatch covers the rest.
/// </summary>
/// <remarks>
/// <para>The set has to come from the gate's own run rather than from a second, engine-issued
/// command: the plan already says how this repo's tests are invoked, and a parallel invocation would
/// be a second measurement that can disagree with the first. So the plan tells the gate to emit the
/// names (a trx logger, <c>go test -v</c>, or anything that prints one name per line) and this says
/// where to look.</para>
/// </remarks>
public sealed class PassSetConfig
{
    /// <summary>One of <see cref="Trx"/>, <see cref="GoTest"/>, <see cref="Lines"/>.</summary>
    public string Format { get; set; } = "";

    /// <summary>For <see cref="Trx"/>: the results file, relative to the gate's working directory.
    /// A <c>*</c> is allowed and resolves to the NEWEST match, because <c>dotnet test</c> names its
    /// own trx after the machine and the clock unless the plan pins <c>LogFileName</c>.</summary>
    public string? Path { get; set; }

    /// <summary>VSTest's trx: every <c>UnitTestResult</c> with <c>outcome="Passed"</c>. The .NET
    /// answer, and the only one of the three that survives a suite whose console output is
    /// summarised rather than enumerated.</summary>
    public const string Trx = "trx";

    /// <summary><c>go test -v</c>: every <c>--- PASS: Name</c> line, subtests included.</summary>
    public const string GoTest = "go-test";

    /// <summary>Every non-blank line of the gate's stdout is one check name. The escape hatch: any
    /// runner at all can be piped through something that prints the names it passed.</summary>
    public const string Lines = "lines";

    /// <summary>The accepted spellings, for the plan-load refusal message.</summary>
    public static readonly string[] Formats = [Trx, GoTest, Lines];

    public static bool IsKnownFormat(string? value)
        => value is not null && Formats.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    public bool Is(string format) => Format.Trim().Equals(format, StringComparison.OrdinalIgnoreCase);
}
