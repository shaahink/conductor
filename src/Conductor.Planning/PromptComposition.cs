namespace Conductor.Planning;

/// <summary>An ordered, labeled decomposition of one task's prompt into its building blocks (P3) —
/// what a Kanban card detail shows instead of the compiled wall of text.</summary>
public sealed record PromptComposition(string TaskId, IReadOnlyList<PromptBlock> Blocks)
{
    /// <summary>The block of a given kind, or null when the composition omitted it (empty
    /// non-editable content).</summary>
    public PromptBlock? Block(PromptBlockKind kind) => Blocks.FirstOrDefault(b => b.Kind == kind);
}
