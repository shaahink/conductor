using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// B5.2 — the <see cref="Replay"/> projection reconstructs a past run from the append-only event log:
/// every transition in order, each paired with the run state as of that moment ("time-travel"). Uses
/// the SAME real recorded stream shape the timeline/parity tests use, so the reconstructed sequence
/// and totals are the ones a real run produces. Per the B5 trap it is a pure fold over the single
/// event log — the final step's cost/tokens must equal what <see cref="RunStateProjection"/> folds
/// (no drifting parallel accounting).
/// </summary>
public class ReplayTests
{
    // Two sessions across a resume: #1 confirms S1.1 (cost 0.0003, 230/140 tok), a gate fails then a
    // retry gate passes, #2 confirms S1.2, then the stage is confirmed and the run finishes.
    private const string RecordedRun = """
    {"type":"runStarted","plan":"BatonSmoke","repo":"C:/r","branch":"feat","resumed":false,"seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r1"}
    {"type":"stageEntered","stageId":"S1","title":"Smoke","startHead":"aaa","seq":2,"ts":"2026-07-08T10:00:05Z","runId":"r1"}
    {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"agentSessionId":"a1","seq":3,"ts":"2026-07-08T10:00:10Z","runId":"r1","sessionId":"1"}
    {"type":"gateFinished","name":"build","passed":false,"skipped":false,"optional":false,"exitCode":1,"durationMs":500,"scope":"session","seq":4,"ts":"2026-07-08T10:00:40Z","runId":"r1","sessionId":"1"}
    {"type":"gateFinished","name":"build","passed":true,"skipped":false,"optional":false,"exitCode":0,"durationMs":600,"scope":"session","seq":5,"ts":"2026-07-08T10:01:00Z","runId":"r1","sessionId":"1"}
    {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"Advanced","newCommits":["f7c2bcf feat"],"newlyDone":["S1.1"],"costUsd":0.0003,"tokensInput":230,"tokensOutput":140,"seq":6,"ts":"2026-07-08T10:01:20Z","runId":"r1","sessionId":"1"}
    {"type":"checkpointConfirmed","checkpointId":"S1.1","stageId":"S1","seq":7,"ts":"2026-07-08T10:01:21Z","runId":"r1","sessionId":"1"}
    {"type":"sessionStarted","number":2,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"agentSessionId":"a2","seq":8,"ts":"2026-07-08T10:02:00Z","runId":"r1","sessionId":"2"}
    {"type":"gateFinished","name":"tests","passed":true,"skipped":false,"optional":false,"exitCode":0,"durationMs":700,"scope":"phase","seq":9,"ts":"2026-07-08T10:02:30Z","runId":"r1","sessionId":"2"}
    {"type":"sessionFinished","number":2,"stageId":"S1","outcome":"Advanced","newCommits":["a6cbb4e feat"],"newlyDone":["S1.2"],"costUsd":0.0002,"tokensInput":100,"tokensOutput":60,"seq":10,"ts":"2026-07-08T10:02:50Z","runId":"r1","sessionId":"2"}
    {"type":"checkpointConfirmed","checkpointId":"S1.2","stageId":"S1","seq":11,"ts":"2026-07-08T10:02:51Z","runId":"r1","sessionId":"2"}
    {"type":"stageConfirmed","stageId":"S1","audited":false,"seq":12,"ts":"2026-07-08T10:03:00Z","runId":"r1"}
    {"type":"runFinished","status":"Completed","sessions":2,"checkpointsDone":2,"checkpointsTotal":2,"seq":13,"ts":"2026-07-08T10:03:05Z","runId":"r1"}
    """;

