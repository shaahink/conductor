using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// B2.2 parity: folding an append-only event stream back into a <see cref="RunState"/> reproduces the
/// legacy <c>state.json</c> on the event-owned surface (<see cref="StateProjectionParity"/>). This is
/// the D-5 precondition for treating the log as source-of-truth — until it holds, <c>state.json</c>
/// stays authoritative (additive discipline). Both fixtures round-trip through the real on-disk read
/// paths (<see cref="EventLog.ReadAll"/> + <see cref="RunState.LoadOrNew"/>), not in-memory shortcuts.
/// </summary>
public class RunStateProjectionTests
{
    // A REAL recorded run: the exact .conductor/events.jsonl produced by the in-tree orchestrator
    // driving the fake agent through two --once sessions (the B2.2 QA reproduction). Its matching
    // state.json is CapturedRunState below.
    private const string CapturedRunEvents = """
    {"type":"runStarted","plan":"BatonSmoke","repo":"C:/tmp/b22","branch":"master","driverVersion":"1.0.0.0","resumed":false,"seq":1,"ts":"2026-07-08T06:53:00.6131722+00:00","runId":"4cf56690c04b46d988dd76d1768e6031"}
    {"type":"stageEntered","stageId":"S1","title":"Smoke","startHead":"06d28c7cce356f4c3538aec7437b40e5d3541e84","seq":2,"ts":"2026-07-08T06:53:00.8255178+00:00","runId":"4cf56690c04b46d988dd76d1768e6031"}
    {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":6,"agentSessionId":"228b8479-8f36-48b3-87e3-8926838370ef","seq":3,"ts":"2026-07-08T06:53:01.330662+00:00","runId":"4cf56690c04b46d988dd76d1768e6031","sessionId":"1"}
    {"type":"gateFinished","name":"build","passed":true,"skipped":false,"optional":false,"exitCode":0,"durationMs":886,"scope":"session","seq":4,"ts":"2026-07-08T06:53:05.6627602+00:00","runId":"4cf56690c04b46d988dd76d1768e6031","sessionId":"1"}
    {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"Advanced","newCommits":["f7c2bcf feat(fake): checkpoint delivered by fake agent"],"newlyDone":["S1.1"],"costUsd":0.0003,"tokensInput":230,"tokensOutput":140,"tokensReasoning":0,"tokensCacheRead":0,"seq":5,"ts":"2026-07-08T06:53:06.165565+00:00","runId":"4cf56690c04b46d988dd76d1768e6031","sessionId":"1"}
    {"type":"checkpointConfirmed","checkpointId":"S1.1","stageId":"S1","seq":6,"ts":"2026-07-08T06:53:06.1659288+00:00","runId":"4cf56690c04b46d988dd76d1768e6031","sessionId":"1"}
    {"type":"runStarted","plan":"BatonSmoke","repo":"C:/tmp/b22","branch":"master","driverVersion":"1.0.0.0","resumed":true,"seq":7,"ts":"2026-07-08T06:53:06.866613+00:00","runId":"4cf56690c04b46d988dd76d1768e6031"}
    {"type":"sessionStarted","number":2,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":6,"agentSessionId":"8f0d2afc-b861-47a6-a24f-592a9194b9e9","seq":8,"ts":"2026-07-08T06:53:07.5143043+00:00","runId":"4cf56690c04b46d988dd76d1768e6031","sessionId":"2"}
    {"type":"gateFinished","name":"build","passed":true,"skipped":false,"optional":false,"exitCode":0,"durationMs":996,"scope":"session","seq":9,"ts":"2026-07-08T06:53:10.7025111+00:00","runId":"4cf56690c04b46d988dd76d1768e6031","sessionId":"2"}
    {"type":"sessionFinished","number":2,"stageId":"S1","outcome":"Advanced","newCommits":["a6cbb4e feat(fake): checkpoint delivered by fake agent"],"newlyDone":["S1.2"],"costUsd":0.0003,"tokensInput":230,"tokensOutput":140,"tokensReasoning":0,"tokensCacheRead":0,"seq":10,"ts":"2026-07-08T06:53:11.2384477+00:00","runId":"4cf56690c04b46d988dd76d1768e6031","sessionId":"2"}
    {"type":"checkpointConfirmed","checkpointId":"S1.2","stageId":"S1","seq":11,"ts":"2026-07-08T06:53:11.238986+00:00","runId":"4cf56690c04b46d988dd76d1768e6031","sessionId":"2"}
    """;

