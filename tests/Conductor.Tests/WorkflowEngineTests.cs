using Conductor.Core.Lanes;
using Conductor.Core.Orchestration;
using Conductor.Models;

namespace Conductor.Tests;

public sealed class WorkflowEngineTests
{
    private readonly WorkflowEngine _engine = new();

    [Fact]
    public void Resolve_ReturnsBuiltIn_ForKnownName()
    {
        var plan = new PlanConfig();
        var stage = new StageConfig { Id = "test" };
        var wf = _engine.Resolve(plan, stage);
        Assert.Equal("deliver-verify", wf.Name);
        Assert.Equal(3, wf.Steps.Count);
        Assert.True(wf.Repeat);
    }

    [Fact]
    public void Resolve_ReturnsStageOverride_WhenSet()
    {
        var plan = new PlanConfig();
        var stage = new StageConfig { Id = "test", Workflow = "spike" };
        var wf = _engine.Resolve(plan, stage);
        Assert.Equal("spike", wf.Name);
        Assert.Single(wf.Steps);
        Assert.False(wf.Repeat);
    }

    [Fact]
    public void Resolve_ReturnsPlanDefault_WhenSet()
    {
        var plan = new PlanConfig { DefaultWorkflow = "docs-only" };
        var stage = new StageConfig { Id = "test" };
        var wf = _engine.Resolve(plan, stage);
        Assert.Equal("docs-only", wf.Name);
        Assert.False(wf.Repeat);
    }

    [Fact]
    public void DeliverVerify_StepsThroughCycle()
    {
        var wf = _engine.Resolve(new PlanConfig(), new StageConfig { Id = "test" });

        // Step -1 → step 0 (deliver)
        var step = _engine.GetNextStep(wf, -1, new WorkflowRuntimeVars());
        Assert.NotNull(step);
        Assert.Equal(SessionKind.Deliver, step.Kind);
        Assert.Equal("deliver", step.Id);

        // After deliver (green) → step 1 (verify)
        var afterDeliver = new WorkflowRuntimeVars { GatesGreen = true, HasCommits = true, NewlyDoneCount = 1 };
        step = _engine.GetNextStep(wf, 0, afterDeliver);
        Assert.NotNull(step);
        Assert.Equal(SessionKind.Verify, step.Kind);
        Assert.Equal("verify", step.Id);

        // After verify (passed) → step 2 (fix) skipped (RunIf "!verifier.passed" is false)
        // Then loop back to step 0 (deliver)
        var afterVerify = new WorkflowRuntimeVars { VerifierPassed = true, VerifierScore = 85 };
        step = _engine.GetNextStep(wf, 1, afterVerify);
        Assert.NotNull(step);
        Assert.Equal(SessionKind.Deliver, step.Kind); // loops back to deliver
    }

    [Fact]
    public void DeliverVerify_FailedVerify_QueuesFix()
    {
        var wf = _engine.Resolve(new PlanConfig(), new StageConfig { Id = "test" });

        // After verify FAILED → step 2 (fix) should run
        var afterFailedVerify = new WorkflowRuntimeVars { VerifierPassed = false, VerifierScore = 45 };
        var step = _engine.GetNextStep(wf, 1, afterFailedVerify);
        Assert.NotNull(step);
        Assert.Equal(SessionKind.Fix, step.Kind);
        Assert.Equal("fix-if-needed", step.Id);
    }

    [Fact]
    public void Spike_StopsAfterOneStep()
    {
        var wf = _engine.Resolve(new PlanConfig(), new StageConfig { Id = "test", Workflow = "spike" });

        var step = _engine.GetNextStep(wf, -1, new WorkflowRuntimeVars());
        Assert.NotNull(step);
        Assert.Equal(SessionKind.Deliver, step.Kind);

        var afterSpike = new WorkflowRuntimeVars { HasCommits = true };
        step = _engine.GetNextStep(wf, 0, afterSpike);
        Assert.Null(step); // Repeat=false → exhausted
    }

    [Fact]
    public void DocsOnly_SkipsVerification()
    {
        var plan = new PlanConfig { DefaultWorkflow = "docs-only" };
        var stage = new StageConfig { Id = "test" };
        var wf = _engine.Resolve(plan, stage);

        var step = _engine.GetNextStep(wf, -1, new WorkflowRuntimeVars());
        Assert.NotNull(step);
        Assert.Equal(SessionKind.Deliver, step.Kind);

        var afterDeliver = new WorkflowRuntimeVars { HasCommits = true };
        step = _engine.GetNextStep(wf, 0, afterDeliver);
        Assert.Null(step); // Repeat=false → no verify step
    }

    [Fact]
    public void EvaluateCondition_Negation_Works()
    {
        var vars = new WorkflowRuntimeVars { VerifierPassed = false };
        Assert.True(_engine.EvaluateCondition("!verifier.passed", vars));
        Assert.False(_engine.EvaluateCondition("verifier.passed", vars));
    }

    [Fact]
    public void EvaluateCondition_NumericComparison_Works()
    {
        var vars = new WorkflowRuntimeVars { VerifierScore = 85, NewlyDoneCount = 3 };
        Assert.True(_engine.EvaluateCondition("verifier.score >= 80", vars));
        Assert.False(_engine.EvaluateCondition("verifier.score < 80", vars));
        Assert.True(_engine.EvaluateCondition("newlyDoneCount > 0", vars));
        Assert.True(_engine.EvaluateCondition("newlyDoneCount == 3", vars));
    }

