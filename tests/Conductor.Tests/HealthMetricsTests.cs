using Conductor.Core.Events;

namespace Conductor.Tests;

/// <summary>
/// B5.3 — the <see cref="HealthMetrics"/> projection folds the event log into execution-health signals.
/// The gate: synthetic streams (a same-failure loop, a flapping gate, a bloated context) produce the
/// expected flags, while a normal fail→fix cycle produces NONE — a false "looping" alarm erodes trust
/// (B5 trap), so the conservative thresholds are asserted directly rather than assumed. Pure fold over
/// the single event log; deterministic regardless of input order.
/// </summary>
public class HealthMetricsTests
{
    private static IReadOnlyList<ConductorEvent> Parse(string ndjson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-health-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, ndjson);
        try { return EventLog.ReadAll(path); }
        finally { File.Delete(path); }
    }

    // A healthy two-session run: both Advanced, no retries, gates green.
    private const string HealthyRun = """
    {"type":"runStarted","plan":"P","repo":"C:/r","resumed":false,"seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r"}
    {"type":"stageEntered","stageId":"S1","title":"Smoke","seq":2,"ts":"2026-07-08T10:00:05Z","runId":"r"}
    {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":3,"ts":"2026-07-08T10:00:10Z","runId":"r","sessionId":"1"}
    {"type":"gateFinished","name":"build","passed":true,"skipped":false,"optional":false,"exitCode":0,"durationMs":500,"scope":"session","seq":4,"ts":"2026-07-08T10:00:40Z","runId":"r","sessionId":"1"}
    {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"Advanced","newlyDone":["S1.1"],"seq":5,"ts":"2026-07-08T10:01:20Z","runId":"r","sessionId":"1"}
    {"type":"sessionStarted","number":2,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":6,"ts":"2026-07-08T10:02:00Z","runId":"r","sessionId":"2"}
    {"type":"gateFinished","name":"tests","passed":true,"skipped":false,"optional":false,"exitCode":0,"durationMs":700,"scope":"phase","seq":7,"ts":"2026-07-08T10:02:30Z","runId":"r","sessionId":"2"}
    {"type":"sessionFinished","number":2,"stageId":"S1","outcome":"Advanced","newlyDone":["S1.2"],"seq":8,"ts":"2026-07-08T10:02:50Z","runId":"r","sessionId":"2"}
    """;

    [Fact]
    public void HealthyRun_NoFlags_RetryRateZero()
    {
        var r = HealthMetrics.Compute(Parse(HealthyRun));
        Assert.Equal(2, r.Sessions);
        Assert.Equal(0, r.Retries);
        Assert.Equal(0d, r.RetryRate);
        Assert.Empty(r.Flags);
        Assert.Equal(HealthMetrics.Severity.Ok, r.Worst);
    }

    [Fact]
    public void EmptyLog_IsHealthy()
    {
        var r = HealthMetrics.Compute(Array.Empty<ConductorEvent>());
        Assert.Equal(0, r.Sessions);
        Assert.Empty(r.Flags);
        Assert.Equal(HealthMetrics.Severity.Ok, r.Worst);
    }

    [Fact]
    public void SameFailureLoop_FlaggedAsAlert()
    {
        // Three consecutive unproductive (GatesRed) sessions on S1 — the classic "stuck stage" loop.
        const string loop = """
        {"type":"runStarted","plan":"P","repo":"C:/r","resumed":false,"seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r"}
        {"type":"stageEntered","stageId":"S1","seq":2,"ts":"2026-07-08T10:00:05Z","runId":"r"}
        {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":3,"ts":"2026-07-08T10:00:10Z","runId":"r","sessionId":"1"}
        {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"GatesRed","seq":4,"ts":"2026-07-08T10:01:00Z","runId":"r","sessionId":"1"}
        {"type":"sessionStarted","number":2,"stageId":"S1","kind":"Fix","attempt":2,"maxAttempts":4,"seq":5,"ts":"2026-07-08T10:02:00Z","runId":"r","sessionId":"2"}
        {"type":"sessionFinished","number":2,"stageId":"S1","outcome":"GatesRed","seq":6,"ts":"2026-07-08T10:03:00Z","runId":"r","sessionId":"2"}
        {"type":"sessionStarted","number":3,"stageId":"S1","kind":"Fix","attempt":3,"maxAttempts":4,"seq":7,"ts":"2026-07-08T10:04:00Z","runId":"r","sessionId":"3"}
        {"type":"sessionFinished","number":3,"stageId":"S1","outcome":"GatesRed","seq":8,"ts":"2026-07-08T10:05:00Z","runId":"r","sessionId":"3"}
        """;
        var r = HealthMetrics.Compute(Parse(loop));

        var flag = Assert.Single(r.Flags, f => f.Code == "same-failure-loop");
        Assert.Equal(HealthMetrics.Severity.Alert, flag.Severity);
        Assert.Contains("S1", flag.Detail, StringComparison.Ordinal);
        Assert.Equal(HealthMetrics.Severity.Alert, r.Worst);
    }

    [Fact]
    public void GateOscillation_FlaggedAsWarn_WithoutFalseLoop()
    {
        // A flaky "tests" gate flaps pass/fail/pass/fail across four green phase runs (3 flips). The
        // sessions all Advanced, so this must flag oscillation WITHOUT a same-failure loop.
        const string flapping = """
        {"type":"runStarted","plan":"P","repo":"C:/r","resumed":false,"seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r"}
        {"type":"stageEntered","stageId":"S1","seq":2,"ts":"2026-07-08T10:00:05Z","runId":"r"}
        {"type":"gateFinished","name":"tests","passed":true,"skipped":false,"optional":false,"exitCode":0,"durationMs":100,"scope":"phase","seq":3,"ts":"2026-07-08T10:00:10Z","runId":"r"}
        {"type":"gateFinished","name":"tests","passed":false,"skipped":false,"optional":false,"exitCode":1,"durationMs":100,"scope":"phase","seq":4,"ts":"2026-07-08T10:00:20Z","runId":"r"}
        {"type":"gateFinished","name":"tests","passed":true,"skipped":false,"optional":false,"exitCode":0,"durationMs":100,"scope":"phase","seq":5,"ts":"2026-07-08T10:00:30Z","runId":"r"}
        {"type":"gateFinished","name":"tests","passed":false,"skipped":false,"optional":false,"exitCode":1,"durationMs":100,"scope":"phase","seq":6,"ts":"2026-07-08T10:00:40Z","runId":"r"}
        """;
        var r = HealthMetrics.Compute(Parse(flapping));

        var flag = Assert.Single(r.Flags, f => f.Code == "gate-oscillation");
        Assert.Equal(HealthMetrics.Severity.Warn, flag.Severity);
        Assert.Contains("tests", flag.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(r.Flags, f => f.Code == "same-failure-loop");
    }

    [Fact]
    public void GateRepetition_FlaggedAsAlert_AcrossStagesWithoutStageLoop()
    {
        // The same gate fails three times in a row, but each failure is on a different stage — so a
        // single stage never loops, yet the "same command keeps failing" signal must still fire.
        const string repeat = """
        {"type":"runStarted","plan":"P","repo":"C:/r","resumed":false,"seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r"}
        {"type":"stageEntered","stageId":"S1","seq":2,"ts":"2026-07-08T10:00:05Z","runId":"r"}
        {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":3,"ts":"2026-07-08T10:00:10Z","runId":"r","sessionId":"1"}
        {"type":"gateFinished","name":"build","passed":false,"skipped":false,"optional":false,"exitCode":1,"durationMs":100,"scope":"session","seq":4,"ts":"2026-07-08T10:00:20Z","runId":"r","sessionId":"1"}
        {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"GatesRed","seq":5,"ts":"2026-07-08T10:00:30Z","runId":"r","sessionId":"1"}
        {"type":"stageEntered","stageId":"S2","seq":6,"ts":"2026-07-08T10:01:00Z","runId":"r"}
        {"type":"sessionStarted","number":2,"stageId":"S2","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":7,"ts":"2026-07-08T10:01:10Z","runId":"r","sessionId":"2"}
        {"type":"gateFinished","name":"build","passed":false,"skipped":false,"optional":false,"exitCode":1,"durationMs":100,"scope":"session","seq":8,"ts":"2026-07-08T10:01:20Z","runId":"r","sessionId":"2"}
        {"type":"sessionFinished","number":2,"stageId":"S2","outcome":"GatesRed","seq":9,"ts":"2026-07-08T10:01:30Z","runId":"r","sessionId":"2"}
        {"type":"stageEntered","stageId":"S3","seq":10,"ts":"2026-07-08T10:02:00Z","runId":"r"}
        {"type":"sessionStarted","number":3,"stageId":"S3","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":11,"ts":"2026-07-08T10:02:10Z","runId":"r","sessionId":"3"}
        {"type":"gateFinished","name":"build","passed":false,"skipped":false,"optional":false,"exitCode":1,"durationMs":100,"scope":"session","seq":12,"ts":"2026-07-08T10:02:20Z","runId":"r","sessionId":"3"}
        {"type":"sessionFinished","number":3,"stageId":"S3","outcome":"GatesRed","seq":13,"ts":"2026-07-08T10:02:30Z","runId":"r","sessionId":"3"}
        """;
        var r = HealthMetrics.Compute(Parse(repeat));

        var flag = Assert.Single(r.Flags, f => f.Code == "gate-repetition");
        Assert.Equal(HealthMetrics.Severity.Alert, flag.Severity);
        Assert.Contains("build", flag.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(r.Flags, f => f.Code == "same-failure-loop"); // no single stage looped
    }

    [Fact]
    public void ContextSaturation_FlaggedAsWarn_NamingTheSession()
    {
        // A session whose cache-read context blew past the threshold (the F-8 bloated-context signal).
        const string bloated = """
        {"type":"runStarted","plan":"P","repo":"C:/r","resumed":false,"seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r"}
        {"type":"stageEntered","stageId":"S1","seq":2,"ts":"2026-07-08T10:00:05Z","runId":"r"}
        {"type":"sessionStarted","number":9,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":3,"ts":"2026-07-08T10:00:10Z","runId":"r","sessionId":"9"}
        {"type":"sessionFinished","number":9,"stageId":"S1","outcome":"Advanced","newlyDone":["S1.1"],"tokensInput":50000,"tokensCacheRead":28500000,"seq":4,"ts":"2026-07-08T11:00:00Z","runId":"r","sessionId":"9"}
        """;
        var r = HealthMetrics.Compute(Parse(bloated));

        var flag = Assert.Single(r.Flags, f => f.Code == "context-saturation");
        Assert.Equal(HealthMetrics.Severity.Warn, flag.Severity);
        Assert.Contains("#9", flag.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalFixCycle_ProducesNoFlags_TrustInvariant()
    {
        // The trust invariant: recovering from one red gate (fail → fix → pass) must NOT trip loop,
        // repetition, or oscillation. If this ever flags, the thresholds have become untrustworthy.
        const string fixCycle = """
        {"type":"runStarted","plan":"P","repo":"C:/r","resumed":false,"seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r"}
        {"type":"stageEntered","stageId":"S1","seq":2,"ts":"2026-07-08T10:00:05Z","runId":"r"}
        {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":3,"ts":"2026-07-08T10:00:10Z","runId":"r","sessionId":"1"}
        {"type":"gateFinished","name":"build","passed":false,"skipped":false,"optional":false,"exitCode":1,"durationMs":100,"scope":"session","seq":4,"ts":"2026-07-08T10:00:20Z","runId":"r","sessionId":"1"}
        {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"GatesRed","seq":5,"ts":"2026-07-08T10:00:30Z","runId":"r","sessionId":"1"}
        {"type":"sessionStarted","number":2,"stageId":"S1","kind":"Fix","attempt":2,"maxAttempts":4,"seq":6,"ts":"2026-07-08T10:01:00Z","runId":"r","sessionId":"2"}
        {"type":"gateFinished","name":"build","passed":true,"skipped":false,"optional":false,"exitCode":0,"durationMs":100,"scope":"session","seq":7,"ts":"2026-07-08T10:01:20Z","runId":"r","sessionId":"2"}
        {"type":"sessionFinished","number":2,"stageId":"S1","outcome":"Advanced","newlyDone":["S1.1"],"seq":8,"ts":"2026-07-08T10:01:30Z","runId":"r","sessionId":"2"}
        """;
        var r = HealthMetrics.Compute(Parse(fixCycle));

        Assert.Empty(r.Flags);
        Assert.Equal(HealthMetrics.Severity.Ok, r.Worst);
        Assert.Equal(1, r.Retries);   // the fix session is a retry — counted, but a single retry never alarms
    }

    [Fact]
    public void HighRetryRate_FlaggedOnlyOncePastTheSampleFloor()
    {
        // Below the sample floor a high rate stays quiet (avoids a tiny-sample false alarm); with enough
        // sessions the same rate is flagged. Uses a custom floor so both cases fit small fixtures.
        var t = HealthMetrics.Thresholds.Default with { MinSessionsForRetryFlag = 4, HighRetryRate = 0.5 };

        // 3 sessions, 2 retries (rate .667) — over the rate, under the floor → no flag.
        const string small = """
        {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r","sessionId":"1"}
        {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"Advanced","newlyDone":["S1.1"],"seq":2,"ts":"2026-07-08T10:00:10Z","runId":"r","sessionId":"1"}
        {"type":"sessionStarted","number":2,"stageId":"S1","kind":"Fix","attempt":2,"maxAttempts":4,"seq":3,"ts":"2026-07-08T10:00:20Z","runId":"r","sessionId":"2"}
        {"type":"sessionFinished","number":2,"stageId":"S1","outcome":"Progress","seq":4,"ts":"2026-07-08T10:00:30Z","runId":"r","sessionId":"2"}
        {"type":"sessionStarted","number":3,"stageId":"S2","kind":"Deliver","attempt":2,"maxAttempts":4,"seq":5,"ts":"2026-07-08T10:00:40Z","runId":"r","sessionId":"3"}
        {"type":"sessionFinished","number":3,"stageId":"S2","outcome":"Advanced","newlyDone":["S2.1"],"seq":6,"ts":"2026-07-08T10:00:50Z","runId":"r","sessionId":"3"}
        """;
        var rSmall = HealthMetrics.Compute(Parse(small), t);
        Assert.DoesNotContain(rSmall.Flags, f => f.Code == "high-retry-rate");
        Assert.Equal(2, rSmall.Retries);

        // 4 sessions, 3 retries (rate .75) — over both → flagged.
        const string big = """
        {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":2,"maxAttempts":4,"seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r","sessionId":"1"}
        {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"Advanced","newlyDone":["S1.1"],"seq":2,"ts":"2026-07-08T10:00:10Z","runId":"r","sessionId":"1"}
        {"type":"sessionStarted","number":2,"stageId":"S2","kind":"Deliver","attempt":2,"maxAttempts":4,"seq":3,"ts":"2026-07-08T10:00:20Z","runId":"r","sessionId":"2"}
        {"type":"sessionFinished","number":2,"stageId":"S2","outcome":"Advanced","newlyDone":["S2.1"],"seq":4,"ts":"2026-07-08T10:00:30Z","runId":"r","sessionId":"2"}
        {"type":"sessionStarted","number":3,"stageId":"S3","kind":"Deliver","attempt":2,"maxAttempts":4,"seq":5,"ts":"2026-07-08T10:00:40Z","runId":"r","sessionId":"3"}
        {"type":"sessionFinished","number":3,"stageId":"S3","outcome":"Advanced","newlyDone":["S3.1"],"seq":6,"ts":"2026-07-08T10:00:50Z","runId":"r","sessionId":"3"}
        {"type":"sessionStarted","number":4,"stageId":"S4","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":7,"ts":"2026-07-08T10:01:00Z","runId":"r","sessionId":"4"}
        {"type":"sessionFinished","number":4,"stageId":"S4","outcome":"Advanced","newlyDone":["S4.1"],"seq":8,"ts":"2026-07-08T10:01:10Z","runId":"r","sessionId":"4"}
        """;
        var rBig = HealthMetrics.Compute(Parse(big), t);
        var flag = Assert.Single(rBig.Flags, f => f.Code == "high-retry-rate");
        Assert.Equal(HealthMetrics.Severity.Warn, flag.Severity);
        Assert.Equal(0.75, rBig.RetryRate, precision: 2);
    }

    [Fact]
    public void BackoffBetweenFailuresDoesNotInflateTheLoopStreak()
    {
        // A rate-limit backoff (neutral) between two red sessions must NOT count toward the loop streak —
        // otherwise an external stall would masquerade as the agent looping.
        const string withBackoff = """
        {"type":"stageEntered","stageId":"S1","seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r"}
        {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":2,"ts":"2026-07-08T10:00:10Z","runId":"r","sessionId":"1"}
        {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"GatesRed","seq":3,"ts":"2026-07-08T10:00:20Z","runId":"r","sessionId":"1"}
        {"type":"sessionStarted","number":2,"stageId":"S1","kind":"Fix","attempt":2,"maxAttempts":4,"seq":4,"ts":"2026-07-08T10:00:30Z","runId":"r","sessionId":"2"}
        {"type":"sessionFinished","number":2,"stageId":"S1","outcome":"LimitBackoff","seq":5,"ts":"2026-07-08T10:00:40Z","runId":"r","sessionId":"2"}
        {"type":"sessionStarted","number":3,"stageId":"S1","kind":"Fix","attempt":3,"maxAttempts":4,"seq":6,"ts":"2026-07-08T10:00:50Z","runId":"r","sessionId":"3"}
        {"type":"sessionFinished","number":3,"stageId":"S1","outcome":"GatesRed","seq":7,"ts":"2026-07-08T10:01:00Z","runId":"r","sessionId":"3"}
        """;
        var r = HealthMetrics.Compute(Parse(withBackoff));

        // Two reds with a neutral backoff between them = streak 2, under the loop threshold of 3.
        Assert.DoesNotContain(r.Flags, f => f.Code == "same-failure-loop");
    }

    [Fact]
    public void FoldIsDeterministicRegardlessOfInputOrder()
    {
        var events = Parse(HealthyRun);
        var forward = HealthMetrics.Format(HealthMetrics.Compute(events)).ToList();
        var reversed = HealthMetrics.Format(HealthMetrics.Compute(events.Reverse().ToList())).ToList();
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void Format_RendersHeadlineThenOneLinePerFlag()
    {
        const string loop = """
        {"type":"stageEntered","stageId":"S1","seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r"}
        {"type":"sessionStarted","number":1,"stageId":"S1","kind":"Deliver","attempt":1,"maxAttempts":4,"seq":2,"ts":"2026-07-08T10:00:10Z","runId":"r","sessionId":"1"}
        {"type":"sessionFinished","number":1,"stageId":"S1","outcome":"GatesRed","seq":3,"ts":"2026-07-08T10:00:20Z","runId":"r","sessionId":"1"}
        {"type":"sessionStarted","number":2,"stageId":"S1","kind":"Fix","attempt":2,"maxAttempts":4,"seq":4,"ts":"2026-07-08T10:00:30Z","runId":"r","sessionId":"2"}
        {"type":"sessionFinished","number":2,"stageId":"S1","outcome":"GatesRed","seq":5,"ts":"2026-07-08T10:00:40Z","runId":"r","sessionId":"2"}
        {"type":"sessionStarted","number":3,"stageId":"S1","kind":"Fix","attempt":3,"maxAttempts":4,"seq":6,"ts":"2026-07-08T10:00:50Z","runId":"r","sessionId":"3"}
        {"type":"sessionFinished","number":3,"stageId":"S1","outcome":"GatesRed","seq":7,"ts":"2026-07-08T10:01:00Z","runId":"r","sessionId":"3"}
        """;
        var r = HealthMetrics.Compute(Parse(loop));
        var lines = HealthMetrics.Format(r).ToList();

        Assert.Contains("sessions 3", lines[0], StringComparison.Ordinal);
        Assert.Contains("overall Alert", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("same-failure-loop", StringComparison.Ordinal));
    }
}
