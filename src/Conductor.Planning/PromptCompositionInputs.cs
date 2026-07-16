namespace Conductor.Planning;

/// <summary>The facts a task-prompt composition is a pure function of (P3) — plain strings the
/// caller gathers (the engine, a standalone tool, a test); empty means "absent". No engine types,
/// no IO: same input, same composition, always.</summary>
public sealed class PromptCompositionInputs
{
    public string PersonaName { get; set; } = "";
    public string PersonaSystemPrompt { get; set; } = "";
    public string StageId { get; set; } = "";
    public string StageNotes { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string TaskTitle { get; set; } = "";

    /// <summary>The owner-editable per-task extra context, stored as task data.</summary>
    public string TaskContext { get; set; } = "";

    /// <summary>Injected knowledge that compounds — ledger, open bugs, lessons, queued instructions.</summary>
    public string InjectedKnowledge { get; set; } = "";

    public string ToolContract { get; set; } = "";
}
