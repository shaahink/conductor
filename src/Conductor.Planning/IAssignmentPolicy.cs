namespace Conductor.Planning;

/// <summary>The P1 seam: who works what. The engine hands over the declarative rules, the session
/// kind, the ready items, and the paths other live work has already claimed; the policy returns the
/// resolved agent overrides + the claimed item set. Pure and deterministic — no IO, no engine types,
/// no model calls (a scheduling decision is never delegated to an LLM).</summary>
public interface IAssignmentPolicy
{
    /// <summary>Decide the assignment for one upcoming session. <paramref name="claimedPaths"/> are
    /// repo-relative paths currently claimed elsewhere (e.g. running lanes) — an extra item whose
    /// declared claims overlap them must not be claimed.</summary>
    SessionAssignment Assign(PipelineRules? rules, SessionKind kind,
        IReadOnlyList<ReadyItem> readyItems, IReadOnlyCollection<string>? claimedPaths);
}
