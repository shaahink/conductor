using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class VerdictEngine
{
    // ── workflow advancement (M3.1) ──

    /// <summary>Consult the workflow engine for the next step after a session completes.
    /// <paramref name="sessionStartHead"/> is the just-finished session's start commit — the audit
    /// diff base when the QA dial narrows the audit to the latest session (P2).</summary>
    private void AdvanceWorkflowStep(
        StageConfig stage,
        SessionRecord rec,
        bool gatesGreen,
        int? verifierScore,
        bool verifierPassed,
        bool circuitBroken,
        bool stageComplete = false,
        string? sessionStartHead = null)
    {
        var workflow = _ctx.Workflows.Resolve(_ctx.Plan, stage, _ctx.Qa);
        var stepIndex = _ctx.State.WorkflowStepIndices.GetValueOrDefault(stage.Id, -1);
        var vars = WorkflowVarsFactory.Build(rec, _ctx.State.AttemptsThisStage,
            gatesGreen, verifierScore, verifierPassed, circuitBroken, stageComplete);

        // ResolveAndRecordStep resolves AND records the index in one call — see its doc comment
        // (WorkflowEngine.cs) for why that matters (a real bug: this used to be two independent
        // read-resolve-write cycles here and in SessionRunner.ResolveSessionKind, which drifted
        // out of sync and left PendingVerify/PendingAudit/PendingFix unpopulated in some cases).
        var next = _ctx.Workflows.ResolveAndRecordStep(workflow, _ctx.State.WorkflowStepIndices, stage.Id, vars);
        if (next == null)
        {
            _ctx.Log($"workflow '{workflow.Name}' exhausted after step {stepIndex} — stage complete");
            return;
        }

        var nextIndex = _ctx.State.WorkflowStepIndices[stage.Id];
        _ctx.Log($"workflow '{workflow.Name}': step {stepIndex} → {nextIndex} ({next.Id}, kind={next.Kind})");

        if (next.Kind == SessionKind.Verify && (_ctx.Qa.EffectiveSkipVerification(_ctx.Plan, stage) || _ctx.State.SkipVerificationThisStage))
        {
            _ctx.Log($"workflow override: skipping verification step for stage {stage.Id} — treating as passed");
            // M4.1: confirm checkpoints immediately when verification is skipped
            ConfirmPendingCheckpoints(stage.Id);
            AdvanceWorkflowStep(stage, rec, gatesGreen, verifierScore, verifierPassed: true, circuitBroken, stageComplete, sessionStartHead);
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
                // P2: auditCoversPriorSessions=false narrows the audit's diff base from the stage
                // start (everything accumulated, the classic phase-gate scope) to just the session
                // that triggered it.
                var coversPrior = _ctx.Qa.Project(_ctx.Plan, stage).AuditCoversPriorSessions;
                _ctx.State.PendingAudit = new PendingAudit
                {
                    StageId = stage.Id,
                    StageStartHead = (coversPrior ? _ctx.State.CurrentStageStartHead : sessionStartHead)
                        ?? _ctx.State.CurrentStageStartHead ?? "",
                };
                break;
            case SessionKind.Deliver:
                break;
        }
    }
}
