namespace Conductor.Core.Release;

/// <summary>The CHANGELOG as the rename act needs to see it. <paramref name="BodyIsPlaceholder"/> is
/// the guard that makes this act safe to automate at all: renaming a heading over a body that says
/// "Nothing yet" ships that sentence to the world as the release notes, which is bug #88's exact
/// shape and would be a silent, permanent mistake.</summary>
public sealed record ChangelogRenameFacts(
    bool FileExists,
    bool HasUnreleased,
    bool BodyIsPlaceholder,
    int BodyLines,
    bool AlreadyHasVersionSection,
    string Date);

/// <summary>What the tag act is decided from. The tag is derivable from the version — but only once
/// the CHANGELOG section it will publish exists, because <c>release.yml</c> runs
/// <c>changelog-section.sh</c> as the first job of a tag build and uses its stdout as the release
/// body.</summary>
public sealed record TagFacts(
    bool Exists,
    bool ChangelogSectionOk,
    string? TargetRef,
    string? TargetSha);

/// <summary>One file the era-close moves, and everything that decides whether it may be moved.
/// <paramref name="ReferencedByPlan"/> is the one that matters most: trap 13 — a move without the
/// repoint in the SAME act means the next session reads nothing.</summary>
public sealed record DocMove(
    string From,
    string To,
    bool SourceExists,
    bool DestinationOccupied,
    bool ReferencedByPlan)
{
    /// <summary>The plan already points at the destination, so this move has happened. MEASURED on
    /// the CH4.2 rig: asked a second time, the probe derives <c>From</c> from the plan — which the
    /// first run repointed — so <c>From</c> and <c>To</c> are the same path, the file is "there" and
    /// the destination is "occupied" by itself. Without this, a completed move reports as a collision
    /// and the sentence reads "X exists and X is not it".</summary>
    public bool AlreadyInPlace =>
        string.Equals(From, To, StringComparison.OrdinalIgnoreCase);
}
