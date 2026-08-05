using Conductor.Models;

namespace Conductor.Core;

/// <summary>P3 adapter: gathers the facts a task's prompt composition is made of — persona, stage
/// notes, the task's own data, injected knowledge, the tool contract — and hands them to the pure
/// <see cref="Conductor.Planning.PromptComposer"/>. The stage is derived from the task's checkpoint
/// id via the plan's progress conventions (the same mapping the tracker uses), so a card always
/// lands on the stage whose session would deliver it.</summary>
public static class TaskPromptComposition
{
    public static PromptComposition Compose(PlanConfig plan, TaskItem task, string injectedKnowledge,
        PersonaRegistry? personas = null)
    {
        var stageId = plan.Conventions.DeriveStageId(task.CheckpointId);
        var stage = plan.Stages.FirstOrDefault(s => string.Equals(s.Id, stageId, StringComparison.OrdinalIgnoreCase));
        var personaName = stage != null ? plan.ResolvePersona(stage) : null;
        var registry = personas ?? new PersonaRegistry(plan);

        return PromptComposer.Compose(new PromptCompositionInputs
        {
            PersonaName = personaName ?? "",
            PersonaSystemPrompt = personaName != null ? registry.ResolveSystemPrompt(personaName) ?? "" : "",
            StageId = stage?.Id ?? stageId,
            StageNotes = stage?.Notes ?? "",
            TaskId = task.TaskId,
            TaskTitle = task.Title,
            TaskContext = task.Context,
            InjectedKnowledge = injectedKnowledge,
            ToolContract = ToolContract.Render(plan),
        });
    }
}