    private const string CapturedRunState = """
    {
      "planName": "BatonSmoke",
      "runId": "4cf56690c04b46d988dd76d1768e6031",
      "status": "idle",
      "currentStage": "S1",
      "currentStageStartHead": "06d28c7cce356f4c3538aec7437b40e5d3541e84",
      "sessionCounter": 2,
      "attemptsThisStage": 0,
      "consecutiveBackoffs": 0,
      "pendingPhaseGate": { "stageId": "S1", "stageStartHead": "06d28c7cce356f4c3538aec7437b40e5d3541e84" },
      "confirmedStages": [],
      "auditedStages": [],
      "history": [
        {
          "number": 1, "stage": "S1", "kind": "deliver",
          "startedUtc": "2026-07-08T06:53:00.9502744Z", "endedUtc": "2026-07-08T06:53:04.7519379Z",
          "outcome": "advanced", "claudeSessionId": "228b8479-8f36-48b3-87e3-8926838370ef",
          "resumeCount": 0, "newCommits": ["f7c2bcf feat(fake): checkpoint delivered by fake agent"],
          "newlyDone": ["S1.1"], "gateSummary": "build:OK", "costUsd": 0.0003, "numTurns": 2,
          "tokensInput": 230, "tokensOutput": 140, "tokensReasoning": 0, "tokensCacheRead": 0,
          "attempt": 1, "resultSummary": "SESSION-RESULT: delivered, gates green."
        },
        {
          "number": 2, "stage": "S1", "kind": "deliver",
          "startedUtc": "2026-07-08T06:53:07.0668892Z", "endedUtc": "2026-07-08T06:53:09.6836332Z",
          "outcome": "advanced", "claudeSessionId": "8f0d2afc-b861-47a6-a24f-592a9194b9e9",
          "resumeCount": 0, "newCommits": ["a6cbb4e feat(fake): checkpoint delivered by fake agent"],
          "newlyDone": ["S1.2"], "gateSummary": "build:OK", "costUsd": 0.0003, "numTurns": 2,
          "tokensInput": 230, "tokensOutput": 140, "tokensReasoning": 0, "tokensCacheRead": 0,
          "attempt": 1, "resultSummary": "SESSION-RESULT: delivered, gates green."
        }
      ],
      "updatedUtc": "2026-07-08T06:53:10.9857912Z"
    }
    """;

    // A Loom-shaped stream: an audited+confirmed L0 (deliver + audit) then a still-running L1 deliver.
    // Exercises StageConfirmed(audited) and multi-record token metrics the smoke run doesn't reach.
    private const string LoomEvents = """
    {"type":"runStarted","plan":"Loom","repo":"C:/repo","branch":"feat/loom-l1","resumed":false,"seq":1,"ts":"2026-07-07T10:00:00.0000000+00:00","runId":"loomrun00000000000000000000000001"}
    {"type":"stageEntered","stageId":"L0","title":"Foundations","startHead":"aaa1111","seq":2,"ts":"2026-07-07T10:00:01.0000000+00:00","runId":"loomrun00000000000000000000000001"}
    {"type":"sessionStarted","number":1,"stageId":"L0","kind":"Deliver","attempt":1,"maxAttempts":4,"agentSessionId":"sess-1","seq":3,"ts":"2026-07-07T10:00:02.0000000+00:00","runId":"loomrun00000000000000000000000001","sessionId":"1"}
    {"type":"sessionFinished","number":1,"stageId":"L0","outcome":"Advanced","newCommits":["c1 feat(l0.1)"],"newlyDone":["L0.1"],"costUsd":0.01,"tokensInput":100,"tokensOutput":50,"tokensReasoning":5,"tokensCacheRead":1000,"seq":4,"ts":"2026-07-07T10:30:00.0000000+00:00","runId":"loomrun00000000000000000000000001","sessionId":"1"}
    {"type":"checkpointConfirmed","checkpointId":"L0.1","stageId":"L0","seq":5,"ts":"2026-07-07T10:30:01.0000000+00:00","runId":"loomrun00000000000000000000000001","sessionId":"1"}
    {"type":"sessionStarted","number":2,"stageId":"L0","kind":"Audit","attempt":1,"maxAttempts":4,"agentSessionId":"sess-2","seq":6,"ts":"2026-07-07T10:35:00.0000000+00:00","runId":"loomrun00000000000000000000000001","sessionId":"2"}
    {"type":"sessionFinished","number":2,"stageId":"L0","outcome":"Progress","newCommits":["c2 fix(l0)"],"newlyDone":[],"costUsd":0.02,"tokensInput":200,"tokensOutput":80,"tokensReasoning":10,"tokensCacheRead":2000,"seq":7,"ts":"2026-07-07T11:05:00.0000000+00:00","runId":"loomrun00000000000000000000000001","sessionId":"2"}
    {"type":"stageConfirmed","stageId":"L0","audited":true,"seq":8,"ts":"2026-07-07T11:05:30.0000000+00:00","runId":"loomrun00000000000000000000000001"}
    {"type":"stageEntered","stageId":"L1","title":"Weave","startHead":"bbb2222","seq":9,"ts":"2026-07-07T11:06:00.0000000+00:00","runId":"loomrun00000000000000000000000001"}
    {"type":"sessionStarted","number":3,"stageId":"L1","kind":"Deliver","attempt":1,"maxAttempts":4,"agentSessionId":"sess-3","seq":10,"ts":"2026-07-07T11:06:01.0000000+00:00","runId":"loomrun00000000000000000000000001","sessionId":"3"}
    """;

