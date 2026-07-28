using Conductor.Core;
using Conductor.Core.Events;

namespace Conductor.Tests;

/// <summary>
/// B9.2 gate: given a checkpoint, the planner produces ≥1 ordered sub-task recorded in the graph
/// (test with fake/deterministic planner output).
/// </summary>
public class PlannerTests
{
    [Fact]
    public void CheckpointPlanner_ProducesAtLeastOneSubTask()
    {
        var planner = new CheckpointPlanner();
        var tasks = planner.Decompose("B9.2", "Planner persona decomposes active checkpoint → ordered sub-tasks", "");

        Assert.NotEmpty(tasks);
        Assert.All(tasks, t => Assert.True(t.Title.Length > 0));
    }

    [Fact]
    public void CheckpointPlanner_ProducesOrderedOutput()
    {
        var planner = new CheckpointPlanner();
        var tasks = planner.Decompose("X1", "Foo + Bar + Baz", "");

        Assert.Equal(3, tasks.Count);
        Assert.Equal(1, tasks[0].Order);
        Assert.Equal(2, tasks[1].Order);
        Assert.Equal(3, tasks[2].Order);
    }

    [Fact]
    public void CheckpointPlanner_SimpleTitle_ProducesSingleTask()
    {
        var planner = new CheckpointPlanner();
        var tasks = planner.Decompose("X1", "SimpleTask", "");

        Assert.Single(tasks);
        Assert.Equal("SimpleTask", tasks[0].Title);
        Assert.Equal(1, tasks[0].Order);
    }

    [Fact]
    public void CheckpointPlanner_SplitsOnPlus()
    {
        var planner = new CheckpointPlanner();
        var tasks = planner.Decompose("X1", "Model + Projection + Tests", "");

        Assert.Equal(3, tasks.Count);
        Assert.Contains(tasks, t => t.Title == "Model");
        Assert.Contains(tasks, t => t.Title == "Projection");
        Assert.Contains(tasks, t => t.Title == "Tests");
    }

    [Fact]
    public void FakePlanner_DecomposeIntoTaskGraph()
    {
        // B9.2 gate: planner → TaskAdded events → TaskGraph projection
        var fakePlanner = new FakePlanner(("Add the model", 1), ("Write tests", 2), ("Verify gates", 3));
        var tasks = fakePlanner.Decompose("B9.2", "any title", "any notes");

        var events = new List<ConductorEvent>();
        foreach (var t in tasks)
            events.Add(new TaskAdded
            {
                RunId = "r1",
                TaskId = $"B9.2-t{t.Order}",
                CheckpointId = "B9.2",
                Title = t.Title,
                Source = "planner",
                Order = t.Order,
            });

        var graph = new TaskGraph();
        graph.Fold(events);

        Assert.Equal(3, graph.Count);
        var cpTasks = graph.ForCheckpoint("B9.2");
        Assert.Equal(3, cpTasks.Count);
        Assert.Equal("Add the model", cpTasks[0].Title);
        Assert.Equal("todo", cpTasks[0].Status);
        Assert.Equal("Write tests", cpTasks[1].Title);
        Assert.Equal("Verify gates", cpTasks[2].Title);
    }

    [Fact]
    public void CheckpointPlanner_EmptyTitle_ProducesSingleTask()
    {
        var planner = new CheckpointPlanner();
        // The method receives the title from the tracker; an empty/whitespace title should still return 1 task.
        var tasks = planner.Decompose("X1", "   ", "");

        Assert.Single(tasks);
    }

    // Deterministic fake planner for the B9.2 gate test.
    private sealed class FakePlanner(params (string Title, int Order)[] tasks) : IPlanner
    {
        public IReadOnlyList<PlannedTask> Decompose(string checkpointId, string checkpointTitle, string stageNotes)
            => tasks.Select(t => new PlannedTask(t.Title, t.Order)).ToList();
    }
}
