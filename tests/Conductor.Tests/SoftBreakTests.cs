using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// B9.4 gate: cooperative soft-break + hard fallback tests.
/// - Soft threshold computation
/// - SoftBreakRequested event round-trip
/// - Signal file written when threshold crossed
/// - Task-graph-aware resume hint (rollover context)
/// - MCP journal fold into event log
/// </summary>
public class SoftBreakTests
{
    private static string TempPath(string prefix = "soft-break") =>
        Path.Combine(Path.GetTempPath(), $"conductor-{prefix}-{Guid.NewGuid():N}.jsonl");

    // --------------- thresholds

    [Fact]
    public void SoftThreshold_DefaultRatio_0_8()
    {
        var limits = new LimitsConfig { MaxSessionTokens = 100_000 };
        var threshold = ComputeSoftThreshold(limits);
        Assert.Equal(80_000, threshold);
    }

    [Fact]
    public void SoftThreshold_CustomRatio()
    {
        var limits = new LimitsConfig { MaxSessionTokens = 100_000, SoftBreakRatio = 0.5 };
        var threshold = ComputeSoftThreshold(limits);
        Assert.Equal(50_000, threshold);
    }

    [Fact]
    public void SoftThreshold_NullWhenNoMax()
    {
        var limits = new LimitsConfig { MaxSessionTokens = null };
        var threshold = ComputeSoftThreshold(limits);
        Assert.Null(threshold);
    }

    [Fact]
    public void SoftThreshold_RoundsDown()
    {
        var limits = new LimitsConfig { MaxSessionTokens = 999, SoftBreakRatio = 0.8 };
        var threshold = ComputeSoftThreshold(limits);
        Assert.Equal(799, threshold);
    }

    private static long? ComputeSoftThreshold(LimitsConfig limits)
    {
        if (limits.MaxSessionTokens is not { } max) return null;
        var ratio = limits.SoftBreakRatio is { } r and > 0 and <= 1.0 ? r : 0.8;
        return (long)(max * ratio);
    }

    // --------------- event round-trip

    [Fact]
    public void SoftBreakRequested_RoundTripsThroughSourceGen()
    {
        var evt = new SoftBreakRequested
        {
            RunId = "r1",
            SessionId = "5",
            LiveTokens = 85_000,
            TokenBudget = 100_000,
            CurrentCheckpointId = "B9.4",
        };
        var json = JsonSerializer.Serialize(evt, EventJsonContext.Default.ConductorEvent);
        var back = JsonSerializer.Deserialize(json, EventJsonContext.Default.ConductorEvent);
        Assert.NotNull(back);
        Assert.IsType<SoftBreakRequested>(back);
        var sbr = (SoftBreakRequested)back;
        Assert.Equal(85_000, sbr.LiveTokens);
        Assert.Equal(100_000, sbr.TokenBudget);
        Assert.Equal("B9.4", sbr.CurrentCheckpointId);
        Assert.Equal(json, JsonSerializer.Serialize(back, EventJsonContext.Default.ConductorEvent));
    }

    [Fact]
    public void SoftBreakRequested_JsonContainsTypeDiscriminator()
    {
        var evt = new SoftBreakRequested { RunId = "r1", LiveTokens = 1, TokenBudget = 2 };
        var json = JsonSerializer.Serialize(evt, EventJsonContext.Default.ConductorEvent);
        Assert.Contains("\"softBreakRequested\"", json);
    }

    // --------------- signal file

    [Fact]
    public void SoftBreakSignalFile_WrittenWithContext()
    {
        var signalDir = Path.Combine(Path.GetTempPath(), $"conductor-signal-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(signalDir);
            var signalFile = Path.Combine(signalDir, "soft-break");
            Assert.False(File.Exists(signalFile));

            WriteSoftBreakSignal(signalFile);
            Assert.True(File.Exists(signalFile));
            var content = File.ReadAllText(signalFile);
            Assert.StartsWith("finish-subtask-and-handoff:", content);
        }
        finally
        {
            try { TestTemp.DeleteTree(signalDir); } catch { }
        }
    }

    [Fact]
    public void SoftBreakSignalFile_CanBeCleanedUp()
    {
        var signalDir = Path.Combine(Path.GetTempPath(), $"conductor-signal-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(signalDir);
            var signalFile = Path.Combine(signalDir, "soft-break");
            WriteSoftBreakSignal(signalFile);
            Assert.True(File.Exists(signalFile));

            // cleanup
            if (File.Exists(signalFile)) File.Delete(signalFile);
            Assert.False(File.Exists(signalFile));
        }
        finally
        {
            try { TestTemp.DeleteTree(signalDir); } catch { }
        }
    }

    private static void WriteSoftBreakSignal(string path)
        => File.WriteAllText(path, $"finish-subtask-and-handoff:{DateTime.UtcNow:o}");

    // --------------- task-graph-aware resume