    [Fact]
    public void EvaluateCondition_BooleanVars_Work()
    {
        var vars = new WorkflowRuntimeVars { GatesGreen = true, Stalled = false, StageComplete = true };
        Assert.True(_engine.EvaluateCondition("gatesGreen", vars));
        Assert.False(_engine.EvaluateCondition("stalled", vars));
        Assert.True(_engine.EvaluateCondition("stageComplete", vars));
    }

    [Fact]
    public void ResolveAndRecordStep_KeepsIndexInSyncAcrossCallers()
    {
        // Regression test for a real bug (found M8): SessionRunner.ResolveSessionKind's
        // workflow-fallback branch used to call GetNextStep to pick a session's kind but never
        // wrote the resolved index back into WorkflowStepIndices — only VerdictEngine.AdvanceWorkflowStep
        // did that, and only for the step IT resolved. Because SessionRunner's own resolution
        // (used for a stage's first session) never recorded anything, AdvanceWorkflowStep's very
        // next read was stale (-1, not 0), so it re-derived "deliver" as "next" instead of
        // "verify" — the workflow step index permanently lagged one step behind, and
        // PendingVerify/PendingAudit/PendingFix were never populated for the step SessionRunner
        // itself later resolved by coincidence of the same lag, crashing PromptBuilder.Verify
        // (and Audit/Fix) with a NullReferenceException. ResolveAndRecordStep is the single call
        // both sites now share — this test drives it exactly the way SessionRunner (session 1's
        // kind) then VerdictEngine (advancing after session 1) do, and asserts the second call
        // sees the first call's step, not a stale index.
        var wf = _engine.Resolve(new PlanConfig(), new StageConfig { Id = "test" });
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);

        // SessionRunner.ResolveSessionKind's fallback: stage's first session, no prior state.
        var session1Step = _engine.ResolveAndRecordStep(wf, indices, "test", new WorkflowRuntimeVars());
        Assert.NotNull(session1Step);
        Assert.Equal(SessionKind.Deliver, session1Step.Kind);
        Assert.Equal(0, indices["test"]);

        // VerdictEngine.AdvanceWorkflowStep after session 1 completes green.
        var afterDeliver = new WorkflowRuntimeVars { GatesGreen = true, HasCommits = true };
        var next = _engine.ResolveAndRecordStep(wf, indices, "test", afterDeliver);
        Assert.NotNull(next);
        Assert.Equal(SessionKind.Verify, next.Kind); // NOT deliver again — the bug's symptom
        Assert.Equal(1, indices["test"]);
    }

    [Fact]
    public void ResolveAndRecordStep_RemovesEntry_WhenWorkflowExhausted()
    {
        var wf = _engine.Resolve(new PlanConfig(), new StageConfig { Id = "test", Workflow = "spike" });
        var indices = new Dictionary<string, int>(StringComparer.Ordinal) { ["test"] = 0 };

        var next = _engine.ResolveAndRecordStep(wf, indices, "test", new WorkflowRuntimeVars { HasCommits = true });

        Assert.Null(next);
        Assert.False(indices.ContainsKey("test"));
    }

    [Fact]
    public void BuildRuntimeVars_CapturesSessionState()
    {
        var rec = new SessionRecord
        {
            Number = 1,
            Outcome = SessionOutcome.Advanced,
            NewCommits = ["abc1234"],
            NewlyDone = ["CP1", "CP2"],
        };
        // P0: the adapter is engine-side now (WorkflowVarsFactory) — the library only sees the POCO.
        var vars = WorkflowVarsFactory.Build(rec, stageAttempts: 2, gatesGreen: true,
            verifierScore: 92, verifierPassed: true, circuitBroken: false, stageComplete: false);

        Assert.Equal(92, vars.VerifierScore);
        Assert.True(vars.VerifierPassed);
        Assert.False(vars.CircuitBroken);
        Assert.Equal(2, vars.StageAttempts);
        Assert.True(vars.GatesGreen);
        Assert.True(vars.HasCommits);
        Assert.Equal(2, vars.NewlyDoneCount);
        Assert.False(vars.StageComplete);
    }
}

public sealed class PathClaimTrackerTests
{
    [Fact]
    public void TryClaim_Succeeds_WithNoConflicts()
    {
        var tracker = new PathClaimTracker();
        Assert.True(tracker.TryClaim("S1", ["src/foo.cs", "src/bar.cs"]));
        Assert.Equal(2, tracker.Count);
    }

    [Fact]
    public void TryClaim_Fails_WithOverlappingPath()
    {
        var tracker = new PathClaimTracker();
        Assert.True(tracker.TryClaim("S1", ["src/foo.cs"]));
        Assert.False(tracker.TryClaim("S2", ["src/foo.cs", "src/baz.cs"]));
    }

    [Fact]
    public void TryClaim_Succeeds_AfterRelease()
    {
        var tracker = new PathClaimTracker();
        Assert.True(tracker.TryClaim("S1", ["src/foo.cs"]));
        tracker.Release("S1");
        Assert.True(tracker.TryClaim("S2", ["src/foo.cs"]));
    }

    [Fact]
    public void HasConflict_DetectsConflicts()
    {
        var tracker = new PathClaimTracker();
        tracker.TryClaim("S1", ["src/foo.cs"]);
        Assert.True(tracker.HasConflict(["src/foo.cs"]));
        Assert.False(tracker.HasConflict(["src/bar.cs"]));
    }

    [Fact]
    public void PathNormalization_HandlesSlashes()
    {
        var tracker = new PathClaimTracker();
        Assert.True(tracker.TryClaim("S1", ["src\\foo/bar.cs"]));
        Assert.True(tracker.HasConflict(["src/foo/bar.cs"]));
        Assert.True(tracker.HasConflict(["src\\foo\\bar.cs"]));
    }
}
