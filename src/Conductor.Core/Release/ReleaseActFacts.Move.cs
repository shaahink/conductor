namespace Conductor.Core.Release;

/// <summary>The doc move as a whole. <paramref name="PlanFileWritable"/> and
/// <paramref name="PlanPath"/> are here because the move and the repoint are ONE act: a
/// <c>git mv</c> that lands while the plan still points at the old path leaves the run reading
/// nothing, and there is no half of this worth performing alone.</summary>
public sealed record DocMoveFacts(
    IReadOnlyList<DocMove> Moves,
    string? PlanPath,
    bool PlanFileWritable,
    bool WorkingTreeDirty);

/// <summary>The acts that are never this engine's, and the state that decides what to say about
/// each. Every field here is a measurement; none of them changes whether the act is the owner's.</summary>
public sealed record OwnerFacts(
    string? Tag,
    string? BaseBranch,
    string? Repo,
    IReadOnlyList<string> RunsOwedARecord,
    bool AnyConductorLive);
