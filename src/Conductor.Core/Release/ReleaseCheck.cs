namespace Conductor.Core.Release;

/// <summary>
/// CH4.1 — one line of the release preflight. Deliberately the same shape as the launch drill's
/// <c>Leg</c> (<c>Name</c>, <c>State</c>, <c>Headline</c>, <c>Detail</c>), because it is the same
/// idea one door further along: a named check, a verdict, and the sentences an operator has to read
/// to fix it printed UNDER the verdict rather than crammed into it.
/// <para>Three states, not two, and the third is the point of CH4. <see cref="Ok"/> and
/// <see cref="Fail"/> are measurements. <see cref="Owner"/> is a decision this engine may not take —
/// the version number, single-versus-split, whether a run joins the published corpus. KS12.3's
/// failure was that six of seven acts went unperformed with nothing saying so; an owner line is how
/// an act gets NAMED and STOPPED AT instead of silently skipped.</para>
/// </summary>
public sealed record ReleaseCheck(string Name, string State, string Headline, IReadOnlyList<string> Detail)
{
    /// <summary>Measured, and it is what the release needs.</summary>
    public const string Ok = "ok";

    /// <summary>Measured, and it is not. Exit code 1.</summary>
    public const string Fail = "fail";

    /// <summary>Measured as far as it can be, and what remains is a judgement only the owner makes.
    /// Exit code 2 — non-zero, because a preflight that cannot certify must not read as certified,
    /// and distinct from 1 so a script can tell "broken" from "waiting on a person".</summary>
    public const string Owner = "owner";
}
