using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>The engine-side adapter from a finished session to the planning library's POCO facts
/// (P0: was WorkflowEngine.BuildRuntimeVars). The library only ever sees
/// <see cref="WorkflowRuntimeVars"/> — this is the one place engine records are translated, so the
/// planning assembly never needs a SessionRecord.</summary>
public static class WorkflowVarsFactory
{
    public static WorkflowRuntimeVars Build(
        SessionRecord rec,
        int stageAttempts,
        bool gatesGreen,
        int? verifierScore,
        bool verifierPassed,
        bool circuitBroken,
        bool stageComplete)
    {
        return new WorkflowRuntimeVars
        {
            VerifierScore = verifierScore,
            VerifierPassed = verifierPassed,
            CircuitBroken = circuitBroken,
            StageAttempts = stageAttempts,
            GatesGreen = gatesGreen,
            // SC4.2: a workflow branching on hasCommits is asking whether the AGENT committed, so
            // conductor's own chore(conductor): bookkeeping never answers that question for it.
            HasCommits = Git.ExcludeBookkeeping(rec.NewCommits).Count > 0,
            Stalled = rec.Outcome is SessionOutcome.Stalled or SessionOutcome.TimedOut,
            NewlyDoneCount = rec.NewlyDone?.Count ?? 0,
            StageComplete = stageComplete,
        };
    }
}
