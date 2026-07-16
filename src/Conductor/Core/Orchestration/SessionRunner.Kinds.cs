using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class SessionRunner
{
    // ── workflow-driven kind resolution (M3.1) + prompt construction ──

    private SessionKind ResolveSessionKind(
        StageConfig stage,
        PendingResume? pendingResume,
        PendingAudit? pendingAudit,
        PendingVerify? pendingVerify,
        PendingFix? pendingFix)
    {
        // Crash recovery: Resume always wins — it carries the agent session id
        if (pendingResume != null) return SessionKind.Resume;

        // Explicit pending states from the previous workflow step take priority
        if (pendingAudit != null) return SessionKind.Audit;
        if (pendingVerify != null) return SessionKind.Verify;
        if (pendingFix != null) return SessionKind.Fix;

        // Workflow-driven: what does the engine say is next? A recorded index IS this session's
        // step — the previous evaluation resolved it (setting PendingVerify/Audit/Fix for those
        // kinds, or landing on a deliver) and recorded it. Consume it WITHOUT advancing: resolving
        // again here double-stepped the workflow onto a verify step no evaluation had populated
        // PendingVerify for — an NRE in PromptBuilder.Verify — whenever evaluation had wrapped
        // back to a deliver (latent) or the QA dial changed at a live boundary (P2, deterministic).
        // Only the very first resolution of a stage (no index yet) advances, from -1.
        var workflow = _ctx.Workflows.Resolve(_ctx.Plan, stage, _ctx.Qa);
        WorkflowStep? step;
        if (_ctx.State.WorkflowStepIndices.TryGetValue(stage.Id, out var recorded)
            && recorded >= 0 && recorded < workflow.Steps.Count)
        {
            step = workflow.Steps[recorded];
        }
        else
        {
            var vars = new WorkflowRuntimeVars(); // initial run — no prior session vars
            step = _ctx.Workflows.ResolveAndRecordStep(workflow, _ctx.State.WorkflowStepIndices, stage.Id, vars);
        }
        if (step != null)
        {
            var wfKind = step.Kind;
            // Skip verification when the QA dial or the per-stage override says so (P2/M3.2)
            if (wfKind == SessionKind.Verify && _ctx.Qa.EffectiveSkipVerification(_ctx.Plan, stage))
                return SessionKind.Deliver; // fall through to next deliver
            return wfKind;
        }

        // Workflow exhausted or not configured — default to Deliver
        return SessionKind.Deliver;
    }

    public static SessionKind PendingToKind(
        PendingResume? pr, PendingAudit? pa, PendingVerify? pv, PendingFix? pf)
    {
        if (pr != null) return SessionKind.Resume;
        if (pa != null) return SessionKind.Audit;
        if (pv != null) return SessionKind.Verify;
        if (pf != null) return SessionKind.Fix;
        return SessionKind.Deliver;
    }

    private string BuildPrompt(SessionKind kind, StageConfig stage, int sessionNumber, int attempt, int maxAttempts,
        PendingResume? pendingResume, PendingAudit? pendingAudit, PendingVerify? pendingVerify, PendingFix? pendingFix,
        bool isReview, string reviewPath, string? personaOverride = null)
    {
        return kind switch
        {
            SessionKind.Resume => _ctx.Prompts.Resume(stage, sessionNumber, attempt, maxAttempts, pendingResume!),
            // The diff base rides PendingAudit (P2: a phaseGate dial with auditCoversPriorSessions=false
            // scopes it to the latest delivery session; classically it equals the stage start head).
            SessionKind.Audit => _ctx.Prompts.Audit(stage, sessionNumber, pendingAudit!,
                pendingAudit!.StageStartHead is { Length: > 0 } auditBase ? auditBase : _ctx.State.CurrentStageStartHead ?? "HEAD~1", personaOverride),
            SessionKind.Verify => _ctx.Prompts.Verify(stage, sessionNumber, pendingVerify!, personaOverride),
            SessionKind.Fix => _ctx.Prompts.Fix(stage, sessionNumber, attempt, maxAttempts, pendingFix!, personaOverride),
            _ => isReview
                ? _ctx.Prompts.Review(stage, sessionNumber, attempt, maxAttempts, reviewPath)
                : _ctx.Prompts.Deliver(stage, sessionNumber, attempt, maxAttempts, personaOverride),
        };
    }
}
