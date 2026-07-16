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

        // Workflow-driven: the START-kind decision lives behind the seam (P4). The library consumes
        // a recorded index WITHOUT advancing (the previous advance resolved and recorded it —
        // re-resolving here once double-stepped onto a verify no advance had populated PendingVerify
        // for, an NRE in PromptBuilder.Verify); only a stage's very first resolution advances, and a
        // verify downgrades to Deliver when the QA dial or per-stage override skips verification.
        var workflow = _ctx.Workflows.Resolve(_ctx.Plan, stage, _ctx.Qa);
        return _ctx.Workflows.ResolveStartKind(workflow, _ctx.State.WorkflowStepIndices, stage.Id,
            _ctx.Qa.EffectiveSkipVerification(_ctx.Plan, stage));
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
