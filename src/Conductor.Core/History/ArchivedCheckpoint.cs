namespace Conductor.Core.History;

/// <summary>One checkpoint, folded from the archived run's event log.</summary>
public sealed record ArchivedCheckpoint(
    string Id, string StageId, string Title, string Status, string? Commit, string? Evidence, bool Confirmed);
