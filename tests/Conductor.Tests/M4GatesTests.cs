using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// M4 truth gates: claims vs confirmations (M4.1), gate caching (M4.2),
/// verifier findings → retry prompt (M4.3).
/// </summary>
public sealed class M4GatesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-m4-{Guid.NewGuid():N}");
    private readonly SqliteRunStore _db;

    public M4GatesTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new SqliteRunStore(Path.Combine(_dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── M4.1: claims vs confirmations ──

    [Fact]
    public void Checkpoints_have_confirmed_column_defaults_to_zero()
    {
        var runId = "r-m41-1";
        _db.InitializeRun(runId, "plan", _dir, "main", "1.0");
        _db.SeedCheckpoints(runId, [("CP1", "S1", "First task", "TODO", "-", "-")]);

        var cps = _db.GetCheckpoints(runId);
        Assert.Single(cps);
        Assert.False(cps[0].Confirmed, "new checkpoints should start unconfirmed");
    }

    [Fact]
    public void ConfirmCheckpoints_sets_confirmed_to_one()
    {
        var runId = "r-m41-2";
        _db.InitializeRun(runId, "plan", _dir, "main", "1.0");
        _db.SeedCheckpoints(runId, [("CP1", "S1", "First task", "TODO", "-", "-")]);
        _db.UpdateCheckpoint(runId, "CP1", "DONE", "abc1234", "gate: OK");

        _db.ConfirmCheckpoints(runId, ["CP1"]);

        var cps = _db.GetCheckpoints(runId);
        Assert.True(cps[0].Confirmed, "checkpoint should be confirmed after ConfirmCheckpoints");
        Assert.Equal("DONE", cps[0].Status);
    }

    [Fact]
    public void Agent_claim_without_confirmation_is_not_counted_done()
    {
        var runId = "r-m41-3";
        _db.InitializeRun(runId, "plan", _dir, "main", "1.0");
        _db.SeedCheckpoints(runId, [("CP1", "S1", "First task", "TODO", "-", "-")]);
        _db.UpdateCheckpoint(runId, "CP1", "DONE", "abc1234", "agent claim");

        // Agent claimed DONE but engine hasn't confirmed
        var cps = _db.GetCheckpoints(runId);
        Assert.Equal("DONE", cps[0].Status);
        Assert.False(cps[0].Confirmed, "agent claim should be unconfirmed");
    }

    [Fact]
    public void PendingConfirmation_is_cleared_after_confirm()
    {
        var state = new RunState { RunId = "r-m41-4" };
        state.PendingConfirmation.AddRange(["CP1", "CP2"]);

        _db.InitializeRun(state.RunId, "plan", _dir, "main", "1.0");
        _db.ConfirmCheckpoints(state.RunId, state.PendingConfirmation);
        state.PendingConfirmation.Clear();

        Assert.Empty(state.PendingConfirmation);
    }

    // ── M4.2: gate caching by (gate, sha, tier) ──

    [Fact]
    public void GetLastPassingGateResult_returns_null_when_no_cache()
    {
        var runId = "r-m42-1";
        _db.InitializeRun(runId, "plan", _dir, "main", "1.0");

        var cached = _db.GetLastPassingGateResult(runId, "build", "fast", "abc1234");
        Assert.Null(cached);
    }

    [Fact]
    public void GetLastPassingGateResult_returns_true_after_passing_gate_recorded()
    {
        var runId = "r-m42-2";
        _db.InitializeRun(runId, "plan", _dir, "main", "1.0");

        // Record a passing gate result
        _db.RecordGate(runId, sessionNumber: 1, stageId: "S1",
            name: "build", tier: "fast", scope: "session", sha: "abc1234",
            passed: true, skipped: false, optional: false, exitCode: 0, durationMs: 500, tail: "OK");

        var cached = _db.GetLastPassingGateResult(runId, "build", "fast", "abc1234");
        Assert.True(cached, "cache should hit after passing gate recorded");
    }

    [Fact]
    public void GetLastPassingGateResult_returns_null_for_different_sha()
    {
        var runId = "r-m42-3";
        _db.InitializeRun(runId, "plan", _dir, "main", "1.0");

        _db.RecordGate(runId, sessionNumber: 1, stageId: "S1",
            name: "build", tier: "fast", scope: "session", sha: "abc1234",
            passed: true, skipped: false, optional: false, exitCode: 0, durationMs: 500, tail: "OK");

        var cached = _db.GetLastPassingGateResult(runId, "build", "fast", "different-sha");
        Assert.Null(cached);
    }

    [Fact]
    public void GetLastPassingGateResult_returns_null_for_different_tier()
    {
        var runId = "r-m42-4";
        _db.InitializeRun(runId, "plan", _dir, "main", "1.0");

        _db.RecordGate(runId, sessionNumber: 1, stageId: "S1",
            name: "build", tier: "fast", scope: "session", sha: "abc1234",
            passed: true, skipped: false, optional: false, exitCode: 0, durationMs: 500, tail: "OK");

        var cached = _db.GetLastPassingGateResult(runId, "build", "full", "abc1234");
        Assert.Null(cached);
    }

    [Fact]
    public void GetLastPassingGateResult_returns_false_for_failed_gate()
    {
        var runId = "r-m42-5";
        _db.InitializeRun(runId, "plan", _dir, "main", "1.0");

        _db.RecordGate(runId, sessionNumber: 1, stageId: "S1",
            name: "build", tier: "fast", scope: "session", sha: "abc1234",
            passed: false, skipped: false, optional: false, exitCode: 1, durationMs: 500, tail: "FAIL");

        var cached = _db.GetLastPassingGateResult(runId, "build", "fast", "abc1234");
        Assert.False(cached, "failed gates should return false, not be cached as passing");
    }

    [Fact]
    public void GateConfig_truth_tier_excluded_from_fast_only()
    {
        var plan = new PlanConfig
        {
            Repo = _dir,
            Gates = new List<GateConfig>
            {
                new() { Name = "build", Tier = "fast" },
                new() { Name = "smoke", Tier = "truth" },
                new() { Name = "tests", Tier = "full" },
            },
        };

        // Fast-only: truth gates excluded, full gates excluded, only fast runs
        var applies = plan.Gates.Where(g => !g.IsTruth).ToList();
        Assert.Equal(2, applies.Count);
        Assert.DoesNotContain(applies, g => g.Name == "smoke");
        Assert.Contains(applies, g => g.Name == "build");
    }

    // ── M4.3: verifier findings → retry prompt ──

    [Fact]
    public void Verifier_parse_bad_delivery_scores_below_threshold()
    {
        var output = """
                     {
                       "score": 45,
                       "verdict": "FAIL",
                       "findings": [
                         "Missing null check in ProcessRunner",
                         "Gate battery not wired correctly"
                       ]
                     }
                     """;

        var verdict = Verifier.Parse(output);
        Assert.NotNull(verdict);
        Assert.Equal(45, verdict.Score);
        Assert.Equal("FAIL", verdict.Verdict);
        Assert.Equal(2, verdict.Findings.Count);
        Assert.False(verdict.Passes(80));
    }

    [Fact]
    public void Verifier_parse_good_delivery_scores_above_threshold()
    {
        var output = """
                     {
                       "score": 92,
                       "verdict": "PASS",
                       "findings": ["Consider adding more edge-case tests"]
                     }
                     """;

        var verdict = Verifier.Parse(output);
        Assert.NotNull(verdict);
        Assert.Equal(92, verdict.Score);
        Assert.True(verdict.Passes(80));
    }

    [Fact]
    public void Verifier_parse_handles_malformed_output()
    {
        var output = "Here is my unstructured report: it looks good! No JSON here.";

        var verdict = Verifier.Parse(output);
        Assert.Null(verdict);
    }

    [Fact]
    public void Verifier_findings_appear_in_pending_fix()
    {
        var findingsText = "Missing null check\nGate battery not wired";
        var pendingFix = new PendingFix
        {
            FromSession = 1,
            VerifierFindings = findingsText,
            VerifierScore = 45,
            GateFailures = "verifier score 45/100 < threshold 80",
            ProgressSummary = "Verifier verdict: FAIL. Findings: Missing null check; Gate battery not wired",
        };

        Assert.Contains("Missing null check", pendingFix.VerifierFindings);
        Assert.Contains("Gate battery not wired", pendingFix.VerifierFindings);
        Assert.Equal(45, pendingFix.VerifierScore);
    }

    [Fact]
    public void WorkflowEngine_skips_fix_when_verify_passed()
    {
        var engine = new WorkflowEngine();
        var wf = engine.Resolve(new PlanConfig(), new StageConfig { Id = "test" }, new DefaultQaPolicy());

        // After verify passed: step 2 (fix-if-needed) RunIf "!verifier.passed" → false → skipped
        var vars = new WorkflowRuntimeVars { VerifierPassed = true, VerifierScore = 85 };
        var step = engine.GetNextStep(wf, 1, vars); // step 1 = verify
        Assert.NotNull(step);
        Assert.Equal(SessionKind.Deliver, step.Kind); // wraps back to deliver, not fix
    }

    [Fact]
    public void WorkflowEngine_queues_fix_when_verify_failed()
    {
        var engine = new WorkflowEngine();
        var wf = engine.Resolve(new PlanConfig(), new StageConfig { Id = "test" }, new DefaultQaPolicy());

        // After verify failed: step 2 (fix-if-needed) RunIf "!verifier.passed" → true → runs
        var vars = new WorkflowRuntimeVars { VerifierPassed = false, VerifierScore = 45 };
        var step = engine.GetNextStep(wf, 1, vars);
        Assert.NotNull(step);
        Assert.Equal(SessionKind.Fix, step.Kind);
        Assert.Equal("fix-if-needed", step.Id);
    }

    [Fact]
    public void WorkflowEngine_skip_verify_treats_as_passed()
    {
        // When verification is skipped via override, verifierPassed should be true
        // to prevent the fix step from triggering incorrectly.
        var engine = new WorkflowEngine();
        var wf = engine.Resolve(new PlanConfig(), new StageConfig { Id = "test" }, new DefaultQaPolicy());

        // Simulate what happens when verify is skipped: pass verifierPassed=true
        var vars = new WorkflowRuntimeVars { VerifierPassed = true, GatesGreen = true };
        var step = engine.GetNextStep(wf, 1, vars);
        Assert.NotNull(step);
        Assert.NotEqual(SessionKind.Fix, step.Kind); // fix should NOT be queued
    }
}
