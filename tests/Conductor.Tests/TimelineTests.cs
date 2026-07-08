using Conductor.Core.Events;

namespace Conductor.Tests;

/// <summary>
/// B5.1 — the <see cref="Timeline"/> projection folds the append-only event log into ordered
/// transitions with correct wall-clock durations. Uses a REAL recorded stream (the same fixture shape
/// the B2.2 parity tests use) so the durations asserted here are the ones a real run produces, and a
/// synthetic stream with owner-gate + gate-fail + attention so every entry kind is exercised.
/// Per the B5 trap the timeline is a pure fold over the single event log — never a parallel store.
/// </summary>
public class TimelineTests
{
    // A real two-session run: seq/ts are the exact values the in-tree orchestrator recorded (B2.2).
    // Session #1 ran 06:53:01.33 → 06:53:06.16 (≈4.835s); #2 06:53:07.51 → 06:53:11.24 (≈3.724s).
    private const string RecordedRun = """
    {"type":"runStarted","plan":"BatonSmoke","repo":"C:/tmp/b22","branch":"master","resumed":false,"seq":1,"ts":"2026-07-08T06:53:00.6131722+00:00","runId":"r1"}
    {"type":"stageEntered","stageId":"S1","title":"Smoke","startHead":"06d28c7","seq":2,"ts":"2026-07-08T06:53:00.8255178+00:00","runId":"r1"}
    {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":6,"agentSessionId":"a1","seq":3,"ts":"2026-07-08T06:53:01.330662+00:00","runId":"r1","sessionId":"1"}
    {"type":"gateFinished","name":"build","passed":true,"skipped":false,"optional":false,"exitCode":0,"durationMs":886,"scope":"session","seq":4,"ts":"2026-07-08T06:53:05.6627602+00:00","runId":"r1","sessionId":"1"}
    {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"Advanced","newCommits":["f7c2bcf feat"],"newlyDone":["S1.1"],"costUsd":0.0003,"tokensInput":230,"tokensOutput":140,"seq":5,"ts":"2026-07-08T06:53:06.165565+00:00","runId":"r1","sessionId":"1"}
    {"type":"checkpointConfirmed","checkpointId":"S1.1","stageId":"S1","seq":6,"ts":"2026-07-08T06:53:06.1659288+00:00","runId":"r1","sessionId":"1"}
    {"type":"sessionStarted","number":2,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":6,"agentSessionId":"a2","seq":8,"ts":"2026-07-08T06:53:07.5143043+00:00","runId":"r1","sessionId":"2"}
    {"type":"sessionFinished","number":2,"stageId":"S1","outcome":"Advanced","newCommits":["a6cbb4e feat"],"newlyDone":["S1.2"],"costUsd":0.0003,"seq":10,"ts":"2026-07-08T06:53:11.2384477+00:00","runId":"r1","sessionId":"2"}
    {"type":"checkpointConfirmed","checkpointId":"S1.2","stageId":"S1","seq":11,"ts":"2026-07-08T06:53:11.238986+00:00","runId":"r1","sessionId":"2"}
    """;

