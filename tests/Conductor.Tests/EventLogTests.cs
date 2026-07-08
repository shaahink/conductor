using System.Text.Json;
using Conductor.Core.Events;

namespace Conductor.Tests;

public class EventLogTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"conductor-events-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public void EveryEventTypeRoundTripsThroughSourceGen()
    {
        ConductorEvent[] samples =
        [
            new RunStarted { RunId = "r1", Plan = "Baton", Repo = @"C:\repo", Branch = "feat/baton", DriverVersion = "2.0.0", Resumed = true },
            new StageEntered { RunId = "r1", StageId = "B2", Title = "Spine", StartHead = "abc1234" },
            new SessionStarted { RunId = "r1", SessionId = "5", Number = 5, StageId = "B2", Kind = "Deliver", Attempt = 1, MaxAttempts = 6, AgentSessionId = "sess-xyz" },
            new SessionFinished { RunId = "r1", SessionId = "5", Number = 5, StageId = "B2", Outcome = "Advanced", NewCommits = ["a1 feat", "b2 fix"], NewlyDone = ["B2.1"], CostUsd = 0.42m, TokensInput = 100, TokensOutput = 50, TokensReasoning = 7, TokensCacheRead = 900 },
            new GateFinished { RunId = "r1", SessionId = "5", Name = "build", Passed = true, Skipped = false, Optional = false, ExitCode = 0, DurationMs = 1234, Scope = "session" },
            new CheckpointConfirmed { RunId = "r1", SessionId = "5", CheckpointId = "B2.1", StageId = "B2" },
            new StageConfirmed { RunId = "r1", StageId = "B2", Audited = true },
            new AttentionRequested { RunId = "r1", Reason = "needs a human" },
            new RunFinished { RunId = "r1", Status = "Completed", Sessions = 12, CheckpointsDone = 65, CheckpointsTotal = 65 },
            new TokenDelta { RunId = "r1", SessionId = "3", Input = 1500, Output = 200, Reasoning = 50, CacheRead = 8000, CostUsd = 0.02m },
            new OwnerApprovalRequested { RunId = "r1", StageId = "B3" },
            new OwnerApprovalGranted { RunId = "r1", StageId = "B3" },
        ];

        foreach (var evt in samples)
        {
            var json = JsonSerializer.Serialize(evt, EventJsonContext.Default.ConductorEvent);
            var back = JsonSerializer.Deserialize(json, EventJsonContext.Default.ConductorEvent);
            Assert.NotNull(back);
            Assert.Equal(evt.GetType(), back!.GetType()); // discriminator preserves the concrete type
            // Re-serialise the reconstructed event: canonical-JSON equality proves full payload
            // fidelity (records don't compare list members structurally, so compare the wire form).
            Assert.Equal(json, JsonSerializer.Serialize(back, EventJsonContext.Default.ConductorEvent));
        }
    }

    [Fact]
    public void SerializedLineIsCompactCamelCaseNdjsonWithDiscriminator()
    {
        var evt = new StageEntered { RunId = "r1", StageId = "B2", Title = "Spine", StartHead = "abc" };
        var json = JsonSerializer.Serialize<ConductorEvent>(evt, EventJsonContext.Default.ConductorEvent);

        Assert.DoesNotContain('\n', json);                 // one event = one line
        Assert.StartsWith("{\"type\":\"stageEntered\"", json); // discriminator first, camelCase name
        Assert.Contains("\"stageId\":\"B2\"", json);        // camelCase property naming
        Assert.DoesNotContain("sessionId", json);           // null envelope field omitted
    }

    [Fact]
    public void WriterProducesWellFormedAppendOnlyLogInOrder()
    {
        var path = TempPath();
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using (var log = new EventLog(path, "run-1", clock))
            {
                log.Emit(new RunStarted { Plan = "Baton", Repo = "r" });
                log.Emit(new StageEntered { StageId = "B2" });
                log.Emit(new SessionStarted { Number = 1, StageId = "B2", Kind = "Deliver" });
            }

            // Every physical line is independently valid JSON with the envelope stamped by the writer.
            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                Assert.True(doc.RootElement.TryGetProperty("type", out _));
                Assert.Equal("run-1", doc.RootElement.GetProperty("runId").GetString());
            }

            var events = EventLog.ReadAll(path);
            Assert.Collection(events,
                e => Assert.IsType<RunStarted>(e),
                e => Assert.IsType<StageEntered>(e),
                e => Assert.IsType<SessionStarted>(e));
            Assert.Equal([1, 2, 3], events.Select(e => e.Seq)); // stamped, monotonic, 1-based
            Assert.All(events, e => Assert.Equal(clock.GetUtcNow(), e.Ts));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SequenceContinuesAcrossRestart()
    {
        var path = TempPath();
        try
        {
            using (var log = new EventLog(path, "run-1"))
            {
                log.Emit(new StageEntered { StageId = "B2" });
                log.Emit(new StageEntered { StageId = "B2" });
            }
            using (var log = new EventLog(path, "run-1")) // resume: appends, seq keeps climbing
            {
                log.Emit(new StageEntered { StageId = "B3" });
            }

            var events = EventLog.ReadAll(path);
            Assert.Equal([1, 2, 3], events.Select(e => e.Seq));
            Assert.Equal("B3", Assert.IsType<StageEntered>(events[^1]).StageId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadAllToleratesATrailingTornLine()
    {
        var path = TempPath();
        try
        {
            using (var log = new EventLog(path, "run-1"))
                log.Emit(new StageEntered { StageId = "B2" });

            // Simulate a crash mid-flush: a valid line followed by a truncated one.
            File.AppendAllText(path, "{\"type\":\"stageEntered\",\"seq\":2,\"stag");

            var events = EventLog.ReadAll(path); // must not throw; drops the partial tail
            Assert.Single(events);
            Assert.Equal("B2", Assert.IsType<StageEntered>(events[0]).StageId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmitPreservesSessionIdSoLiveMetricsCanFoldPersistedDeltas()
    {
        // Regression: TokenDelta must reach disk with its SessionId intact. EventLog.Emit stamps
        // Seq/Ts/RunId but MUST NOT clobber the per-event SessionId the emitter set, or
        // LiveMetrics.ForSession (B2.6) folds nothing from a real run's log. Locks the end-to-end
        // path AgentSession → EventLog → LiveMetrics that a null sessionId silently broke.
        var path = TempPath();
        try
        {
            using (var log = new EventLog(path, "run-1"))
            {
                log.Emit(new TokenDelta { SessionId = "7", Input = 100, Output = 40, CostUsd = 0.01m });
                log.Emit(new TokenDelta { SessionId = "7", Input = 60, Output = 20, CostUsd = 0.02m });
                log.Emit(new TokenDelta { SessionId = "8", Input = 999, Output = 999 });
            }

            var events = EventLog.ReadAll(path);
            Assert.All(events.OfType<TokenDelta>(), td => Assert.False(string.IsNullOrEmpty(td.SessionId)));

            var totals = LiveMetrics.ForSession(events, sessionNumber: 7);
            Assert.Equal(160, totals.Input);   // 100 + 60, session 8's delta excluded
            Assert.Equal(60, totals.Output);
            Assert.Equal(0.03m, totals.CostUsd);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadAllSucceedsWhileLiveWriterHoldsTheFile()
    {
        // Crash recovery (Orchestrator.RecoverFromCrash) reads events.jsonl while the process's own
        // EventLog writer still holds it open. ReadAll must share-read that live Write handle rather
        // than throw a sharing violation — the real-run path a B2.5 --once smoke surfaced.
        var path = TempPath();
        try
        {
            using var log = new EventLog(path, "run-live");
            log.Emit(new RunStarted { Plan = "Baton", Repo = "r" });
            log.Emit(new SessionStarted { Number = 1, StageId = "B2", Kind = "Deliver" });

            IReadOnlyList<ConductorEvent> events = [];
            // Poll: the drain task flushes asynchronously. Give it a generous window — slow CI
            // machines may take several seconds to schedule the background task and flush. The
            // point is that reading NEVER throws while the writer is open (sharing violation).
            // Small initial sleep gives the drain task time to start before the first poll.
            Thread.Sleep(100);
            for (var i = 0; i < 100 && events.Count < 2; i++)
            {
                events = EventLog.ReadAll(path); // must not throw despite the open writer
                if (events.Count < 2) Thread.Sleep(50);
            }
            Assert.Equal(2, events.Count);
            Assert.IsType<RunStarted>(events[0]);
            Assert.IsType<SessionStarted>(events[1]);
        }
        finally { File.Delete(path); }
    }
}
