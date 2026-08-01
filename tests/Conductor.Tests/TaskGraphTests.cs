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
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "skipped", Seq = 3 }, // done → skipped stays illegal
        ]);

        Assert.Equal("done", graph.Find("t1")!.Status);
    }

    [Fact]
    public void Fold_ReopeningADoneTask_IsLegal()
    {
        // G2: the Kanban ←-move — pulling a card back out of Done/Skipped reopens it.
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "B9.1", Title = "Add events", Source = "planner", Order = 1, Seq = 1 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "done", Seq = 2 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "in_progress", Seq = 3 },
        ]);

        Assert.Equal("in_progress", graph.Find("t1")!.Status);
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

    [Fact]
    public void Fold_DuplicateTaskAdded_FirstWriteWins()
    {
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "B9.1", Title = "Original", Source = "planner", Order = 1, Seq = 1 },
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "B9.1", Title = "Duplicate", Source = "agent", Order = 99, Seq = 2 },
        ]);

        Assert.Equal(1, graph.Count);
        Assert.Equal("Original", graph.Find("t1")!.Title);
        Assert.Equal(1, graph.Find("t1")!.Order);
    }

    // ── SF3.2: the card meta a board shows without selecting the card. The Kanban had nothing to
    // print under an unselected card because the graph knew none of this: a status change carries no
    // session number and nothing counted pickups. All three are folded, so a replay reproduces them.

    [Fact]
    public void Fold_StatusChange_IsAttributedToTheSessionInFlight()
    {
        var t0 = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "SF3.2", CheckpointId = "SF3.2", Title = "Kanban", Source = "planner", Order = 1, Seq = 1, Ts = t0 },
            new SessionStarted { RunId = "r1", Number = 13, StageId = "SF3", Kind = "session", Seq = 2, Ts = t0.AddMinutes(1) },
            new SessionStarted { RunId = "r1", Number = 14, StageId = "SF3", Kind = "session", Seq = 3, Ts = t0.AddMinutes(2) },
            new TaskStatusChanged { RunId = "r1", TaskId = "SF3.2", Status = "in_progress", Seq = 4, Ts = t0.AddMinutes(3) },
        ]);

        var card = graph.Find("SF3.2")!;
        Assert.Equal(14, card.SessionNumber);
        Assert.Equal(t0.AddMinutes(3), card.StatusSinceUtc);
        Assert.Equal(1, card.Attempts);
    }

    [Fact]
    public void Fold_SeededCardKeepsItsAddedStampAndNoSession()
    {
        var t0 = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "SF9.9", CheckpointId = "SF9.9", Title = "Untouched", Source = "planner", Order = 1, Seq = 1, Ts = t0 },
        ]);

        var card = graph.Find("SF9.9")!;
        Assert.Equal(t0, card.StatusSinceUtc);
        Assert.Equal(0, card.SessionNumber);
        Assert.Equal(0, card.Attempts);
    }

    [Fact]
    public void Fold_ReopenedCard_CountsEveryPickup()
    {
        var t0 = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "F1.1", Title = "Flaky", Source = "planner", Order = 1, Seq = 1, Ts = t0 },
            new SessionStarted { RunId = "r1", Number = 3, StageId = "F1", Kind = "session", Seq = 2, Ts = t0 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "in_progress", Seq = 3, Ts = t0.AddMinutes(1) },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "done", Seq = 4, Ts = t0.AddMinutes(2) },
            // The verdict engine rejected the claim and the card went back for another go.
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "in_progress", Seq = 5, Ts = t0.AddMinutes(3) },
            new SessionStarted { RunId = "r1", Number = 4, StageId = "F1", Kind = "fix", Seq = 6, Ts = t0.AddMinutes(4) },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "done", Seq = 7, Ts = t0.AddMinutes(5) },
        ]);

        var card = graph.Find("t1")!;
        Assert.Equal(2, card.Attempts);
        Assert.Equal(4, card.SessionNumber);
        Assert.Equal(t0.AddMinutes(5), card.StatusSinceUtc);
    }

    [Fact]
    public void Fold_RepeatedDoneClaim_IsAMetadataRefreshNotAMove()
    {
        var t0 = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "t1", CheckpointId = "F1.1", Title = "Done once", Source = "planner", Order = 1, Seq = 1, Ts = t0 },
            new SessionStarted { RunId = "r1", Number = 3, StageId = "F1", Kind = "session", Seq = 2, Ts = t0 },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "in_progress", Seq = 3, Ts = t0.AddMinutes(1) },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "done", Seq = 4, Ts = t0.AddMinutes(2) },
            // The engine re-asserts the same claim hours later to attach the commit it verified.
            new SessionStarted { RunId = "r1", Number = 5, StageId = "F2", Kind = "session", Seq = 5, Ts = t0.AddHours(3) },
            new TaskStatusChanged { RunId = "r1", TaskId = "t1", Status = "done", Commit = "abc1234", Seq = 6, Ts = t0.AddHours(4) },
        ]);

        var card = graph.Find("t1")!;
        Assert.Equal("abc1234", card.Commit);
        Assert.Equal(t0.AddMinutes(2), card.StatusSinceUtc);
        Assert.Equal(3, card.SessionNumber);
        Assert.Equal(1, card.Attempts);
    }

    [Fact]
    public void FromTasks_CarriesTheCardMetaOntoTheWire()
    {
        var t0 = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var graph = new TaskGraph();
        graph.Fold([
            new TaskAdded { RunId = "r1", TaskId = "SF3.2", CheckpointId = "SF3.2", Title = "Kanban", Source = "planner", Order = 1, Seq = 1, Ts = t0 },
            new SessionStarted { RunId = "r1", Number = 14, StageId = "SF3", Kind = "session", Seq = 2, Ts = t0 },
            new TaskStatusChanged { RunId = "r1", TaskId = "SF3.2", Status = "in_progress", Seq = 3, Ts = t0.AddMinutes(7) },
        ]);

        var dto = Conductor.Core.Http.ControlPlaneDto.FromTasks(graph.Tasks).Tasks.Single();
        Assert.Equal(14, dto.SessionNumber);
        Assert.Equal(1, dto.Attempts);
        Assert.Equal(t0.AddMinutes(7).ToString("O"), dto.StatusSinceUtc);
        Assert.Equal("SF3", dto.StageId);
    }
}
