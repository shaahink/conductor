using System.Text;
using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;
using Conductor.Planning;

namespace Conductor.Core.Orchestration;

/// <summary>
/// KS3.4 round 6 — the ONE place a session's prompt is put together, from the template render to the
/// last appended section, read by all three surfaces that claim to know what a session says: the
/// live <see cref="SessionRunner"/>, the run loop's dry-run branch, and <c>preflight</c>'s compose
/// leg.
/// <para>Rounds 1–5 shared the loop's DECISION and its INPUTS, and the leg still measured a
/// different string than the launch spawned, because the prompt is not finished when the decision
/// is: <see cref="SessionRunner"/> appended three more sections after the battery — the multi-item
/// claim list, the task-context cards, and the completed parallel audit's findings — none measured,
/// none bounded by <c>batteries.maxBytes</c>, and one of them (the findings) 3000 chars of exactly
/// the kind of text that walks a composed argv over the 8191-char cmd.exe ceiling. Round 6 measured
/// it live: drill said 7592, launch spawned 10094. The tail lives here now, once; the runner calls
/// this and hands the result to the agent, the drill calls this and measures the result, so the two
/// numbers are the same computation.</para>
/// <para>Pure with respect to the state it is handed: nothing here writes a file, opens a store
/// read-write, or mutates <see cref="RunState"/> — the runner performs the mutations this
/// composition implies (consuming the parallel-audit outcome, clearing pendings) itself, guided by
/// the flags on the returned <see cref="Composition"/>.</para>
/// </summary>
public static class SessionComposer
{
    /// <summary>The composed session, whole. <paramref name="Prompt"/> is the exact string the agent
    /// is handed; <paramref name="PromptSansBattery"/> is the same assembly with the battery section
    /// left out — the base the unmeasured-knowledge ceiling is computed from, because the whole
    /// battery section is capped at <c>batteries.maxBytes</c> and adding the cap to a string already
    /// carrying measured batteries would count them twice. <paramref name="Kind"/> and
    /// <paramref name="Stage"/> are the composition's own (a verify retargets to the stage that
    /// DELIVERED — W1.3 — and a workflow fix with no failure context falls back to a delivery).
    /// <paramref name="ConsumesParallelAuditOutcome"/> tells the caller that the findings section
    /// was included and the outcome is spent — the runner clears it from state.</summary>
    public sealed record Composition(
        SessionKind Kind,
        StageConfig Stage,
        string Prompt,
        string PromptSansBattery,
        SessionAssignment Assignment,
        PendingResume? Resume,
        PendingAudit? Audit,
        PendingVerify? Verify,
        PendingFix? Fix,
        bool ConsumesParallelAuditOutcome,
        bool IsReview,
        string ReviewPath);

    /// <summary>Compose the session exactly as the runner will hand it to the agent: template render
    /// (persona-resolved through the assignment policy), battery section, claimed-items list,
    /// task-context cards, parallel-audit findings — in the runner's own order, with the runner's own
    /// joins. <paramref name="store"/> is the live store when the caller has one (the runner), null
    /// at rest (the drill, the dry run) — the store-backed knowledge batteries are the one part a
    /// read-only surface reports as a ceiling instead of a measurement.</summary>
    public static Composition Compose(
        PlanConfig plan, PromptBuilder prompts, IAssignmentPolicy assignments,
        RunState state, TrackerSnapshot track, TaskGraph? graph, IRunStore? store,
        SessionKind kind, StageConfig stage, int sessionNumber, int attempt,
        PendingResume? pendingResume, PendingAudit? pendingAudit,
        PendingVerify? pendingVerify, PendingFix? pendingFix)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(stage);

        // A workflow-resolved kind can arrive without its pending context (a custom workflow that
        // opens on a QA step, or a pending cleared out from under the recorded index). Verify and
        // audit have a well-defined meaning without one — review the stage's work since it started
        // — so synthesize that context rather than dereference null. A fix without failure context
        // is just a delivery attempt; fall back honestly. The one fix that CAN be synthesized is the
        // one the launch itself will materialize: a completed HIGH-severity parallel audit becomes
        // the queued fix before anything composes (RunLoop's own branch), so a drill composing at
        // rest builds the same PendingFix here that the loop will write.
        // The runner computes this before it increments SessionCounter; a surface composing at rest
        // has not incremented anything — sessionNumber - 1 is the same instant on both.
        var lastSession = state.History.Count > 0 ? state.History[^1].Number : sessionNumber - 1;
        if (kind == SessionKind.Fix && pendingFix is null
            && state.ParallelAuditOutcome is { Completed: true, MaxSeverity: AuditFindingSeverity.High } high)
            pendingFix = FixFromParallelAudit(state, high);
        if (kind == SessionKind.Verify && pendingVerify is null)
            pendingVerify = new PendingVerify { FromSession = lastSession, StageId = stage.Id, StageStartHead = state.CurrentStageStartHead ?? "" };
        else if (kind == SessionKind.Audit && pendingAudit is null)
            pendingAudit = new PendingAudit { StageId = stage.Id, StageStartHead = state.CurrentStageStartHead ?? "" };
        else if (kind == SessionKind.Fix && pendingFix is null)
            kind = SessionKind.Deliver;