    private static IReadOnlyList<ConductorEvent> Parse(string ndjson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-tl-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, ndjson);
        try { return EventLog.ReadAll(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SessionDurationIsTheSpanBetweenStartedAndFinished()
    {
        var timeline = Timeline.Build(Parse(RecordedRun));

        var finished = timeline.Where(e => e.Kind == Timeline.EntryKind.Session && e.Label.Contains("→", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, finished.Count);

        // #1: 06:53:06.165565 − 06:53:01.330662 = 4.834903s
        Assert.NotNull(finished[0].Duration);
        Assert.Equal(4.8, finished[0].Duration!.Value.TotalSeconds, precision: 1);
        // #2: 06:53:11.2384477 − 06:53:07.5143043 = 3.724143s
        Assert.NotNull(finished[1].Duration);
        Assert.Equal(3.7, finished[1].Duration!.Value.TotalSeconds, precision: 1);
    }

    [Fact]
    public void SessionStartedRowHasNoDurationAndPreservesOrder()
    {
        var timeline = Timeline.Build(Parse(RecordedRun));

        // The stream is ordered by Seq regardless of input order, and the started rows carry no span.
        Assert.True(timeline.Select(e => e.Seq).SequenceEqual(timeline.Select(e => e.Seq).OrderBy(s => s)));
        var started = timeline.First(e => e.Kind == Timeline.EntryKind.Session && e.Label.Contains("started", StringComparison.Ordinal));
        Assert.Null(started.Duration);
    }

    [Fact]
    public void GateEntryCarriesTheEngineMeasuredDuration()
    {
        var timeline = Timeline.Build(Parse(RecordedRun));
        var gate = Assert.Single(timeline, e => e.Kind == Timeline.EntryKind.Gate);
        Assert.Equal(TimeSpan.FromMilliseconds(886), gate.Duration);
        Assert.Contains("build", gate.Label, StringComparison.Ordinal);
        Assert.Contains("pass", gate.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckpointConfirmedIsAPointEventWithNoDuration()
    {
        var timeline = Timeline.Build(Parse(RecordedRun));
        var cps = timeline.Where(e => e.Kind == Timeline.EntryKind.Checkpoint).ToList();
        Assert.Equal(2, cps.Count);
        Assert.All(cps, c => Assert.Null(c.Duration));
        Assert.Contains(cps, c => c.Label.Contains("S1.1", StringComparison.Ordinal));
    }

    [Fact]
    public void FoldIsDeterministicRegardlessOfInputOrder()
    {
        var events = Parse(RecordedRun);
        var forward = Timeline.Build(events);
        var reversed = Timeline.Build(events.Reverse().ToList());
        Assert.Equal(forward.Select(Timeline.Format), reversed.Select(Timeline.Format));
    }

    [Fact]
    public void StageAndRunSpansAreMeasuredAndEveryKindRenders()
    {
        // Synthetic stream reaching stageConfirmed, owner-gate, a FAILED gate, attention, and runFinished
        // so all seven entry kinds are produced and the stage/run durations are computed end-to-end.
        const string full = """
        {"type":"runStarted","plan":"Loom","repo":"C:/r","branch":"feat","resumed":false,"seq":1,"ts":"2026-07-07T10:00:00Z","runId":"r"}
        {"type":"stageEntered","stageId":"L0","title":"Foundations","startHead":"a1","seq":2,"ts":"2026-07-07T10:00:10Z","runId":"r"}
        {"type":"sessionStarted","number":1,"stageId":"L0","kind":"Deliver","attempt":1,"maxAttempts":4,"agentSessionId":"s1","seq":3,"ts":"2026-07-07T10:00:20Z","runId":"r","sessionId":"1"}
        {"type":"gateFinished","name":"tests","passed":false,"skipped":false,"optional":false,"exitCode":1,"durationMs":5000,"scope":"phase","seq":4,"ts":"2026-07-07T10:05:00Z","runId":"r","sessionId":"1"}
        {"type":"attentionRequested","reason":"gate failed","seq":5,"ts":"2026-07-07T10:05:01Z","runId":"r","sessionId":"1"}
        {"type":"sessionFinished","number":1,"stageId":"L0","outcome":"Advanced","newlyDone":["L0.1"],"seq":6,"ts":"2026-07-07T10:30:20Z","runId":"r","sessionId":"1"}
        {"type":"checkpointConfirmed","checkpointId":"L0.1","stageId":"L0","seq":7,"ts":"2026-07-07T10:30:21Z","runId":"r","sessionId":"1"}
        {"type":"ownerApprovalRequested","stageId":"L0","seq":8,"ts":"2026-07-07T10:31:00Z","runId":"r"}
        {"type":"ownerApprovalGranted","stageId":"L0","seq":9,"ts":"2026-07-07T10:35:00Z","runId":"r"}
        {"type":"stageConfirmed","stageId":"L0","audited":true,"seq":10,"ts":"2026-07-07T10:36:10Z","runId":"r"}
        {"type":"runFinished","status":"Completed","sessions":1,"checkpointsDone":1,"checkpointsTotal":1,"seq":11,"ts":"2026-07-07T10:40:00Z","runId":"r"}
        """;
        var timeline = Timeline.Build(Parse(full));

        var kinds = timeline.Select(e => e.Kind).Distinct().ToHashSet();
        foreach (var k in Enum.GetValues<Timeline.EntryKind>())
            Assert.Contains(k, kinds);

        // Stage span: 10:00:10 entered → 10:36:10 confirmed = 36m.
        var stageConfirmed = timeline.First(e => e.Kind == Timeline.EntryKind.Stage && e.Label.Contains("confirmed", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromMinutes(36), stageConfirmed.Duration);

        // Run span: 10:00:00 → 10:40:00 = 40m.
        var runFinished = timeline.First(e => e.Kind == Timeline.EntryKind.Run && e.Label.Contains("finished", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromMinutes(40), runFinished.Duration);

        // A failed gate renders FAIL (conservative — no false "pass"), a point event carries no span.
        var gate = timeline.First(e => e.Kind == Timeline.EntryKind.Gate);
        Assert.Contains("FAIL", gate.Label, StringComparison.Ordinal);
        Assert.Null(timeline.First(e => e.Kind == Timeline.EntryKind.Attention).Duration);

        // Format is stable and includes the duration suffix for a span.
        Assert.Contains("36m00s", Timeline.Format(stageConfirmed), StringComparison.Ordinal);
    }

    [Fact]
    public void TokenDeltasAreExcludedFromTheTransitionTimeline()
    {
        // Token/cost accrual is LiveMetrics' concern; the timeline is state transitions only (B5 trap).
        const string withDeltas = """
        {"type":"runStarted","plan":"P","repo":"C:/r","resumed":false,"seq":1,"ts":"2026-07-07T10:00:00Z","runId":"r"}
        {"type":"tokenDelta","input":100,"output":50,"reasoning":0,"cacheRead":0,"costUsd":0.01,"seq":2,"ts":"2026-07-07T10:00:01Z","runId":"r","sessionId":"1"}
        {"type":"runFinished","status":"Completed","sessions":0,"checkpointsDone":0,"checkpointsTotal":0,"seq":3,"ts":"2026-07-07T10:00:02Z","runId":"r"}
        """;
        var timeline = Timeline.Build(Parse(withDeltas));
        Assert.DoesNotContain(timeline, e => e.Label.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, timeline.Count); // run started + run finished only
    }

    [Fact]
    public void EmptyLogYieldsEmptyTimeline()
        => Assert.Empty(Timeline.Build(Array.Empty<ConductorEvent>()));
}
