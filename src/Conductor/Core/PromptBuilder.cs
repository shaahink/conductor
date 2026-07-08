using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// Renders session prompts from md templates in <plan-dir>/<templatesDir>/,
/// falling back to built-in defaults when a template file is absent.
/// Placeholders: {name} replaced verbatim.
/// </summary>
public sealed class PromptBuilder
{
    private readonly PlanConfig _plan;
    private readonly PersonaRegistry _personas;
    private readonly LessonsManager _lessons;

    public PromptBuilder(PlanConfig plan, PersonaRegistry? personaRegistry = null, LessonsManager? lessons = null)
    {
        _plan = plan;
        _personas = personaRegistry ?? new PersonaRegistry(plan);
        _lessons = lessons ?? new LessonsManager(plan.StateDir);
    }
    public string Deliver(StageConfig stage, int sessionNumber, int attempt, int maxAttempts)
        => Render("session.md", Vars(stage, sessionNumber, attempt, maxAttempts));

    public string Fix(StageConfig stage, int sessionNumber, int attempt, int maxAttempts, PendingFix fix)
    {
        var vars = Vars(stage, sessionNumber, attempt, maxAttempts);
        vars["gateFailures"] = string.IsNullOrWhiteSpace(fix.GateFailures) ? "(no gate output captured)" : fix.GateFailures;
        vars["progressSummary"] = fix.ProgressSummary;
        vars["prevSession"] = fix.FromSession.ToString();
        return Render("fix.md", vars);
    }

    public string Resume(StageConfig stage, int sessionNumber, int attempt, int maxAttempts, PendingResume resume)
    {
        var vars = Vars(stage, sessionNumber, attempt, maxAttempts);
        vars["reason"] = resume.Reason;
        return Render("resume.md", vars);
    }

    public string Audit(StageConfig stage, int sessionNumber, Models.PendingAudit audit, string stageStartHead)
    {
        var vars = Vars(stage, sessionNumber, 1, 1);
        vars["diffBase"] = stageStartHead;
        vars["handoverPath"] = $".conductor/handovers/{stage.Id}.md";
        return Render("audit.md", vars);
    }

    public string Advisor(StageConfig stage, string outcome, string gates, string commits, string handoff, string tail, int attempt, int maxAttempts)
    {
        var vars = Vars(stage, 0, attempt, maxAttempts);
        vars["outcome"] = outcome;
        vars["gates"] = gates;
        vars["commits"] = commits;
        vars["handoff"] = handoff;
        vars["tail"] = tail;
        return Render("advisor.md", vars);
    }

    public string Review(StageConfig stage, int sessionNumber, int attempt, int maxAttempts, string reviewPath)
    {
        var vars = Vars(stage, sessionNumber, attempt, maxAttempts);
        vars["reviewPath"] = reviewPath;
        return Render("review.md", vars);
    }

    private Dictionary<string, string> Vars(StageConfig stage, int sessionNumber, int attempt, int maxAttempts)
    {
        var readOrder = "";
        if (_plan.ReadOrder is { Count: > 0 } docs)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Required reading (in order):");
            for (var i = 0; i < docs.Count; i++)
                sb.AppendLine($"{i + 1}. {docs[i]}");
            readOrder = sb.ToString();
        }

        var personaName = _plan.ResolvePersona(stage);
        var personaSystemPrompt = _personas.ResolveSystemPrompt(personaName) ?? "";

        var lessonsContent = _lessons.ReadRecent(5);