        // W1.3 (bug #6): a Verify session reviews the stage that DELIVERED, not the stage the loop
        // has already moved on to.
        stage = EffectiveStage(plan, stage, kind, pendingVerify);

        // P1: ask the assignment policy who runs this session and which ready items it claims.
        // PF3: each item carries the declared paths of its OPEN task cards.
        var readyItems = track.ForStage(stage.Id).Where(c => c.IsOpen)
            .Select(c => new ReadyItem { Id = c.Id, Title = c.Title, PathClaims = graph?.DeclaredOpenPaths(c.Id) })
            .ToList();
        var assignment = assignments.Assign(plan.Pipeline, kind, readyItems, claimedPaths: null);

        var isReview = stage.Kind.Equals("review", StringComparison.OrdinalIgnoreCase);
        var reviewPath = isReview ? Path.Combine(plan.StateDir, "reviews", $"{stage.Id}.md") : "";
        var maxAttempts = StageSelection.MaxAttempts(plan, stage);

        var core = kind switch
        {
            SessionKind.Resume => prompts.Resume(stage, sessionNumber, attempt, maxAttempts, pendingResume!),
            // The diff base rides PendingAudit (P2: a phaseGate dial with auditCoversPriorSessions=false
            // scopes it to the latest delivery session; classically it equals the stage start head).
            SessionKind.Audit => prompts.Audit(stage, sessionNumber,
                pendingAudit!.StageStartHead is { Length: > 0 } auditBase ? auditBase : state.CurrentStageStartHead ?? "HEAD~1", assignment.Persona),
            SessionKind.Verify => prompts.Verify(stage, sessionNumber, pendingVerify!, assignment.Persona),
            SessionKind.Fix => prompts.Fix(stage, sessionNumber, attempt, maxAttempts, pendingFix!, assignment.Persona),
            _ => isReview
                ? prompts.Review(stage, sessionNumber, attempt, maxAttempts, reviewPath)
                : prompts.Deliver(stage, sessionNumber, attempt, maxAttempts, assignment.Persona),
        };

        // KS7.5: the folded board and the effective stage reach the battery section, so the
        // definition-of-done recap names the card THIS session is holding rather than a placeholder.
        var battery = prompts.BatterySection(state, store, graph?.Checkpoints(), stage.Id);
        var consumed = false;
        var prompt = AppendTail(plan, state, graph, kind, assignment,
            battery.Length > 0 ? core.TrimEnd() + "\n\n" + battery : core, ref consumed);
        var sansBattery = AppendTail(plan, state, graph, kind, assignment, core, ref consumed);

