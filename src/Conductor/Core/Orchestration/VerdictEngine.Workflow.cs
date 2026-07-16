using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class VerdictEngine
{
    // ── workflow advancement (M3.1; decision moved behind the seam in P4) ──

    /// <summary>Ask the planning library what comes after a finished session, then EFFECT the
    /// answer — log the hops, confirm checkpoints for skipped-as-passed verifications, and populate
    /// the pending context the resolved kind needs. The walk itself (conditionals, repeat wrap,
    /// skip-verification collapse) is the library's <see cref="IWorkflowResolver.Advance"/>.
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
        var vars = WorkflowVarsFactory.Build(rec, _ctx.State.AttemptsThisStage,
            gatesGreen, verifierScore, verifierPassed, circuitBroken, stageComplete);
        var skipVerification = _ctx.Qa.EffectiveSkipVerification(_ctx.Plan, stage) || _ctx.State.SkipVerificationThisStage;

        var advance = _ctx.Workflows.Advance(workflow, _ctx.State.WorkflowStepIndices, stage.Id, vars, skipVerification);

        foreach (var hop in advance.Hops)
        {
            _ctx.Log($"workflow '{workflow.Name}': step {hop.FromIndex} → {hop.ToIndex} ({hop.Step.Id}, kind={hop.Step.Kind})");
            if (hop.SkippedAsPassed)
            {
                _ctx.Log($"workflow override: skipping verification step for stage {stage.Id} — treating as passed");
                // M4.1: confirm checkpoints immediately when verification is skipped
                ConfirmPendingCheckpoints(stage.Id);
            }
        }

        if (advance.Next is not { } next)
        {
            _ctx.Log($"workflow '{workflow.Name}' exhausted after step {advance.ExhaustedFromIndex} — stage complete");
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