        return new()
        {
            ["planName"] = _plan.Name,
            ["repo"] = _plan.Repo,
            ["tracker"] = _plan.Tracker,
            ["planDoc"] = _plan.PlanDoc,
            ["stage"] = stage.Id,
            ["stageTitle"] = stage.Title,
            ["stageNotes"] = string.IsNullOrWhiteSpace(stage.Notes) ? "" : $"\nStage-specific notes from the orchestrator config:\n{stage.Notes}\n",
            ["sessionNumber"] = sessionNumber.ToString(),
            ["attempt"] = attempt.ToString(),
            ["maxAttempts"] = maxAttempts.ToString(),
            ["extra"] = _plan.PromptExtra,
            ["readOrder"] = readOrder,
            ["persona"] = personaName ?? "",
            ["personaSystemPrompt"] = personaSystemPrompt,
            ["lessons"] = lessonsContent,
            ["batteryCollapseNote"] = _plan.BatteryCollapse
                ? "\n**IMPORTANT — battery collapse (B10.4):** Do NOT run build or test commands yourself. Conductor's independent battery is the single source of truth. Instead, describe what you changed in a `## Changes` section of your handoff so Conductor knows what to verify. This saves tokens and avoids duplicating work."
                : "",
        };
    }

    private string Render(string templateFile, Dictionary<string, string> vars)
    {
        var path = Path.Combine(_plan.PlanDir, _plan.TemplatesDir, templateFile);
        var text = File.Exists(path) ? File.ReadAllText(path) : BuiltIn(templateFile);
        foreach (var (k, v) in vars) text = text.Replace("{" + k + "}", v);
        text = text.Trim();

        // Merge in persona system prompt — appended after base prompt rendering so the
        // persona doesn't need to be referenced in every template (B7.3). Conductor contract
        // rules (the built-in template's rules block) remain last and win over persona content.
        var personaSystemPrompt = vars.GetValueOrDefault("personaSystemPrompt", "");
        if (personaSystemPrompt.Length > 0)
            text = personaSystemPrompt + "\n\n" + text;

        // Human-injected instructions (from the TUI `I` key / `.conductor/queue/`) are appended to
        // every session prompt so they're delivered even if a custom template omits the placeholder.
        var queued = InstructionQueue.PromptSection(_plan);
        if (queued.Length > 0) text += "\n\n" + queued;

        return text;
    }

    /// <summary>Builds and renders the battery group section from the plan's
    /// <see cref="BatteriesConfig"/>, using current run state for the recent-failure digest (B8.5).</summary>
    public string BatterySection(RunState? state)
    {
        var cfg = _plan.Batteries;
        if (cfg == null) return "";

        var list = new List<IPromptBattery>();
        if (cfg.Lessons) list.Add(new LessonsBattery(_lessons, cfg.LessonsMaxEntries));
        if (cfg.RecentFailure && state != null) list.Add(new RecentFailureBattery(state));

        return list.Count > 0 ? new BatteryGroup(list, cfg.MaxBytes).Render() : "";
    }

    internal static string BuiltIn(string name) => name switch
    {
        "session.md" => """
            You are one autonomous engineering session inside the "{planName}" mega plan, launched by the Conductor orchestrator (session #{sessionNumber}, target stage {stage} — {stageTitle}, attempt {attempt}/{maxAttempts}).

            Work in: {repo}
            {readOrder}
            Do, in order:
            1. PRE-SESSION RITUAL — exactly as `{planDoc}` prescribes: read `{tracker}` (handoff block + stated read order), your stage section, and the design docs it cites. Run the gate battery. Never build on red — fix or record first.{batteryCollapseNote}
            2. QA THE PREVIOUS SESSION — audit its tracker claims against fresh artifacts (re-run things; do not trust claims). Fix real findings before new work; note the QA verdict in your final tracker handoff.
            3. DELIVER the next incomplete checkpoint(s) of stage {stage} only. One checkpoint landed with proof beats three claimed. Do not start other stages' work.
            4. POST-SESSION RITUAL — re-run the gate battery plus your stage's truth gates; produce fresh evidence artifacts; update `{tracker}` (overwrite the handoff block, fill checkpoint rows: Status, Commit, Evidence); commit per checkpoint using the plan's commit convention; push the branch.

            Conductor rules (in addition to the plan's):
            - Evidence or it didn't happen: a checkpoint without a fresh artifact path is not DONE.
            - Never weaken gates, goldens, or truth files to get green — ratchet-only policy.
            - If genuinely blocked on a human decision, set the row BLOCKED, add a line starting `HUMAN:` to the tracker handoff block, commit, push, and end the session.
            - Leave the working tree clean (commit or revert leftovers) and the branch pushed.
            - End by printing one paragraph starting with `SESSION-RESULT:` — what landed, what is red, what the next session should do.
            {stageNotes}{extra}
            """,
        "fix.md" => """
            You are a FIX session inside the "{planName}" mega plan, launched by the Conductor orchestrator (session #{sessionNumber}, stage {stage} — {stageTitle}, attempt {attempt}/{maxAttempts}).

            Work in: {repo}
            {readOrder}
            The previous session (#{prevSession}) did not verify. Conductor independently re-ran the gates and observed:

            {gateFailures}

            Progress observed by the orchestrator: {progressSummary}

            Your job: make the previous session's claims true.
            1. Read `{tracker}` handoff + your stage section in `{planDoc}` first.
            2. Reproduce each failure above and fix root causes. Never weaken gates, goldens, or truth files to pass — ratchet-only policy.
            3. Re-run the full gate battery until green.
            4. Correct `{tracker}` to reflect reality — downgrade over-claimed rows with a note if needed.
            5. Commit (plan's commit convention), push, overwrite the tracker handoff block.
            Only if gates are green and time allows, continue stage {stage}'s next checkpoint per the normal ritual.

            If genuinely blocked on a human decision, add a line starting `HUMAN:` to the tracker handoff, commit, push, and stop.
            End by printing one paragraph starting with `SESSION-RESULT:`.
            {stageNotes}{extra}
            """,
        "resume.md" => """
            Conductor detected that your previous run in "{planName}" stage {stage} was interrupted ({reason}).

            {readOrder}
            Re-orient before acting: run `git status` and `git log --oneline -5` in {repo}, re-read the `{tracker}` handoff block, and inspect what you had in flight. Then finish the in-flight work and complete the full post-session ritual: gate battery green, fresh evidence artifacts, `{tracker}` updated (handoff + checkpoint rows), committed per checkpoint, pushed.

            If the interruption left half-done changes you cannot finish safely, revert to the last good state, record what happened in the tracker handoff, commit and push that.
            End by printing one paragraph starting with `SESSION-RESULT:`.
            """,
        "advisor.md" => """
            You advise an orchestrator that runs an autonomous multi-session engineering plan. A session ended badly; decide the next action. Be decisive and terse.

            Context:
            - Plan: {planName}, stage {stage} ({stageTitle}), attempt {attempt} of {maxAttempts}
            - Session outcome: {outcome}
            - Gate results: {gates}
            - New commits this session: {commits}
            - Tracker handoff block: {handoff}
            - Last agent output (tail): {tail}

            Reply with ONLY a JSON object, no prose: {"action":"retry|resume|skip|human","reason":"one sentence"}
            - retry: a fresh fix-session is likely to succeed
            - resume: resume the interrupted agent session to finish in-flight work
            - skip: park this stage for human review later and move on
            - human: a human must intervene before anything else runs
            """,
        "audit.md" => """
            You are an AUDIT session inside the "{planName}" mega plan, launched by the Conductor orchestrator after stage {stage} — {stageTitle} passed its full gate battery (session #{sessionNumber}).

            Work in: {repo}
            {readOrder}
            The stage's checkpoints are DONE and gates are green — now harden the work before the plan advances. Review everything this phase produced: `git diff {diffBase}..HEAD` (and the files it touched).

            Do a rigorous STATIC AUDIT of the phase's changes and ACT on what you find:
            1. Correctness bugs, race conditions, resource leaks, unhandled errors.
            2. Shallow / stubbed implementations that only satisfy the happy path or the truth tests superficially — deepen them so they are genuinely correct.
            3. Missing edge cases (empty/null, boundary, large input, concurrency, failure paths) — add handling and tests.
            4. Refactoring opportunities that reduce real risk or duplication (only if low-risk and clearly worth it).
            Ratchet-only: never weaken gates, goldens, or truth files. Fix root causes; add tests for anything you fix.

            Then:
            - Re-run the full gate battery; keep it green.
            - Commit your fixes with clear messages; push the branch.
            - Write an HONEST phase handover to `{handoverPath}` (create the folder if needed) covering, truthfully:
              * what is solid and proven (with evidence paths),
              * what is shortcut / weak / assumed / not fully covered,
              * bugs found and whether fixed or deferred,
              * risks the next phase should watch, and concrete follow-ups.
              Do not oversell. If something is thin, say so plainly. Commit and push this file.

            If you find an issue that needs a human decision, add a line starting `HUMAN:` to the tracker handoff, commit, push, and stop.
            End by printing one paragraph starting with `SESSION-RESULT:` summarising the audit verdict and what you changed.
            {stageNotes}{extra}
            """,
        "review.md" => """
            You are a SELF-REVIEW session inside the "{planName}" mega plan, launched by the Conductor orchestrator (session #{sessionNumber}, stage {stage} — {stageTitle}, attempt {attempt}/{maxAttempts}).

            Work in: {repo}
            {readOrder}
            Your role: review the last few sessions' work, gate results, and the plan itself. Propose concrete adjustments — gates to add, checkpoints to split, risks to watch. Be honestly critical; overselling defeats the point.

            Write your findings to `{reviewPath}`. This artifact is ADVISORY ONLY — Conductor reads it but does NOT auto-apply your recommendations. The human owner decides what to adopt.

            Structure your review artifact:
            1. **What went well** — evidence-backed wins from recent sessions.
            2. **What was hard** — patterns from the lessons file, repeated failures, toil.
            3. **Risks ahead** — traps the next stages should watch based on the code and the plan doc.
            4. **Proposed adjustments** — specific, actionable changes (gates, checkpoints, templates, conventions). Be concrete: cite stage ids, file paths, line numbers where possible.

            After writing the review: commit it, push, and update `{tracker}` normally.
            End by printing one paragraph starting with `SESSION-RESULT:` summarising the review verdict.
            {stageNotes}{extra}
            """,
        _ => throw new ArgumentException($"No built-in template named {name}", nameof(name)),
    };
}
