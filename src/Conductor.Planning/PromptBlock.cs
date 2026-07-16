namespace Conductor.Planning;

/// <summary>One labeled building block of a task's prompt (P3). <see cref="Editable"/> marks the
/// task-scoped blocks (title, extra context) an owner may edit as structured task data — never as
/// raw prompt splicing; everything else is a read-only projection of plan/run state.</summary>
public sealed record PromptBlock(PromptBlockKind Kind, string Label, string Content, bool Editable);
