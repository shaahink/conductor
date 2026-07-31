using Conductor.Core.Orchestration;
using Conductor.Core.Store;
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
    private readonly IQaPolicy _qa;

    public PromptBuilder(PlanConfig plan, PersonaRegistry? personaRegistry = null, LessonsManager? lessons = null, IQaPolicy? qa = null)
    {
        _plan = plan;
        _personas = personaRegistry ?? new PersonaRegistry(plan);
        _lessons = lessons ?? new LessonsManager(plan.StateDir);
        _qa = qa ?? new DefaultQaPolicy();
    }
    public string Deliver(StageConfig stage, int sessionNumber, int attempt, int maxAttempts, string? personaOverride = null)
        => Render("session.md", Vars(stage, sessionNumber, attempt, maxAttempts, personaOverride));

    public string Fix(StageConfig stage, int sessionNumber, int attempt, int maxAttempts, PendingFix fix, string? personaOverride = null)
    {
        var vars = Vars(stage, sessionNumber, attempt, maxAttempts, personaOverride);
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

    public string Audit(StageConfig stage, int sessionNumber, Models.PendingAudit audit, string stageStartHead, string? personaOverride = null)
    {
        var vars = Vars(stage, sessionNumber, 1, 1, personaOverride);
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

    public string Verify(StageConfig stage, int sessionNumber, PendingVerify verify, string? personaOverride = null)
    {
        var vars = Vars(stage, sessionNumber, 1, 1, personaOverride);
        vars["prevSession"] = verify.FromSession.ToString();
        vars["diffBase"] = verify.StageStartHead;
        return Render("verify.md", vars);
    }

    public string Review(StageConfig stage, int sessionNumber, int attempt, int maxAttempts, string reviewPath)
    {
        var vars = Vars(stage, sessionNumber, attempt, maxAttempts);
        vars["reviewPath"] = reviewPath;
        return Render("review.md", vars);
    }

    private Dictionary<string, string> Vars(StageConfig stage, int sessionNumber, int attempt, int maxAttempts, string? personaOverride = null)
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

        // P1: a role→agent rule may override the stage persona for this one session.
        var personaName = personaOverride ?? _plan.ResolvePersona(stage);
        var personaSystemPrompt = _personas.ResolveSystemPrompt(personaName) ?? "";

        var lessonsContent = _lessons.ReadRecent(5);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["planName"] = _plan.Name,
            ["repo"] = _plan.Repo,
            ["tracker"] = _plan.Tracker,
            // Fall back to the tracker when a plan sets no separate design doc, so the ritual reads
            // "exactly as `TRACKER.md` prescribes" instead of the broken "exactly as `` prescribes".
            ["planDoc"] = string.IsNullOrWhiteSpace(_plan.PlanDoc) ? _plan.Tracker : _plan.PlanDoc,
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
            ["verifierThreshold"] = _qa.EffectiveVerifierThreshold(_plan, stage).ToString(),
            ["tools"] = ToolContract.Render(_plan),
            ["packs"] = LoadPacks(),
            ["batteryCollapseNote"] = _plan.BatteryCollapse
                ? "\n**IMPORTANT — battery collapse (B10.4):** Do NOT run build or test commands yourself. Conductor's independent battery is the single source of truth. Instead, describe what you changed in a `## Changes` section of your handoff so Conductor knows what to verify. This saves tokens and avoids duplicating work."
                : "",
        };
    }

    /// <summary>Resolves a template by name. Search order: the plan's <c>templatesDir</c>, then the plan
    /// directory itself, then the built-in default. Until this honoured <c>templatesDir</c>, every plan that
    /// set it silently ran on the built-ins and its .md files were dead — so a template edit did nothing.</summary>
    internal string ResolveTemplatePath(string templateFile)
    {
        if (!string.IsNullOrWhiteSpace(_plan.TemplatesDir))
        {
            var inTemplatesDir = Path.Combine(_plan.PlanDir, _plan.TemplatesDir, templateFile);
            if (File.Exists(inTemplatesDir)) return inTemplatesDir;
        }
        return Path.Combine(_plan.PlanDir, templateFile);
    }

    /// <summary>Concatenates the plan's domain packs (<c>templatesDir/packs/&lt;name&gt;.md</c>) — the
    /// "batteries included" context: house C# style, the mistakes agents make in this domain, etc.</summary>
    private string LoadPacks()
    {
        if (_plan.Packs is not { Count: > 0 }) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var name in _plan.Packs)
        {
            var path = Path.Combine(_plan.PlanDir, _plan.TemplatesDir ?? "", "packs", name + ".md");
#pragma warning disable MA0045 // sync read — Render is sync on the control loop's session-start path
            if (!File.Exists(path)) continue;
            sb.AppendLine(File.ReadAllText(path).Trim()).AppendLine();
#pragma warning restore MA0045
        }
        return sb.ToString().TrimEnd();
    }

    private string Render(string templateFile, Dictionary<string, string> vars)
    {
#pragma warning disable MA0045 // sync Render — called from BuildPrompt which is sync in the control loop
        var path = ResolveTemplatePath(templateFile);
        var text = File.Exists(path) ? File.ReadAllText(path) : BuiltIn(templateFile);
#pragma warning restore MA0045

        // SC3.3. Two protections, and the order is the whole point:
        //   1. the template's own {{word}} escapes are held BEFORE substitution, so `{{extra}}`
        //      renders the literal `{extra}` instead of the extra's value;
        //   2. every substituted VALUE is held as it goes in, so a brace inside stage notes, a
        //      tracker handoff, gate output or an agent's transcript tail is prose — it can neither
        //      be re-substituted by a later variable (which used to depend on dictionary order) nor
        //      read as an unresolved placeholder and kill the run.
        // Only the template can carry a placeholder, so only the template can be refused.
        text = PromptPlaceholders.ProtectEscapes(text);
        foreach (var (k, v) in vars) text = text.Replace("{" + k + "}", PromptPlaceholders.ProtectValue(v), StringComparison.Ordinal);
        PromptValidator.ThrowIfUnresolved(text, templateFile);
        text = PromptPlaceholders.Restore(text).Trim();

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
    /// <see cref="BatteriesConfig"/>, using current run state for the recent-failure digest (B8.5).
    /// When a <paramref name="store"/> is supplied (always, in a real run) the M7 knowledge batteries —
    /// the ledger and the run's open bugs — are injected too, so knowledge compounds across sessions.</summary>
    public string BatterySection(RunState? state, IRunStore? store = null)
    {
        var cfg = _plan.Batteries;

        var list = new List<IPromptBattery>();

        // M7.1/M7.2: knowledge that compounds — injected by default (no batteries block required), and
        // added FIRST so the byte cap never truncates them away: the ledger and open bugs from prior
        // sessions must reliably reach the next prompt (that is the whole point of M7).
        if (store != null && state is { RunId.Length: > 0 })
        {
            if (cfg?.Ledger ?? true)
            {
                var ledgerBattery = new LedgerBattery(store, state.RunId, cfg?.LedgerMaxEntries ?? 8);
                if (!ledgerBattery.IsEmpty) list.Add(ledgerBattery);
            }
            if (cfg?.Bugs ?? true)
            {
                var bugsBattery = new BugsBattery(store, state.RunId);
                if (!bugsBattery.IsEmpty) list.Add(bugsBattery);
            }
        }

        if (cfg != null)
        {
            if (cfg.Lessons) list.Add(new LessonsBattery(_lessons, cfg.LessonsMaxEntries));
            if (cfg.RecentFailure && state != null) list.Add(new RecentFailureBattery(state));
        }

        // B12.1: inject recent analysis-lane artifacts into the next session's prompt
        if (_plan.AnalysisLanes.Count > 0 && state != null)
        {
            var laneBattery = new LaneArtifactBattery(_plan.StateDir, state.CurrentStage ?? "");
            if (!laneBattery.IsEmpty) list.Add(laneBattery);
        }

        var maxBytes = cfg?.MaxBytes ?? 2048;
        return list.Count > 0 ? new BatteryGroup(list, maxBytes).Render() : "";
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
            4. POST-SESSION RITUAL — re-run the gate battery plus your stage's truth gates; produce fresh evidence artifacts; CLAIM each delivered checkpoint with `conductor task --done <id> --evidence <path>` (the only channel Conductor reads — the tracker's checkpoint rows are generated from it); overwrite the `{tracker}` handoff block for the next session; commit per checkpoint using the plan's commit convention; push the branch.

            {tools}

            Conductor rules (in addition to the plan's):
            - If genuinely blocked on a human decision, add a line starting `HUMAN:` to the tracker handoff block, `conductor note` the reason, commit, push, and end the session.
            - Leave the working tree clean (commit or revert leftovers) and the branch pushed.
            - End by printing one paragraph starting with `SESSION-RESULT:` — what landed, what is red, what the next session should do.

            {packs}
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
            1. Read `{tracker}` handoff + your stage section in `{planDoc}` first. Check `ledger_list` — the failing session may already have recorded why.
            2. Reproduce each failure above and fix root causes. Never weaken gates, goldens, or truth files to pass — ratchet-only policy.
            3. Re-run the full gate battery until green.
            4. Correct the record via `conductor task` — downgrade over-claimed checkpoints rather than leaving a false DONE.
            5. Commit (plan's commit convention), push, overwrite the tracker handoff block.
            Only if gates are green and time allows, continue stage {stage}'s next checkpoint per the normal ritual.

            {tools}

            If genuinely blocked on a human decision, add a line starting `HUMAN:` to the tracker handoff, commit, push, and stop.
            End by printing one paragraph starting with `SESSION-RESULT:`.

            {packs}
            {stageNotes}{extra}
            """,
        "resume.md" => """
            Conductor detected that your previous run in "{planName}" stage {stage} was interrupted ({reason}).

            {readOrder}
            Re-orient before acting: run `git status` and `git log --oneline -5` in {repo}, re-read the `{tracker}` handoff block, and inspect what you had in flight. Then finish the in-flight work and complete the full post-session ritual: gate battery green, fresh evidence artifacts, each delivered checkpoint claimed with `conductor task --done <id> --evidence <path>`, the `{tracker}` handoff block overwritten, committed per checkpoint, pushed.

            Before anything else, run `ledger_list` — your previous self may have recorded what it was doing and why. Do not re-derive what it already knew.

            If the interruption left half-done changes you cannot finish safely, revert to the last good state, record what happened in the tracker handoff, commit and push that.

            {tools}
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

            Reply with ONLY a JSON object, no prose: {"action":"<action>","reason":"one sentence"}

            Available actions (choose the strongest that applies):
            - BlockRetry: stall pattern detected (2+ identical failures with zero commits) — block further attempts until a human or condition clears
            - ResetBudget: session exhausted its attempt budget on a fixable problem — reset the attempt counter, granting more tries
            - NeedsHuman: a human must intervene before anything else runs (broken environment, bad config, decision needed)
            - ApplyFix: run a configured remediation script (e.g., kill stale agent process, clean temp files) then retry
            - RerunGates: re-run the gate battery instead of another agent session — claims may already be true
            - retry: a fresh fix-session is likely to succeed (legacy: maps to fresh attempt)
            - resume: resume the interrupted agent session to finish in-flight work (legacy: maps to resume)
            - skip: park this stage for human review later and move on (legacy: maps to skip)
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

            {tools}

            If you find an issue that needs a human decision, add a line starting `HUMAN:` to the tracker handoff, commit, push, and stop.
            End by printing one paragraph starting with `SESSION-RESULT:` summarising the audit verdict and what you changed.

            {packs}
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
        "verify.md" => """
            You are a VERIFICATION session inside the "{planName}" mega plan, launched by the Conductor orchestrator after session #{prevSession} in stage {stage} — {stageTitle} completed its deliver phase.

            Work in: {repo}
            {readOrder}
            Session #{prevSession} has just delivered work. Your job is to INDEPENDENTLY verify that the work matches its claims.

            Do, in order:
            1. Read `{tracker}` (handoff block + checkpoint rows) and the stage's design doc in `{planDoc}`.
            2. Inspect the actual changes: `git diff {diffBase}..HEAD` and the files touched.
            3. Re-run the gate battery yourself — do not trust the previous session's gate results. Run `dotnet build` and `dotnet test` independently.
            4. Check every claim in the handoff against reality: do the commits exist? do the files mentioned actually exist? do the tests actually pass? are code changes genuinely correct?
            5. Look for bugs, race conditions, resource leaks, async anti-patterns, analyzer violations.

            Then produce your verdict as a SINGLE JSON object (no prose before or after):

            {"score":0-100,"findings":["finding 1","finding 2"],"verdict":"PASS|WARN|FAIL"}

            Scoring guide:
            - 90-100: Excellent — all claims verified, gates green, code is correct and well-structured, no issues found.
            - 80-89: Good — claims mostly verified, gates green, minor or cosmetic issues only.
            - 60-79: Needs work — gates green but claims don't fully match reality, or found real bugs.
            - 0-59: Failed — gates red, or claims are false, or serious bugs present.

            Your score determines the next step: ≥{verifierThreshold} means the checkpoint is DONE and findings become follow-ups. Below that, your findings become the retry prompt — so write them as instructions, not complaints.

            {tools}
            End by printing exactly the JSON object on its own line.
            {stageNotes}{extra}
            """,
        "chat.md" => """
            You are a CONDUCTOR CHAT agent — you answer questions about a running (or completed) conductor plan. You have access to the full run history via MCP tools. Be concise; prefer facts over speculation.

            Context:
            - Plan: {planName}
            - Repo: {repo}
            - Tracker: {tracker}
            - Number of checkpoints DONE: {sessionNumber} (placeholder — actual count is in the tracker)

            USER QUERY:
            {extra}

            RULES:
            1. Answer the user's query DIRECTLY — don't narrate your process unless asked.
            2. Use your MCP tools to gather data — never guess when you can query:
               - `run_query` — execute SQL against run.db (tables: sessions, gates, costs, ledger, stages, checkpoints)
               - `session_detail` — look up a specific session by number
               - `ledger_list` — see recent findings/observations
               - `task_list` — see the current checkpoint's sub-tasks
               - `inject_instruction` — if asked to update a task or inject context, use this to write it
            3. If asked to make a change (update a task, inject an instruction, add a note), DO IT via the appropriate MCP tool — don't just describe what should happen.
            4. Format costs as dollars ($X.XX) and times as minutes or hours.
            5. If the user's query is ambiguous, ask ONE clarifying question then proceed.
            6. End with a one-line summary in bold.
            """,
        _ => throw new ArgumentException($"No built-in template named {name}", nameof(name)),
    };
}
