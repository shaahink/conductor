namespace Conductor.Core.Planning;

/// <summary>The change shapes a <see cref="PlanDiff"/> reports: a stage or gate whose named fields
/// each moved from an old value to a new one. Rendered by the import command and the TUI review pane.</summary>
public sealed record StageChange(string Id, IReadOnlyList<FieldChange> Fields);

public sealed record GateChange(string Name, IReadOnlyList<FieldChange> Fields);

public sealed record FieldChange(string Field, string? Old, string? New);
