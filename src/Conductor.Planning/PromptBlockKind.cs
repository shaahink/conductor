namespace Conductor.Planning;

/// <summary>The closed set of building blocks a task's prompt is composed from (P3). The order of
/// the enum members is the composition order <see cref="PromptComposer.Compose"/> emits.</summary>
public enum PromptBlockKind
{
    Persona,
    StageNotes,
    TaskTitle,
    TaskContext,
    Knowledge,
    Tools,
}