    private const string LoomState = """
    {
      "planName": "Loom",
      "runId": "loomrun00000000000000000000000001",
      "status": "running",
      "currentStage": "L1",
      "currentStageStartHead": "bbb2222",
      "sessionCounter": 3,
      "attemptsThisStage": 0,
      "consecutiveBackoffs": 0,
      "confirmedStages": ["L0"],
      "auditedStages": ["L0"],
      "history": [
        {
          "number": 1, "stage": "L0", "kind": "deliver",
          "startedUtc": "2026-07-07T10:00:02.1Z", "endedUtc": "2026-07-07T10:30:00.1Z",
          "outcome": "advanced", "claudeSessionId": "sess-1", "resumeCount": 0,
          "newCommits": ["c1 feat(l0.1)"], "newlyDone": ["L0.1"], "gateSummary": "build:OK;tests:OK",
          "costUsd": 0.01, "numTurns": 40, "tokensInput": 100, "tokensOutput": 50,
          "tokensReasoning": 5, "tokensCacheRead": 1000, "attempt": 1, "resultSummary": "L0.1 done"
        },
        {
          "number": 2, "stage": "L0", "kind": "audit",
          "startedUtc": "2026-07-07T10:35:00.1Z", "endedUtc": "2026-07-07T11:05:00.1Z",
          "outcome": "progress", "claudeSessionId": "sess-2", "resumeCount": 0,
          "newCommits": ["c2 fix(l0)"], "newlyDone": [], "gateSummary": "",
          "costUsd": 0.02, "numTurns": 56, "tokensInput": 200, "tokensOutput": 80,
          "tokensReasoning": 10, "tokensCacheRead": 2000, "attempt": 1, "resultSummary": "audit pass"
        },
        {
          "number": 3, "stage": "L1", "kind": "deliver",
          "startedUtc": "2026-07-07T11:06:01.1Z", "claudeSessionId": "sess-3",
          "resumeCount": 0, "newCommits": [], "newlyDone": [], "attempt": 1
        }
      ],
      "updatedUtc": "2026-07-07T11:06:01.2Z"
    }
    """;

    [Theory]
    [InlineData(CapturedRunEvents, CapturedRunState)]
    [InlineData(LoomEvents, LoomState)]
    public void FoldedProjectionMatchesLegacyStateJson(string ndjson, string stateJson)
    {
        var (eventsPath, statePath) = WriteFixture(ndjson, stateJson);
        try
        {
            var events = EventLog.ReadAll(eventsPath);       // real on-disk read (crash-safe fold path)
            var legacy = RunState.LoadOrNew(statePath, "?");  // real deserialization

            var projected = RunStateProjection.Fold(events);

            var diff = StateProjectionParity.Diff(projected, legacy);
            Assert.True(diff.Count == 0, "projection diverged from state.json:\n  " + string.Join("\n  ", diff));
        }
        finally
        {
            File.Delete(eventsPath);
            File.Delete(statePath);
        }
    }

    [Fact]
    public void ParityContractCatchesADivergentProjection()
    {
        // Guard the guard: a real difference on the event-owned surface must be reported (a green
        // parity test is only meaningful if Diff can go red).
        var legacy = RunState.LoadOrNew(WriteText(LoomState), "?");
        var projected = RunStateProjection.Fold(EventLog.ReadAll(WriteText(LoomEvents, ".jsonl")));
        Assert.Empty(StateProjectionParity.Diff(projected, legacy));

        projected.SessionCounter += 1;                 // tamper with one owned field
        projected.History[0].NewlyDone = ["L0.1", "L0.2"];

        var diff = StateProjectionParity.Diff(projected, legacy);
        Assert.Contains(diff, d => d.StartsWith("sessionCounter:", StringComparison.Ordinal));
        Assert.Contains(diff, d => d.StartsWith("history[0].newlyDone:", StringComparison.Ordinal));
    }

    [Fact]
    public void FoldIsDeterministicRegardlessOfInputOrder()
    {
        var events = EventLog.ReadAll(WriteText(LoomEvents, ".jsonl"));
        var forward = RunStateProjection.Fold(events);
        var shuffled = RunStateProjection.Fold(events.Reverse().ToList()); // Fold orders by Seq itself
        Assert.Empty(StateProjectionParity.Diff(forward, shuffled));
    }

    private static (string eventsPath, string statePath) WriteFixture(string ndjson, string stateJson)
        => (WriteText(ndjson, ".jsonl"), WriteText(stateJson));

    private static string WriteText(string content, string ext = ".json")
    {
        // Raw string literals strip the delimiter's common indentation, so each NDJSON fixture line
        // reaches disk as clean, column-0 JSON that EventLog.ReadAll can parse line by line.
        var path = Path.Combine(Path.GetTempPath(), $"conductor-proj-{Guid.NewGuid():N}{ext}");
        File.WriteAllText(path, content);
        return path;
    }
}
