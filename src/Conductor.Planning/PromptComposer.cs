namespace Conductor.Planning;

/// <summary>Pure composition of a task's prompt building blocks (P3): facts in, an ordered labeled
/// block list out. Deterministic — editing one input changes exactly its block and nothing else,
/// which is what makes the card-detail view honest and testable. Empty read-only blocks are
/// omitted; the editable task-scoped blocks are always present so an editor can fill them.</summary>
public static class PromptComposer
{
    public static PromptComposition Compose(PromptCompositionInputs inputs)
    {
        var blocks = new List<PromptBlock>();

        AddIfPresent(blocks, PromptBlockKind.Persona,
            inputs.PersonaName.Length > 0 ? $"Persona — {inputs.PersonaName}" : "Persona",
            inputs.PersonaSystemPrompt);
        AddIfPresent(blocks, PromptBlockKind.StageNotes,
            inputs.StageId.Length > 0 ? $"Stage notes — {inputs.StageId}" : "Stage notes",
            inputs.StageNotes);

        blocks.Add(new PromptBlock(PromptBlockKind.TaskTitle, "Task title", inputs.TaskTitle, Editable: true));
        blocks.Add(new PromptBlock(PromptBlockKind.TaskContext, "Extra context (task-scoped)", inputs.TaskContext, Editable: true));

        AddIfPresent(blocks, PromptBlockKind.Knowledge, "Injected knowledge", inputs.InjectedKnowledge);
        AddIfPresent(blocks, PromptBlockKind.Tools, "Tool contract", inputs.ToolContract);

        return new PromptComposition(inputs.TaskId, blocks);
    }

    private static void AddIfPresent(List<PromptBlock> blocks, PromptBlockKind kind, string label, string content)
    {
        if (!string.IsNullOrWhiteSpace(content))
            blocks.Add(new PromptBlock(kind, label, content.Trim(), Editable: false));
    }
}