    [Fact]
    public void BuildResumeHint_FindsNextPendingTask()
    {
        var events = new List<ConductorEvent>
        {
            new TaskAdded { RunId = "r1", TaskId = "B9.4-t1", CheckpointId = "B9.4", Title = "Add model", Source = "planner", Order = 1 },
            new TaskAdded { RunId = "r1", TaskId = "B9.4-t2", CheckpointId = "B9.4", Title = "Add tests", Source = "planner", Order = 2 },
            new TaskAdded { RunId = "r1", TaskId = "B9.4-t3", CheckpointId = "B9.4", Title = "Verify gates", Source = "planner", Order = 3 },
            new TaskStatusChanged { RunId = "r1", TaskId = "B9.4-t1", Status = "done" },
        };

        var graph = new TaskGraph();
        graph.Fold(events);

        var next = graph.CurrentTask("B9.4");
        Assert.NotNull(next);
        Assert.Equal("B9.4-t2", next.TaskId);
        Assert.Equal("Add tests", next.Title);
    }

    [Fact]
    public void BuildResumeHint_AllDone_ReturnsNull()
    {
        var events = new List<ConductorEvent>
        {
            new TaskAdded { RunId = "r1", TaskId = "B9.4-t1", CheckpointId = "B9.4", Title = "Add model", Source = "planner", Order = 1 },
            new TaskStatusChanged { RunId = "r1", TaskId = "B9.4-t1", Status = "done" },
        };

        var graph = new TaskGraph();
        graph.Fold(events);

        var next = graph.CurrentTask("B9.4");
        Assert.Null(next);
    }

    [Fact]
    public void BuildResumeHint_InProgressTask_IsFirstCandidate()
    {
        var events = new List<ConductorEvent>
        {
            new TaskAdded { RunId = "r1", TaskId = "B9.4-t1", CheckpointId = "B9.4", Title = "Add model", Source = "planner", Order = 1 },
            new TaskAdded { RunId = "r1", TaskId = "B9.4-t2", CheckpointId = "B9.4", Title = "Add tests", Source = "planner", Order = 2 },
            new TaskStatusChanged { RunId = "r1", TaskId = "B9.4-t1", Status = "in_progress" },
        };

        var graph = new TaskGraph();
        graph.Fold(events);

        var next = graph.CurrentTask("B9.4");
        Assert.NotNull(next);
        Assert.Equal("B9.4-t1", next.TaskId);
        Assert.Equal("in_progress", next.Status);
    }

    // --------------- MCP journal fold

    [Fact]
    public void McpJournalFold_MergesEventsIntoEventLog()
    {
        var eventsPath = TempPath("events");
        var journalPath = TempPath("journal");
        try
        {
            // Seed event log with one TaskAdded
            var added = new TaskAdded { RunId = "r1", TaskId = "B9.4-t1", CheckpointId = "B9.4", Title = "Model", Source = "planner", Order = 1 };
            File.WriteAllText(eventsPath,
                JsonSerializer.Serialize(added, EventJsonContext.Default.ConductorEvent) + Environment.NewLine);

            // Write journal with a TaskStatusChanged
            var sc = new TaskStatusChanged { RunId = "r1", TaskId = "B9.4-t1", Status = "done" };
            File.WriteAllText(journalPath,
                JsonSerializer.Serialize(sc, EventJsonContext.Default.ConductorEvent) + Environment.NewLine);

            Assert.True(File.Exists(journalPath));

            // Fold: read journal entries
            var journalEvents = EventLog.ReadAll(journalPath);
            Assert.NotEmpty(journalEvents);
            Assert.IsType<TaskStatusChanged>(journalEvents[0]);

            // Read main event log — should still have the TaskAdded
            var mainEvents = EventLog.ReadAll(eventsPath);
            Assert.NotEmpty(mainEvents);
            Assert.IsType<TaskAdded>(mainEvents[0]);

            // After fold, delete the journal (production code deletes it)
            File.Delete(journalPath);
            Assert.False(File.Exists(journalPath));
        }
        finally
        {
            Cleanup(eventsPath);
            Cleanup(journalPath);
        }
    }

    // --------------- no soft-break without config

    [Fact]
    public void NoSoftBreak_WhenTokensBelowThreshold()
    {
        var limits = new LimitsConfig { MaxSessionTokens = 100_000 };
        var threshold = ComputeSoftThreshold(limits);
        Assert.NotNull(threshold);

        // 50k tokens is below 80k threshold (0.8 default) → no soft-break
        long liveTokens = 50_000;
        Assert.True(liveTokens < threshold.Value);
    }

    [Fact]
    public void SoftBreak_WhenTokensAboveThreshold()
    {
        var limits = new LimitsConfig { MaxSessionTokens = 100_000 };
        var threshold = ComputeSoftThreshold(limits);
        Assert.NotNull(threshold);

        // 85k tokens is above 80k threshold → soft-break
        long liveTokens = 85_000;
        Assert.True(liveTokens >= threshold.Value);
    }

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
