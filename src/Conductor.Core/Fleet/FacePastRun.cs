namespace Conductor.Core.Fleet;

/// <summary>One finished run as the picker lists it. No base url and no token: there is nothing to
/// attach to and nothing to authorise. <paramref name="RunDb"/> is what <c>conductor history</c>
/// would open.</summary>
public sealed record FacePastRun(
    string Repo, string PlanName, string RunId, string Status,
    int Done, int Total, decimal CostUsd, string? LastActivityUtc, string RunDb)
{
    /// <summary>What to hand back to open this row — <c>face --archive</c>'s selector. The run id when
    /// the store opened; the catalogue SLUG when it did not, because a row whose database cannot be
    /// read has no run id to name it by and the slug is all such a row has.</summary>
    public string Selector { get; init; } = "";

    /// <summary>Empty when the store opened. Otherwise the one sentence saying why it did not — the
    /// same sentence <c>face --archive</c> refuses with (<c>ArchiveView.Describe</c>), so the list and
    /// the attach cannot end up telling two stories about one broken file.</summary>
    public string Problem { get; init; } = "";

    /// <summary>False for a catalogue row this engine could not open. Such a row is still LISTED — "that
    /// run is gone" and "that run was never here" are different answers, and a picker that hid the first
    /// would make the second the only one a reader could reach.</summary>
    public bool Readable => Problem.Length == 0;
}