        return new Composition(kind, stage, prompt, sansBattery, assignment,
            pendingResume, pendingAudit, pendingVerify, pendingFix, consumed, isReview, reviewPath);
    }

    /// <summary>The three sections the runner appends AFTER the battery, in the runner's order and
    /// with the runner's joins — the tail round 6 caught being spawned but never measured.</summary>
    private static string AppendTail(PlanConfig plan, RunState state, TaskGraph? graph,
        SessionKind kind, SessionAssignment assignment, string prompt, ref bool consumed)
    {
        // P1: a multi-item session must SEE every item it claimed — the prompt names each one.
        if (assignment.Items.Count > 1)
        {
            var claimedList = new StringBuilder();
            claimedList.AppendLine("## Claimed items this session");
            claimedList.AppendLine("The assignment policy claimed ALL of the following conflict-free items for this single session. Deliver each one and update its tracker row (Status + Commit + Evidence) individually.");
            foreach (var item in assignment.Items)
                claimedList.AppendLine($"- **{item.Id}** — {item.Title}");
            prompt = prompt.TrimEnd() + "\n\n" + claimedList.ToString().TrimEnd() + "\n";
        }

        // P3/W2.3: the cards for the claimed checkpoints — title and owner-attached context — are real
        // prompt input, not decoration, and are rendered by the same composer the card detail serves.
        if (graph != null)
        {
            var contextSection = SessionRunner.BuildTaskContextSection(plan, graph, assignment.Items.Select(i => i.Id));
            if (contextSection.Length > 0)
                prompt = prompt.TrimEnd() + "\n\n" + contextSection;
        }

        if (kind == SessionKind.Deliver && state.ParallelAuditOutcome is { Completed: true, MaxSeverity: not AuditFindingSeverity.High } outcome)
        {
            var findings = Trunc(outcome.Findings, 3000);
            if (!string.IsNullOrWhiteSpace(findings))
            {
                prompt = prompt.TrimEnd() + $"\n\n## Parallel audit findings for stage {outcome.StageId}\n" +
                    "The following audit findings were produced by a read-only audit lane running concurrently with the previous stage. " +
                    // SC3.3: this line was not interpolated, so every parallel-audit hand-off since
                    // B12 shipped the agent the literal text "{findings}" and dropped the findings.
                    $"Address LOW and MEDIUM findings in this session if convenient.\n\n{findings}";
                consumed = true;
            }
        }
        return prompt;
    }

    /// <summary>W1.3 (bug #6): the stage the composed session is ABOUT. A Verify reviews the stage
    /// that delivered — <c>PendingVerify.StageId</c> is authoritative for the prompt, the session
    /// record and the verdict scope — while the loop's selected stage has already moved on. Shared so
    /// the drill names (and measures) the same stage the dispatch will.</summary>
    public static StageConfig EffectiveStage(PlanConfig plan, StageConfig stage, SessionKind kind, PendingVerify? pendingVerify)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(stage);
        if (kind == SessionKind.Verify && pendingVerify is { StageId.Length: > 0 } pv
            && !pv.StageId.Equals(stage.Id, StringComparison.OrdinalIgnoreCase)
            && plan.Stages.FirstOrDefault(s => s.Id.Equals(pv.StageId, StringComparison.OrdinalIgnoreCase)) is { } deliveredStage)
            return deliveredStage;
        return stage;
    }

    /// <summary>The fix a completed HIGH-severity parallel audit becomes — <c>RunLoop</c>'s
    /// materialization, stated once so the drill's at-rest synthesis and the launch's queued
    /// <c>PendingFix</c> are the same bytes.</summary>
    public static PendingFix FixFromParallelAudit(RunState state, ParallelAuditOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(outcome);
        return new PendingFix
        {
            FromSession = state.History.LastOrDefault()?.Number ?? 0,
            GateFailures = "",
            ProgressSummary = $"prior parallel audit found HIGH-severity issues in stage {outcome.StageId}:\n{Trunc(outcome.Findings, 2000)}",
        };
    }

    /// <summary>The field mutations of the loop's stage-entry block — the ones that change what the
    /// NEXT compose renders (start head, attempt counter, a fix that does not survive its stage, the
    /// recorded workflow index). The loop performs them live before every compose; a surface
    /// composing at rest applies them to its peeked copy, or it renders the state of the stage being
    /// LEFT (round 6: an audit prompt carrying "HEAD~1" where the launch renders the entry head).
    /// The block's side effects (events, store rows, process title) stay in the loop.</summary>
    public static void ProjectStageEntry(RunState state, StageConfig stage, string headSha)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Id == state.CurrentStage) return;
        state.CurrentStage = stage.Id;
        state.CurrentStageStartHead = headSha;
        state.AttemptsThisStage = 0;
        state.PendingFix = null;
        state.WorkflowStepIndices.Remove(stage.Id); // reset workflow step for new stage
    }

    /// <summary>The work graph as a store-less surface reads it: the same <c>run.db</c> a launch
    /// would open, read-only, folded through the same <see cref="TaskGraph"/> — the graph the
    /// runner's claimed-paths, item-QA and task-context reads all use. Null when there is no run yet
    /// or the file cannot answer, which composes identically to a fresh store's empty graph.</summary>
    public static TaskGraph? GraphAtRest(PlanConfig plan, string runId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrEmpty(runId) || !File.Exists(plan.RunDbPath)) return null;
        try
        {
            using var store = SqliteRunStore.OpenReadOnly(plan.RunDbPath);
            var graph = new TaskGraph();
            graph.Fold(store.ReadAllEvents(runId));
            return graph;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
