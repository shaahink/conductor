using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// B9.5 — verifies the event-log → TaskGraph → display pipeline shared by CLI, TUI, and Telegram views.
/// All three views read events.jsonl, fold through TaskGraph, and format tasks per checkpoint.
/// </summary>
public class TaskViewTests
{
    [Fact]
    public void ReadAll_Fold_CheckpointsGroupedCorrectly()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "events.jsonl");
        try
        {
            WriteEvents(path, new ConductorEvent[]
            {
                new TaskAdded { RunId = "r1", Seq = 1, TaskId = "t1", CheckpointId = "B9.1", Title = "Add events", Source = "planner", Order = 1 },
                new TaskAdded { RunId = "r1", Seq = 2, TaskId = "t2", CheckpointId = "B9.1", Title = "Add MCP server", Source = "planner", Order = 2 },
                new TaskAdded { RunId = "r1", Seq = 3, TaskId = "t3", CheckpointId = "B9.5", Title = "CLI command", Source = "planner", Order = 1 },
                new TaskAdded { RunId = "r1", Seq = 4, TaskId = "t4", CheckpointId = "B9.5", Title = "TUI pane", Source = "planner", Order = 2 },
                new TaskAdded { RunId = "r1", Seq = 5, TaskId = "t5", CheckpointId = "B9.5", Title = "Telegram /tasks", Source = "planner", Order = 3 },
                new TaskStatusChanged { RunId = "r1", Seq = 6, TaskId = "t1", Status = "done" },
                new TaskStatusChanged { RunId = "r1", Seq = 7, TaskId = "t2", Status = "in_progress" },
                new TaskStatusChanged { RunId = "r1", Seq = 8, TaskId = "t4", Status = "skipped" },
            });

            var events = EventLog.ReadAll(path);
            var graph = new TaskGraph();
            graph.Fold(events);

            Assert.Equal(5, graph.Count);

            var b91 = graph.ForCheckpoint("B9.1");
            Assert.Equal(2, b91.Count);
            Assert.Equal("done", b91[0].Status);
            Assert.Equal("in_progress", b91[1].Status);

            var b95 = graph.ForCheckpoint("B9.5");
            Assert.Equal(3, b95.Count);
            Assert.Equal("todo", b95[0].Status);
            Assert.Equal("skipped", b95[1].Status);
            Assert.Equal("todo", b95[2].Status);
        }
        finally
        {
            try { TestTemp.DeleteTree(dir); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void ReadAll_EmptyLog_ReturnsEmptyGraph()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "events.jsonl");
        try
        {
            File.WriteAllText(path, "");

            var events = EventLog.ReadAll(path);
            var graph = new TaskGraph();
            graph.Fold(events);

            Assert.Empty(events);
            Assert.Equal(0, graph.Count);
        }
        finally
        {
            try { TestTemp.DeleteTree(dir); } catch { }
        }
    }

    [Fact]
    public void ReadAll_MissingFile_ReturnsEmptyList()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-test-nosuch{Guid.NewGuid():N}", "events.jsonl");
        var events = EventLog.ReadAll(path);
        Assert.Empty(events);

        var graph = new TaskGraph();
        graph.Fold(events);
        Assert.Equal(0, graph.Count);
    }

    [Fact]
    public void Fold_ChecksGroupingAndOrder()
    {
        var graph = new TaskGraph();
        graph.Fold(new ConductorEvent[]
        {
            new TaskAdded { RunId = "r1", Seq = 1, TaskId = "a", CheckpointId = "S2", Title = "Second task in S2", Source = "planner", Order = 2 },
            new TaskAdded { RunId = "r1", Seq = 2, TaskId = "b", CheckpointId = "S1", Title = "First task in S1", Source = "planner", Order = 1 },
            new TaskAdded { RunId = "r1", Seq = 3, TaskId = "c", CheckpointId = "S1", Title = "Second task in S1", Source = "agent", Order = 2 },
            new TaskAdded { RunId = "r1", Seq = 4, TaskId = "d", CheckpointId = "S2", Title = "First task in S2", Source = "planner", Order = 1 },
        });

        var distinctCheckpoints = graph.Tasks.GroupBy(t => t.CheckpointId).Select(g => g.Key).ToList();
        Assert.Equal(2, distinctCheckpoints.Count);

        var s1 = graph.ForCheckpoint("S1");
        Assert.Equal(2, s1.Count);
        Assert.Equal(1, s1[0].Order);
        Assert.Equal(2, s1[1].Order);

        var s2 = graph.ForCheckpoint("S2");
        Assert.Equal(2, s2.Count);
        Assert.Equal(1, s2[0].Order);
        Assert.Equal(2, s2[1].Order);
    }

    private static void WriteEvents(string path, IEnumerable<ConductorEvent> events)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(fs);
        foreach (var evt in events)
        {
            var line = JsonSerializer.Serialize(evt, EventJsonContext.Default.ConductorEvent);
            writer.WriteLine(line);
        }
        writer.Flush();
    }
}
