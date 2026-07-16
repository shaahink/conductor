using Conductor.Planning;

namespace Conductor.Tests;

/// <summary>P3: the pure prompt composition — facts in, ordered labeled blocks out. The load-bearing
/// gate is <see cref="EditingTaskContext_ChangesExactlyThatBlock"/>: an owner edit to the task's
/// extra context must change that one block and nothing else in the recomposed prompt.</summary>
public sealed class PromptComposerTests
{
    private static PromptCompositionInputs FullInputs() => new()
    {
        PersonaName = "architect",
        PersonaSystemPrompt = "You design before you build.",
        StageId = "P3",
        StageNotes = "Reuse PromptBuilder; do not fork.",
        TaskId = "P3-a1",
        TaskTitle = "Wire the card detail",
        TaskContext = "Focus on tab_kanban.go",
        InjectedKnowledge = "## Ledger\n- goldens live in testdata/",
        ToolContract = "conductor note / task --done",
    };

    [Fact]
    public void Compose_EmitsBlocksInCompositionOrder_WithTaskBlocksEditable()
    {
        var composition = PromptComposer.Compose(FullInputs());

        Assert.Equal("P3-a1", composition.TaskId);
        Assert.Equal(
            [PromptBlockKind.Persona, PromptBlockKind.StageNotes, PromptBlockKind.TaskTitle,
             PromptBlockKind.TaskContext, PromptBlockKind.Knowledge, PromptBlockKind.Tools],
            composition.Blocks.Select(b => b.Kind));
        Assert.Equal([false, false, true, true, false, false], composition.Blocks.Select(b => b.Editable));
        Assert.Equal("Persona — architect", composition.Block(PromptBlockKind.Persona)!.Label);
        Assert.Equal("Stage notes — P3", composition.Block(PromptBlockKind.StageNotes)!.Label);
    }

    [Fact]
    public void EditingTaskContext_ChangesExactlyThatBlock()
    {
        var before = PromptComposer.Compose(FullInputs());
        var edited = FullInputs();
        edited.TaskContext = "Actually: start from the demo source.";
        var after = PromptComposer.Compose(edited);

        Assert.Equal(before.Blocks.Count, after.Blocks.Count);
        for (var i = 0; i < before.Blocks.Count; i++)
        {
            if (after.Blocks[i].Kind == PromptBlockKind.TaskContext)
                Assert.Equal("Actually: start from the demo source.", after.Blocks[i].Content);
            else
                Assert.Equal(before.Blocks[i], after.Blocks[i]); // every other block is byte-identical
        }
    }

    [Fact]
    public void Compose_IsDeterministic()
    {
        var first = PromptComposer.Compose(FullInputs());
        var second = PromptComposer.Compose(FullInputs());
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.Equal(first.Blocks, second.Blocks); // element-wise; PromptBlock is a value record
    }

    [Fact]
    public void EmptyReadOnlyBlocksAreOmitted_EditableBlocksAlwaysPresent()
    {
        var composition = PromptComposer.Compose(new PromptCompositionInputs { TaskId = "T1" });

        Assert.Equal([PromptBlockKind.TaskTitle, PromptBlockKind.TaskContext],
            composition.Blocks.Select(b => b.Kind));
        Assert.All(composition.Blocks, b => Assert.True(b.Editable));
        Assert.Null(composition.Block(PromptBlockKind.Persona));
        Assert.Null(composition.Block(PromptBlockKind.Tools));
    }

    [Fact]
    public void MissingPersonaName_StillLabelsThePersonaBlock()
    {
        var inputs = FullInputs();
        inputs.PersonaName = "";
        var composition = PromptComposer.Compose(inputs);
        Assert.Equal("Persona", composition.Block(PromptBlockKind.Persona)!.Label);
    }
}
