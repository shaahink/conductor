using Conductor.Planning;

namespace Conductor.Tests;

/// <summary>P4: the two decisions that moved behind the seam — the post-session Advance (with the
/// skip-verification collapse the engine used to do by recursion) and the session-start kind
/// resolution (consume a recorded index without advancing). Behavior must be identical to the
/// engine's former inline logic; these tests pin the walk itself, purely.</summary>
public sealed class WorkflowAdvanceTests
{
    private readonly WorkflowEngine _engine = new();

    private WorkflowDefinition DeliverVerify() => _engine.Resolve("deliver-verify", null, null);

    [Fact]
    public void Advance_FromDeliver_LandsOnVerify_WhenVerificationRuns()
    {
        var indices = new Dictionary<string, int>(StringComparer.Ordinal) { ["S"] = 0 };
        var advance = _engine.Advance(DeliverVerify(), indices, "S", new WorkflowRuntimeVars(), skipVerification: false);

        Assert.NotNull(advance.Next);
        Assert.Equal(SessionKind.Verify, advance.Next!.Kind);
        var hop = Assert.Single(advance.Hops);
        Assert.False(hop.SkippedAsPassed);
        Assert.Equal(0, hop.FromIndex);
        Assert.Equal(1, hop.ToIndex);
        Assert.Equal(1, indices["S"]); // the index is recorded, single source of truth
    }

    [Fact]
    public void Advance_CollapsesSkippedVerify_AndReEvaluatesWithVerifierPassed()
    {
        // From the deliver step with verification skipped: the verify hop is consumed as
        // skipped-as-passed, and the re-evaluation (verifier.passed = true) must SKIP the
        // fix-if-needed step (RunIf !verifier.passed) and wrap to deliver — exactly the engine's
        // former recursion with verifierPassed: true.
        var indices = new Dictionary<string, int>(StringComparer.Ordinal) { ["S"] = 0 };
        var advance = _engine.Advance(DeliverVerify(), indices, "S",
            new WorkflowRuntimeVars { VerifierPassed = false }, skipVerification: true);

        Assert.NotNull(advance.Next);
        Assert.Equal(SessionKind.Deliver, advance.Next!.Kind);
        Assert.Equal(2, advance.Hops.Count);
        Assert.True(advance.Hops[0].SkippedAsPassed);
        Assert.Equal(SessionKind.Verify, advance.Hops[0].Step.Kind);
        Assert.False(advance.Hops[1].SkippedAsPassed);
        Assert.Equal(0, indices["S"]); // wrapped back to the deliver step
    }

    [Fact]
    public void Advance_ReportsExhaustion_WithTheIndexItRanOutAt()
    {
        var docsOnly = _engine.Resolve("docs-only", null, null); // one step, no repeat
        var indices = new Dictionary<string, int>(StringComparer.Ordinal) { ["S"] = 0 };
        var advance = _engine.Advance(docsOnly, indices, "S", new WorkflowRuntimeVars(), skipVerification: false);

        Assert.Null(advance.Next);
        Assert.Empty(advance.Hops);
        Assert.Equal(0, advance.ExhaustedFromIndex);
        Assert.False(indices.ContainsKey("S")); // exhaustion removes the entry, as before
    }

    [Fact]
    public void ResolveStartKind_ConsumesARecordedIndexWithoutAdvancing()
    {
        var indices = new Dictionary<string, int>(StringComparer.Ordinal) { ["S"] = 1 }; // verify recorded
        var kind = _engine.ResolveStartKind(DeliverVerify(), indices, "S", skipVerification: false);

        Assert.Equal(SessionKind.Verify, kind);
        Assert.Equal(1, indices["S"]); // untouched — consuming must never advance
    }

    [Fact]
    public void ResolveStartKind_FirstResolutionAdvancesFromMinusOne()
    {
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var kind = _engine.ResolveStartKind(DeliverVerify(), indices, "S", skipVerification: false);

        Assert.Equal(SessionKind.Deliver, kind);
        Assert.Equal(0, indices["S"]); // the very first resolution records step 0
    }

    [Fact]
    public void ResolveStartKind_DowngradesARecordedVerifyToDeliver_WhenVerificationIsSkipped()
    {
        var indices = new Dictionary<string, int>(StringComparer.Ordinal) { ["S"] = 1 };
        var kind = _engine.ResolveStartKind(DeliverVerify(), indices, "S", skipVerification: true);
        Assert.Equal(SessionKind.Deliver, kind);
    }

    [Fact]
    public void ResolveStartKind_DefaultsToDeliver_OnAnEmptyWorkflow()
    {
        var empty = new WorkflowDefinition { Name = "empty", Steps = [] };
        var kind = _engine.ResolveStartKind(empty, new Dictionary<string, int>(StringComparer.Ordinal), "S", skipVerification: false);
        Assert.Equal(SessionKind.Deliver, kind);
    }
}
