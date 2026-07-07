using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// Renders session prompts from md templates in <plan-dir>/<templatesDir>/,
/// falling back to built-in defaults when a template file is absent.
/// Placeholders: {name} replaced verbatim.
/// </summary>
public sealed class PromptBuilder(PlanConfig plan)
{
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

    private Dictionary<string, string> Vars(StageConfig stage, int sessionNumber, int attempt, int maxAttempts) => new()
    {
        ["planName"] = plan.Name,
        ["repo"] = plan.Repo,
        ["tracker"] = plan.Tracker,
        ["planDoc"] = plan.PlanDoc,
        ["stage"] = stage.Id,
        ["stageTitle"] = stage.Title,
        ["stageNotes"] = string.IsNullOrWhiteSpace(stage.Notes) ? "" : $"\nStage-specific notes from the orchestrator config:\n{stage.Notes}\n",
        ["sessionNumber"] = sessionNumber.ToString(),
        ["attempt"] = attempt.ToString(),
        ["maxAttempts"] = maxAttempts.ToString(),
        ["extra"] = plan.PromptExtra,
    };

    private string Render(string templateFile, Dictionary<string, string> vars)
    {
        var path = Path.Combine(plan.PlanDir, plan.TemplatesDir, templateFile);
        var text = File.Exists(path) ? File.ReadAllText(path) : BuiltIn(templateFile);
        foreach (var (k, v) in vars) text = text.Replace("{" + k + "}", v);
        return text.Trim();
    }

    internal static string BuiltIn(string name) => name switch
    {
        "session.md" => """
            You are one autonomous engineering session inside the "{planName}" mega plan, launched by the Conductor orchestrator (session #{sessionNumber}, target stage {stage} — {stageTitle}, attempt {attempt}/{maxAttempts}).

            Work in: {repo}

            Do, in order:
            1. PRE-SESSION RITUAL — exactly as `{planDoc}` prescribes: read `{tracker}` (handoff block + stated read order), your stage section, and the design docs it cites. Run the gate battery. Never build on red — fix or record first.
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
        _ => throw new ArgumentException($"No built-in template named {name}"),
    };
}
