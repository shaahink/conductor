namespace Conductor.Core.Planning;

/// <summary>The deterministic-parse output shapes (<see cref="MarkdownPlanParser"/>): a plan is a list
/// of stages, each with a title, optional subtitle-as-notes, and its checkpoints.</summary>
public sealed record ParsedPlan(IReadOnlyList<ParsedStage> Stages);

public sealed record ParsedStage(string Id, string Title, string? Notes, IReadOnlyList<ParsedCheckpoint> Checkpoints);

public sealed record ParsedCheckpoint(string Id, string Title, string? Status);
