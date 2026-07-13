using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class VerdictEngine
{
    // ── workflow advancement (M3.1) ──

    /// <summary>Consult the workflow engine for the next step after a session completes.</summary>
    private void AdvanceWorkflowStep(
        StageConfig stage,
        SessionRecord rec,
        bool gatesGreen,
        int? verifierScore,
        bool verifierPassed,
        bool circuitBroken,
        bool stageComplete = false)
    {
        var workflow = _ctx.Workflows.Resolve(_ctx.Plan, stage);
        var stepIndex = _ctx.State.WorkflowStepIndices.GetValueOrDefault(stage.Id, -1);
        var vars = _ctx.Workflows.BuildRuntimeVars(rec, _ctx.State.AttemptsThisStage,
            gatesGreen, verifierScore, verifierPassed, circuitBroken, stageComplete);

        var next = _ctx.Workflows.GetNextStep(workflow, stepIndex, vars);
        if (next == null)
        {
            _ctx.Log($"workflow '{workflow.Name}' exhausted after step {stepIndex} — stage complete");
            _ctx.State.WorkflowStepIndices.Remove(stage.Id);
            return;
        }

        var nextIndex = workflow.Steps.FindIndex(s => s.Id == next.Id);
        if (nextIndex < 0) nextIndex = stepIndex + 1;
        _ctx.State.WorkflowStepIndices[stage.Id] = nextIndex;
        _ctx.Log($"workflow '{workflow.Name}': step {stepIndex} → {nextIndex} ({next.Id}, kind={next.Kind})");

        if (next.Kind == SessionKind.Verify && (stage.Overrides?.SkipVerification == true || _ctx.State.SkipVerificationThisStage))
        {
            _ctx.Log($"workflow override: skipping verification step for stage {stage.Id}");
            AdvanceWorkflowStep(stage, rec, gatesGreen, verifierScore, verifierPassed, circuitBroken, stageComplete);
            return;
        }

        switch (next.Kind)
        {
            case SessionKind.Verify:
                _ctx.State.PendingVerify = new PendingVerify
                {
                    FromSession = rec.Number,
                    StageId = stage.Id,
                    StageStartHead = _ctx.State.CurrentStageStartHead ?? "",
                };
                break;
            case SessionKind.Fix:
                _ctx.State.PendingFix = new PendingFix
                {
                    FromSession = rec.Number,
                    GateFailures = gatesGreen ? "" : GateRunner.FailureDetails(_ctx.LastGates ?? []),
                    ProgressSummary = $"Workflow step '{next.Id}' — fix session after {rec.Outcome}",
                };
                break;
            case SessionKind.Audit:
                _ctx.State.PendingAudit = new PendingAudit
                {
                    StageId = stage.Id,
                    StageStartHead = _ctx.State.CurrentStageStartHead ?? "",
                };
                break;
            case SessionKind.Deliver:
                break;
        }
    }
}