    private static IReadOnlyList<ConductorEvent> Parse(string ndjson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-replay-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, ndjson);
        try { return EventLog.ReadAll(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReconstructsEveryTransitionInSeqOrder()
    {
        var steps = Replay.Build(Parse(RecordedRun));

        // Every event is a transition here (no TokenDelta in this stream), so all 13 produce a step,
        // both gate rows included, in Seq order.
        Assert.Equal(13, steps.Count);
        var seqs = steps.Select(s => s.Entry.Seq).ToList();
        Assert.True(seqs.SequenceEqual(seqs.OrderBy(x => x)), "steps must be in Seq order");
        Assert.Equal(new[] { 1L, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }, seqs);
    }

    [Fact]
    public void TimeTravel_StateAsOfReflectsOnlyEventsUpToThatStep()
    {
        var steps = Replay.Build(Parse(RecordedRun));

        // Park on the S1.1-confirmed step: exactly one checkpoint is known, session #2 has NOT started,
        // and only session #1's cost/tokens have accrued — a later confirmation cannot leak backwards.
        var atS1_1 = steps.First(s => s.Entry.Kind == Timeline.EntryKind.Checkpoint && s.Entry.Label.Contains("S1.1", StringComparison.Ordinal));
        Assert.Equal(1, atS1_1.StateAsOf.CheckpointsConfirmed);
        Assert.Equal(1, atS1_1.StateAsOf.SessionsStarted);
        Assert.Equal(1, atS1_1.StateAsOf.SessionsFinished);
        Assert.Equal(0.0003m, atS1_1.StateAsOf.CostUsd);
        Assert.Equal(230, atS1_1.StateAsOf.TokensInput);
        Assert.Equal(1, atS1_1.StateAsOf.GatesPassed);   // the retry pass, seq 5
        Assert.Equal(1, atS1_1.StateAsOf.GatesFailed);   // the first fail, seq 4

        // Park earlier, on session #1 started: nothing confirmed yet, no cost accrued.
        var atStart = steps.First(s => s.Entry.Kind == Timeline.EntryKind.Session && s.Entry.Label.Contains("started", StringComparison.Ordinal));
        Assert.Equal(0, atStart.StateAsOf.CheckpointsConfirmed);
        Assert.Equal(0m, atStart.StateAsOf.CostUsd);
    }

    [Fact]
    public void FinalStateEqualsTheFoldedRunState_NoDrift()
    {
        var events = Parse(RecordedRun);
        var last = Replay.Build(events)[^1].StateAsOf;
        var folded = RunStateProjection.Fold(events);

        // The B5 trap: replay is not a parallel bookkeeping store. Its terminal totals must match the
        // authoritative RunState projection exactly.
        Assert.Equal(folded.TotalCostUsd, last.CostUsd);
        Assert.Equal(folded.TotalTokensInput, last.TokensInput);
        Assert.Equal(folded.TotalTokensOutput, last.TokensOutput);
        Assert.Equal(2, last.CheckpointsConfirmed);
        Assert.Equal(1, last.StagesConfirmed);
        Assert.Equal("S1", last.Stage);
    }

    [Fact]
    public void FoldIsDeterministicRegardlessOfInputOrder()
    {
        var events = Parse(RecordedRun);
        var forward = Replay.Build(events).SelectMany(Replay.FormatStep);
        var shuffled = Replay.Build(events.Reverse().ToList()).SelectMany(Replay.FormatStep);
        Assert.Equal(forward, shuffled);
    }

    [Fact]
    public void FormatStepRendersTheTransitionThenTheAsOfStrip()
    {
        var steps = Replay.Build(Parse(RecordedRun));
        var lines = Replay.FormatStep(steps.First(s => s.Entry.Kind == Timeline.EntryKind.Checkpoint)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("checkpoint S1.1 confirmed", lines[0], StringComparison.Ordinal);
        Assert.Contains("↳", lines[1], StringComparison.Ordinal);
        Assert.Contains("1 cp", lines[1], StringComparison.Ordinal);
        Assert.Contains("stage S1", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyLogYieldsNoSteps()
        => Assert.Empty(Replay.Build(Array.Empty<ConductorEvent>()));

    [Fact]
    public void TokenDeltaIsNotATransition_NoPhantomStep_NoDoubleCount()
    {
        // A TokenDelta is live-metrics accrual, not a state transition: it must not create a replay
        // step, and cost stays sourced from SessionFinished so it is not double-counted (B5 trap).
        const string withDelta = """
        {"type":"runStarted","plan":"P","repo":"C:/r","resumed":false,"seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r"}
        {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":2,"ts":"2026-07-08T10:00:01Z","runId":"r","sessionId":"1"}
        {"type":"tokenDelta","input":999,"output":999,"reasoning":0,"cacheRead":0,"costUsd":9.99,"seq":3,"ts":"2026-07-08T10:00:02Z","runId":"r","sessionId":"1"}
        {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"Advanced","costUsd":0.0001,"tokensInput":10,"tokensOutput":5,"seq":4,"ts":"2026-07-08T10:00:03Z","runId":"r","sessionId":"1"}
        """;
        var steps = Replay.Build(Parse(withDelta));

        Assert.DoesNotContain(steps, s => s.Entry.Seq == 3);        // no phantom step for the delta
        Assert.Equal(0.0001m, steps[^1].StateAsOf.CostUsd);         // not 9.99 + 0.0001
        Assert.Equal(10, steps[^1].StateAsOf.TokensInput);
    }
}
