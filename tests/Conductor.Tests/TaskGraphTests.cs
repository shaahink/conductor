using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Tests;

public class TaskGraphTests
{
    [Fact]
    public void Fold_TaskAdded_CreatesTaskWithTodoStatus()
    {
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "B9.1", Title = "Add events", Source = "planner", Order = 1, Seq = 1 },
        ]);

        Assert.Equal(1, graph.Count);
        var task = graph.Find("t1");
        Assert.NotNull(task);
        Assert.Equal("t1", task!.TaskId);
        Assert.Equal("B9.1", task.CheckpointId);
        Assert.Equal("Add events", task.Title);
        Assert.Equal("todo", task.Status);
        Assert.Equal("planner", task.Source);
        Assert.Equal(1, task.Order);
    }

    [Fact]
    public void Fold_TaskStatusChanged_UpdatesStatus()
    {
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "B9.1", Title = "Add events", Source = "planner", Order = 1, Seq = 1 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "in_progress", Seq = 2 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "done", Seq = 3 },
        ]);

        Assert.Equal("done", graph.Find("t1")!.Status);
        Assert.Equal(3, graph.LastEventSeq);
    }

    [Fact]
    public void Fold_InvalidTransition_IsIgnored()
    {
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "B9.1", Title = "Add events", Source = "planner", Order = 1, Seq = 1 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "done", Seq = 2 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "in_progress", Seq = 3 },
        ]);

        Assert.Equal("done", graph.Find("t1")!.Status);
    }

    [Fact]
    public void Fold_StatusChangeForUnknownTask_IsHarmless()
    {
        var graph = new TaskGraph();
        graph.Fold([
            new TaskStatusChanged { RunId = "r1", TaskId = "ghost", Status = "in_progress", Seq = 1 },
        ]);

        Assert.Equal(0, graph.Count);
    }

    [Fact]
    public void Fold_MultipleCheckpoints_OrdersWithinCheckpoint()
    {
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "B9.1", Title = "Model", Source = "planner", Order = 2, Seq = 1 },
            new TaskAdded { RunId = "r1", TaskId = "t2", CheckpointId = "B9.1", Title = "Projection", Source = "planner", Order = 1, Seq = 2 },
            new TaskAdded { RunId = "r1", TaskId = "t3", CheckpointId = "B9.2", Title = "Planner", Source = "planner", Order = 1, Seq = 3 },
        ]);

        Assert.Equal(3, graph.Count);

        var cp1Tasks = graph.ForCheckpoint("B9.1");
        Assert.Equal(2, cp1Tasks.Count);
        Assert.Equal("t2", cp1Tasks[0].TaskId); // order 1
        Assert.Equal("t1", cp1Tasks[1].TaskId); // order 2

        var cp2Tasks = graph.ForCheckpoint("B9.2");
        Assert.Single(cp2Tasks);
    }

    [Fact]
    public void CurrentTask_ReturnsFirstNonDoneNonSkipped()
    {
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "B9.1", Title = "One", Source = "planner", Order = 1, Seq = 1 },
            new TaskAdded { RunId = "r1", TaskId = "t2", CheckpointId = "B9.1", Title = "Two", Source = "planner", Order = 2, Seq = 2 },
            new TaskAdded { RunId = "r1", TaskId = "t3", CheckpointId = "B9.1", Title = "Three", Source = "planner", Order = 3, Seq = 3 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "done", Seq = 4 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t2", Status = "in_progress", Seq = 5 },
        ]);

        var current = graph.CurrentTask("B9.1");
        Assert.NotNull(current);
        Assert.Equal("t2", current!.TaskId); // first non-done
    }

    [Fact]
    public void CurrentTask_ReturnsNullWhenAllDone()
    {
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "B9.1", Title = "One", Source = "planner", Order = 1, Seq = 1 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "done", Seq = 2 },
        ]);

        Assert.Null(graph.CurrentTask("B9.1"));
    }

    [Fact]
    public void Fold_SkipTransition_IsValid()
    {
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "B9.1", Title = "Skip me", Source = "planner", Order = 1, Seq = 1 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "skipped", Seq = 2 },
        ]);

        Assert.Equal("skipped", graph.Find("t1")!.Status);
        Assert.Null(graph.CurrentTask("B9.1")); // skipped is not current
    }

    [Fact]
    public void Fold_FromEmpty_ProducesZeroCount()
    {
        var graph = new TaskGraph();
        graph.Fold([]);
        Assert.Equal(0, graph.Count);
        Assert.Equal(0, graph.LastEventSeq);
    }

    [Fact]
    public void EventRoundTrip_TaskAdded_SerialisesThroughSourceGen()
    {
        var samples = new ConductorEvent[]
        {
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "B9.1", Title = "Test", Source = "planner", Order = 1 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "in_progress" },
        };

        foreach (var evt in samples)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(evt, EventJsonContext.Default.ConductorEvent);
            var back = System.Text.Json.JsonSerializer.Deserialize(json, EventJsonContext.Default.ConductorEvent);
            Assert.NotNull(back);
            Assert.Equal(evt.GetType(), back!.GetType());
        }
    }
}
