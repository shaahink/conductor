namespace Conductor.Core.Http;

/// <summary>W4.1: one work item an import declares. Part of <see cref="PlanDiffDto"/> — the preview
/// has to show the work, not just the stages, because the work is what was missing.</summary>
public sealed record PlanCheckpointDto(string Id, string StageId, string Title, string Status);
