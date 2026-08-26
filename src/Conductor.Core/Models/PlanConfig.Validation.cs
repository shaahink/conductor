using System.Text.RegularExpressions;

namespace Conductor.Models;

/// <summary>
/// Everything that can be WRONG with a plan, and the one place that says so. <c>Load</c> throws on
/// these and the Options validator collects them without throwing, so both read the same list.
/// Split out of PlanConfig.cs when CH1.2 took that file past the architecture ratchet 500-line
/// ceiling, the same way the consult blocks were split out before it.
/// </summary>
public sealed partial class PlanConfig
{
    internal void Validate()
    {
        var errors = CollectErrors();
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid plan config:\n  - " + string.Join("\n  - ", errors));
    }

    /// <summary>Gathers configuration problems without throwing, so both <see cref="Load"/> (fail-fast)
    /// and the Options validator (<c>IValidateOptions&lt;PlanConfig&gt;</c>, validated on host start, B2.5)
    /// share one source of truth.</summary>
    internal List<string> CollectErrors()
    {
        var errors = new List<string>();

        // Schema version check (B1.6). A plan with no `version` deserialises to the "1.0" default
        // (back-compat), so only an explicit unsupported value is rejected.
        if (Version != "1.0")
            errors.Add($"plan.version is '{Version}' but only \"1.0\" is supported — upgrade the plan or set version to \"1.0\"");

        if (string.IsNullOrWhiteSpace(Repo)) errors.Add("plan.repo is empty — set it to the repository dir: an absolute path, or (CH1.2) a path relative to the plan file, such as ../.. from plans/<era>/");
        else if (!Directory.Exists(Repo)) errors.Add($"plan.repo '{Repo}' does not exist — create the dir or correct the path"
            + (RepoAsWritten is null ? "" : $" (resolved from '{RepoAsWritten}', relative to the plan file in {PlanDir})"));
        else if (!File.Exists(TrackerPath)) errors.Add($"plan.tracker '{Tracker}' not found at {TrackerPath} — create the file or correct path/repo");

        errors.AddRange(PermissionsConfig.CollectPostureErrors(Agent, Stages)); // KS7.1

        if (Stages.Count == 0) errors.Add("plan.stages is empty — define at least one stage with id, title, and sessions");
        else
        {
            var dupes = Stages.GroupBy(s => s.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0) errors.Add($"duplicate stage ids: {string.Join(", ", dupes)} — each stage must have a unique id");
            foreach (var s in Stages)
            {
                if (string.IsNullOrWhiteSpace(s.Id)) errors.Add("a stage is missing its id — every stage needs an id field");
                else if (s.Id.Length > 20) errors.Add($"stage '{s.Id}' id is too long ({s.Id.Length} chars) — keep ids under 20 chars");
            }

            // B10.1: dependency validation
            var stageIds = Stages.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Stages)
            {
                if (s.DependsOn is not { Count: > 0 }) continue;
                foreach (var dep in s.DependsOn)
                {
                    if (!stageIds.Contains(dep))
                        errors.Add($"stage '{s.Id}' dependsOn '{dep}' which is not a known stage id");
                    if (dep.Equals(s.Id, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"stage '{s.Id}' dependsOn itself — circular self-dependency");
                }
            }

            if (HasDependencyCycle())
                errors.Add("plan.stages has a dependency cycle — fix the dependsOn graph so every stage can eventually become ready");

            // B10.2: parent hierarchy validation
            foreach (var s in Stages)
            {
                if (string.IsNullOrEmpty(s.ParentId)) continue;
                if (s.ParentId.Equals(s.Id, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"stage '{s.Id}' has parentId '{s.ParentId}' which references itself");
                else if (!stageIds.Contains(s.ParentId))
                    errors.Add($"stage '{s.Id}' has parentId '{s.ParentId}' which is not a known stage id");
            }

            if (HasParentCycle())
                errors.Add("plan.stages has a parent hierarchy cycle — fix the parentId chain so no stage is its own ancestor");

            // W1.2 (G13): inline declared work must cover real stages. An inline checkpoint whose
            // derived stage is not in the plan can never be scheduled — that is an authoring error,
            // caught here rather than as a mid-run surprise. (Tracker-file coverage is checked by
            // `doctor` — the tracker is a generated view and validating it here would deadlock the
            // authoring flow that regenerates it.)
            if (Progress?.Checkpoints is { Count: > 0 } inline)
            {
                var dupCps = inline.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (dupCps.Count > 0)
                    errors.Add($"duplicate progress.checkpoints ids: {string.Join(", ", dupCps)} — each work item needs a unique id");
                foreach (var cp in inline)
                {
                    if (string.IsNullOrWhiteSpace(cp.Id)) { errors.Add("a progress.checkpoints item is missing its id"); continue; }
                    var owner = Conventions.DeriveStageId(cp.Id);
                    if (!stageIds.Contains(owner))
                        errors.Add($"progress.checkpoints item '{cp.Id}' derives stage '{owner}' which is not in plan.stages — fix the id or add the stage");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(Agent.Command)) errors.Add("plan.agent.command is required — set the CLI command used to spawn agent sessions");
        if (Agent.Args.Count == 0) errors.Add("plan.agent.args is empty — add at least a {prompt} placeholder");
        else if (!Agent.Args.Any(a => a.Contains("{prompt}", StringComparison.Ordinal))) errors.Add("plan.agent.args must contain a {prompt} placeholder — agent won't receive instructions without it");

        errors.AddRange(GateRules.CollectErrors(Gates));

        // B10 trace: reject zero/negative timeouts on hooks (FU-B10-3).
        if (Setup != null && !string.IsNullOrWhiteSpace(Setup.Command) && Setup.TimeoutMinutes < 1)
            errors.Add("plan.setup.timeoutMinutes must be >= 1 (was " + Setup.TimeoutMinutes + ")");
        if (Teardown != null && !string.IsNullOrWhiteSpace(Teardown.Command) && Teardown.TimeoutMinutes < 1)
            errors.Add("plan.teardown.timeoutMinutes must be >= 1 (was " + Teardown.TimeoutMinutes + ")");
        foreach (var s in Stages)
        {
            if (s.PreHook != null && !string.IsNullOrWhiteSpace(s.PreHook.Command) && s.PreHook.TimeoutMinutes < 1)
                errors.Add($"stage '{s.Id}' pre-hook timeoutMinutes must be >= 1 (was {s.PreHook.TimeoutMinutes})");
            if (s.PostHook != null && !string.IsNullOrWhiteSpace(s.PostHook.Command) && s.PostHook.TimeoutMinutes < 1)
                errors.Add($"stage '{s.Id}' post-hook timeoutMinutes must be >= 1 (was {s.PostHook.TimeoutMinutes})");
        }

        // P2: a typo'd QA dial must never silently project to classic behavior on a live run —
        // reject it here so the plan can't load with a dial that looks active but isn't.
        ValidateQaRule(Pipeline?.Qa, "plan.pipeline.qa", errors);
        foreach (var s in Stages)
            ValidateQaRule(s.Qa, $"stage '{s.Id}' qa", errors);

        // SC3.1: a runIf/skipIf token the evaluator doesn't know evaluates to TRUE at runtime
        // (WorkflowEngine.EvaluateCondition's permissive default), so a mis-cased "!gatesgreen"
        // runs a step that was written to be conditional and nothing ever says so. Refuse here.
        if (Workflows is { Count: > 0 })
        {
            foreach (var (name, wf) in Workflows)
            {
                foreach (var step in wf?.Steps ?? [])
                {
                    ValidateCondition(name, step.Id, "runIf", step.RunIf, errors);
                    ValidateCondition(name, step.Id, "skipIf", step.SkipIf, errors);
                    ValidateInertKeys($"workflow '{name}' step '{step.Id}'", step.UnknownFields,
                        WorkflowStep.KnownFields, InertModelHint, errors);
                }
            }
        }

        // SF0.1 / bug 6: an inert key is the trap SC3 was written to kill, and two survived it —
        // `workflowStep.model` and `stage.overrides.model` were declared, settable, round-tripped by
        // `plan save`, and read by nothing. Both are deleted now, so the JSON that used to set them
        // lands in the extension bucket and is REFUSED here, naming the key that does the job.
        foreach (var s in Stages)
            ValidateInertKeys($"stage '{s.Id}' overrides", s.Overrides?.UnknownFields,
                WorkflowOverrides.KnownFields, InertModelHint, errors);

        // SC3.3: a literal brace in authored prose is the trap that killed a 13-hour run at a stage
        // boundary. Prose is substituted into the prompt as a VALUE, so `{model}` in a stage's notes
        // is not a variable and never was — it either reaches the agent as a broken instruction or
        // (before this landed) took the engine down with a stderr-only refusal. Refuse it here,
        // where doctor reports it as a plan check and the author can still fix it for free.
        foreach (var s in Stages)
            ValidateProse($"stage '{s.Id}' notes", s.Notes, errors);
        ValidateProse("plan.promptExtra", PromptExtra, errors);

        // SC3.4: an advisor that cannot answer is worse than no advisor. Every consult still spawns
        // it, waits out its timeout and then falls back to the deterministic default, saying so in
        // one grey log line nobody reads — so the plan looks like it has a second brain for as long
        // as the run lasts. Judge it here, where the author can still fix it for free.
        ValidateAdvisor(Advisor, errors);

        // KS4.5: the same judgement, for the judge block — a review nobody can read is worth exactly
        // what an advisor nobody can hear is worth, and both are found for free here.
        ValidateJudge(Judge, errors);

        // KS11.2 / CHAPAR CH-2: a chat profile the engine cannot read is refused BY NAME here, at
        // plan load, and never quietly read as admin. Getting this wrong means an outsider holding
        // the steering wheel, so it fails the same way an unknown github.board does.
        if (Telegram?.ProfileRefusal() is { } chatRefusal) errors.Add(chatRefusal);
        // DV3.3: a nonsense transcribe dial is refused at load rather than discovered when the first
        // voice note arrives - which, for a machine nobody is watching, is the worst possible moment.
        if (Courier?.Refusal() is { } courierRefusal) errors.Add(courierRefusal);
        // DV5.2: same posture for the cloud lane - a nonsense timeout is refused at load, not
        // discovered by a lane that hangs somewhere nothing is watching it.
        if (Cloud?.Refusal() is { } cloudRefusal) errors.Add(cloudRefusal);

        return errors;
    }

    private static void ValidateProse(string where, string? prose, List<string> errors)
    {
        var tokens = PromptPlaceholders.UnresolvableIn(prose);
        if (tokens.Count == 0) return;
        errors.Add($"{where} contains {string.Join(", ", tokens)} — prose is substituted as a value, so no " +
                   $"placeholder in it is ever resolved. Write {PromptPlaceholders.Escaped(tokens[0])} for a literal brace, or remove it");
    }

    private static void ValidateCondition(string workflow, string stepId, string field, string? expr, List<string> errors)
    {
        if (expr is null) return; // absent is the normal case — only a written condition is judged
        if (ConditionVocabulary.Validate(expr) is not { } why) return;
        errors.Add($"workflow '{workflow}' step '{stepId}' {field} '{expr}' {why} — valid: {ConditionVocabulary.Describe()}");
    }

    private static void ValidateQaRule(QaRule? qa, string where, List<string> errors)
    {
        if (qa is null) return;
        if (!DefaultQaPolicy.IsValidMode(qa.Mode))
            errors.Add($"{where}.mode is '{qa.Mode}' — use off, everySession, or phaseGate");
        if (qa.VerifierThreshold is { } t && t is < 1 or > 100)
            errors.Add($"{where}.verifierThreshold must be 1–100 (was {t})");
    }

    private bool HasDependencyCycle()
    {
        // Standard DFS cycle detection on the dependsOn graph.
        var ids = Stages.Select(s => s.Id).ToList();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Dfs(string id)
        {
            if (onStack.Contains(id)) return true;
            if (!visited.Add(id)) return false;
            onStack.Add(id);
            var stage = Stages.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (stage?.DependsOn != null)
            {
                foreach (var dep in stage.DependsOn)
                {
                    if (Dfs(dep)) return true;
                }
            }
            onStack.Remove(id);
            return false;
        }

        return ids.Any(id => Dfs(id));
    }

    /// <summary>B10.2: DFS cycle detection on the parentId graph. A cycle exists when walking parent
    /// chains leads back to a previously visited node.</summary>
    private bool HasParentCycle()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Dfs(string id)
        {
            if (onStack.Contains(id)) return true;
            if (!visited.Add(id)) return false;
            onStack.Add(id);
            var stage = Stages.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (stage?.ParentId is { Length: > 0 } parent)
            {
                if (Dfs(parent)) return true;
            }
            onStack.Remove(id);
            return false;
        }

        return Stages.Any(s => Dfs(s.Id));
    }
}
