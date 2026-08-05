namespace Conductor.Core.History;

/// <summary>One declared stage of an archived run.</summary>
public sealed record ArchivedStage(
    string Id, string Title, string Status, int Sessions, string? StartedUtc, string? ConfirmedUtc);
