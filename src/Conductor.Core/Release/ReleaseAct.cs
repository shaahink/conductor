namespace Conductor.Core.Release;

/// <summary>
/// CH4.2 — one act of the era-close, and what became of it.
///
/// <para><b>Why <see cref="Kind"/> and <see cref="State"/> are separate fields.</b> KS12.3's runbook
/// listed seven acts as one flat list, and six of them went unperformed. The reason is visible in
/// the shape of the document: an act the owner must do by hand and an act nobody got round to look
/// identical in prose. So the two questions are asked separately here — <see cref="Kind"/> is
/// "whose act is this, ever?" and never changes; <see cref="State"/> is "what happened this time".
/// An <see cref="Owner"/> act is <see cref="Stopped"/>, always, with the command spelled out. It is
/// never <see cref="Skipped"/>, because "skipped" is what six acts silently were.</para>
/// </summary>
public sealed record ReleaseAct(string Name, string Kind, string State, string Headline, IReadOnlyList<string> Detail)
{
    /// <summary>Derivable from facts the engine can measure, so the engine does it.</summary>
    public const string Mechanical = "mechanical";

    /// <summary>A judgement — the version number, single-versus-split, whether a run joins the
    /// published corpus, overwriting the binary, publishing to the world. Named, never performed.</summary>
    public const string Owner = "owner";

    /// <summary>Would be performed; this is a dry run.</summary>
    public const string Ready = "ready";

    /// <summary>Performed, just now.</summary>
    public const string Done = "done";

    /// <summary>Already in the target state — the act is idempotent and there was nothing to do.
    /// Distinct from <see cref="Stopped"/> and from <see cref="Refused"/> on purpose: "already done"
    /// and "not done" are the two answers KS12.3 could not tell apart.</summary>
    public const string Nothing = "nothing";

    /// <summary>A precondition is not met, so the act did NOT run and is named with the reason. An
    /// act may never run on an unmeasured precondition.</summary>
    public const string Refused = "refused";

    /// <summary>The owner's, and stopped at. Carries the exact command in <see cref="Detail"/>.</summary>
    public const string Stopped = "stopped";

    /// <summary>Attempted and it failed. The sequence stops here rather than performing the next act
    /// on top of a half-done one.</summary>
    public const string Failed = "failed";
}
