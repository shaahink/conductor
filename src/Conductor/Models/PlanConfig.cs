using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Conductor.Models;

/// <summary>The per-mega-plan configuration file (e.g. plans/loom.plan.json).</summary>
public sealed class PlanConfig
{
    /// <summary>Schema version. Currently only "1.0" is supported; a plan without a version or
    /// with an unsupported version is rejected with a clear diagnostic (B1.6).</summary>
    public string Version { get; set; } = "1.0";
    /// <summary>P1: Monotonic plan-edit counter, bumped on every modification (set, reload, add-stage).
    /// Starts at 1; the orchestrator can compare this against its loaded value to detect
    /// external edits at session boundaries.</summary>
    public int PlanVersion { get; set; } = 1;
    public string Name { get; set; } = "plan";
    public string Repo { get; set; } = "";
    /// <summary>SC4.3: sibling repositories this plan's work may land in, absolute or relative to
    /// <see cref="Repo"/>. The session verdict diffs each of them for commits alongside the primary
    /// repo, so a checkpoint delivered entirely in a sibling is progress instead of
    /// <c>NoProgress</c> (sk #3 scored it NoProgress twice). Empty = single-repo plan, unchanged.</summary>
    public List<string> SatelliteRepos { get; set; } = [];
    public string Tracker { get; set; } = "";
    public string PlanDoc { get; set; } = "";
    public string? BranchPattern { get; set; }
    public bool PauseOnBlocked { get; set; } = true;
    public AgentConfig Agent { get; set; } = new();
    public AdvisorConfig? Advisor { get; set; }
    /// <summary>Optional clean-slate command run before each agent session and before each gate battery.</summary>
    public HookConfig? Setup { get; set; }
    /// <summary>Optional command run after each gate battery to stop anything the session/gates left running.</summary>
    public HookConfig? Teardown { get; set; }
    /// <summary>Selects and configures the progress provider (B1.3). Default = markdown-table (Loom's
    /// strict TRACKER.md), so existing plans are unchanged. `script` and `plan-checkpoints` are the
    /// escape hatches for projects whose progress isn't a strict markdown table (F-1, D-2).</summary>
    public ProgressConfig Progress { get; set; } = new();
    /// <summary>Per-plan progress conventions (B1.4, R1.3): checkpoint-id shape, handoff marker,
    /// human token, status vocabulary. Defaults reproduce Loom's original behaviour, so existing
    /// plans are unchanged; a differently-shaped tracker (e.g. Shamshir's P-0/P3.4b/F5 ids) overrides
    /// only what differs.</summary>
    public ProgressConventions Conventions { get; set; } = new();
    public List<StageConfig> Stages { get; set; } = new();
    public List<GateConfig> Gates { get; set; } = new();
    /// <summary>"perSession" (full battery every session) or "perPhase" (fast gates/session, full battery at stage-done). Default perSession.</summary>
    public string GatePolicy { get; set; } = "perSession";
    public AuditConfig? Audit { get; set; }
    /// <summary>false: skip the per-delivery Verify and rely on Audit / the full battery (M3 stopgap).
    /// The LOWEST-precedence input to <c>QaPolicyExtensions.EffectiveSkipVerification</c> — a QA dial
    /// or a stage's <c>overrides.skipVerification</c> both outrank it (SF0.1 / bug 11).</summary>
    public bool VerifyEachDelivery { get; set; } = true;
    /// <summary>On-demand read-only "what's the status?" agent (dashboard `G` key). Default null → disabled.</summary>
    public StatusAgentConfig? StatusAgent { get; set; }
    public LimitsConfig Limits { get; set; } = new();
    public ReportConfig Report { get; set; } = new();
    public NotifyConfig? Notify { get; set; }
    public TelegramConfig? Telegram { get; set; }
    public string PromptExtra { get; set; } = "";

    /// <summary>Directory (relative to the plan file) holding the session templates — <c>session.md</c>,
    /// <c>fix.md</c>, <c>verify.md</c>, … — and a <c>packs/</c> subdirectory. Falls back to the plan
    /// directory itself, then to the built-in defaults. Prompts are editable content, not code: this is
    /// what makes the .md files on disk the thing that actually ships to the agent.</summary>
    public string? TemplatesDir { get; set; }

    /// <summary>"Batteries included" domain packs merged into every prompt as <c>{packs}</c>, by name;
    /// each resolves to <c>&lt;templatesDir&gt;/packs/&lt;name&gt;.md</c>. Use them to carry house style and
    /// the mistakes agents habitually make in this codebase's domain (e.g. <c>dotnet-engineer</c>,
    /// <c>modern-csharp</c>, <c>agent-pitfalls</c>) rather than restating them in every stage's notes.</summary>
    public List<string> Packs { get; set; } = [];

    /// <summary>Opt-in prompt batteries for bounded context injection (B8.5). null = none.</summary>
    public BatteriesConfig? Batteries { get; set; }
    /// <summary>When true, the agent is instructed to skip its own pre-session build+test ritual
    /// and defer to Conductor's battery, which remains the single source of truth (B10.4). This
    /// saves ~30-50% of agent-output tokens that were spent echoing build/test output that Conductor
    /// re-runs anyway. Default false (back-compat).</summary>
    public bool BatteryCollapse { get; set; }
    /// <summary>Mandated docs to read in order at session start (paths relative to repo root).
    /// Rendered as an ordered list in the session prompt. Empty/null = no list rendered (B1.5).</summary>
    public List<string>? ReadOrder { get; set; }
    /// <summary>Read-only analysis lanes that run concurrently with sessions (B12.1 Tier A).
    /// Each lane spawns an agent in a scratch temp directory — it can never write the working tree.</summary>
    public List<AnalysisLaneConfig> AnalysisLanes { get; set; } = new();
    /// <summary>Tier B isolated-worktree mutating lanes that run behind a full-battery merge gate
    /// (B12.3). Each lane runs in its own <c>git worktree</c> on a scratch branch; the lane's
    /// changes are only merged into the primary tree if the merge-gate battery is green.</summary>
    public List<MutatingLaneConfig> MutatingLanes { get; set; } = new();
    /// <summary>Plan-level workflow definitions keyed by name. When a stage or the plan references
    /// a workflow name not in this dictionary, the built-in definitions (deliver-verify, etc.) are used
    /// as fallbacks (M3.1).</summary>
    public Dictionary<string, WorkflowDefinition>? Workflows { get; set; }
    /// <summary>The default workflow name used for stages that don't specify one. Falls back to
    /// "deliver-verify" when unset (M3.1).</summary>
    public string? DefaultWorkflow { get; set; }

    /// <summary>P0: the declarative pipeline rules block (<see cref="PipelineRules"/>, owned by
    /// Conductor.Planning). Absent = null = every default reproduces the classic behavior exactly —
    /// an existing plan with no `pipeline` block is byte-for-byte unchanged. P1 populates
    /// roles/multi-item, P2 the QA dial.</summary>
    public PipelineRules? Pipeline { get; set; }

    [JsonIgnore] public string PlanFilePath { get; internal set; } = "";
    [JsonIgnore] public string PlanDir => Path.GetDirectoryName(PlanFilePath) ?? ".";
    [JsonIgnore] public string StateDir => Path.Combine(Repo, ".conductor");
    [JsonIgnore] public string TrackerPath => Path.Combine(Repo, Tracker);
    [JsonIgnore] public bool PerPhaseGates => GatePolicy.Equals("perPhase", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolve the effective agent config for a stage: stage.Agent overrides plan.Agent
    /// field-by-field via <see cref="AgentConfig.Merge"/>. When both are null/empty a fresh default
    /// is returned so callers never dereference null (B7.1).</summary>
    public AgentConfig ResolveAgent(StageConfig stage)
        => Agent?.Merge(stage.Agent) ?? stage.Agent ?? new AgentConfig();

    /// <summary>Resolve the persona name for a stage — stage.Persona falls back to plan-wide
    /// scrape from stage.Notes (the "Persona: X" hint convention that existed before B7 landed).</summary>
    public string? ResolvePersona(StageConfig stage)
    {
        if (!string.IsNullOrWhiteSpace(stage.Persona)) return stage.Persona;
        // Fall back: parse legacy "Persona: architect" hints from notes (pre-B7 convention)
        if (!string.IsNullOrWhiteSpace(stage.Notes))
        {
            var match = System.Text.RegularExpressions.Regex.Match(stage.Notes,
                @"Persona:\s*(?<persona>[\w-]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.ExplicitCapture,
                ProgressConventions.RegexTimeout);
            if (match.Success) return match.Groups["persona"].Value;
        }
        return null;
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static PlanConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Plan file not found: {full}");
        var cfg = JsonSerializer.Deserialize<PlanConfig>(File.ReadAllText(full), JsonOpts)
                  ?? throw new InvalidOperationException($"Plan file is empty: {full}");
        cfg.PlanFilePath = full;
        cfg.Validate();
        return cfg;
    }

    public void Save()
    {
        BumpVersion();
        var json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(PlanFilePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public void BumpVersion() => PlanVersion++;

    public void AddStage(StageConfig stage)
    {
        Stages.Add(stage);
        BumpVersion();
    }

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

        if (string.IsNullOrWhiteSpace(Repo)) errors.Add("plan.repo is empty — set it to the absolute path of the repository dir");
        else if (!Directory.Exists(Repo)) errors.Add($"plan.repo '{Repo}' does not exist — create the dir or correct the path");
        else if (!File.Exists(TrackerPath)) errors.Add($"plan.tracker '{Tracker}' not found at {TrackerPath} — create the file or correct path/repo");

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

        if (Gates.Any(g => string.IsNullOrWhiteSpace(g.Command)))
            errors.Add("a gate is missing its command — every gate needs a shell command to run");

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

        return errors;
    }

    private static void ValidateAdvisor(AdvisorConfig? a, List<string> errors)
    {
        if (a is null) return; // no advisor block is a supported choice — ambiguity takes the default

        // bug 7: an inert key that looks like agent.provider is a lie about which model answers.
        foreach (var key in (a.UnknownFields?.Keys ?? Enumerable.Empty<string>()).OrderBy(k => k, StringComparer.Ordinal))
        {
            var hint = key.Equals("provider", StringComparison.OrdinalIgnoreCase)
                ? " The advisor has no provider adapter: advisor.command plus its args pick the CLI and the model, and advisor.output only says how to unwrap the answer."
                : "";
            errors.Add($"plan.advisor.{key} is not an advisor field — nothing reads it, so it cannot do what it looks like it does. " +
                       $"Known fields: {string.Join(", ", AdvisorConfig.KnownFields)}.{hint}");
        }

        if (!a.Enabled) return; // a disabled advisor is never spawned, so its invocation is moot

        if (string.IsNullOrWhiteSpace(a.Command))
            errors.Add("plan.advisor.command is empty — name the CLI that answers, or set advisor.enabled false");

        if (a.Args.Count == 0)
            errors.Add("plan.advisor.args is empty — a CLI spawned with no arguments is handed no question: it waits on " +
                       $"stdin until advisor.timeoutMinutes expires and answers nothing. Use [\"{string.Join("\", \"", AdvisorConfig.DefaultArgs)}\"] " +
                       "(the shipped default), or set advisor.enabled false");
        else if (!a.Args.Any(x => x.Contains("{prompt}", StringComparison.Ordinal)))
            errors.Add("plan.advisor.args carries no {prompt} placeholder — the advisor would be spawned without the question it is being asked");

        if (!AdvisorConfig.IsKnownOutput(a.Output))
            errors.Add($"plan.advisor.output is '{a.Output}' — use {string.Join(", ", AdvisorConfig.OutputKinds)}. An unknown kind is passed " +
                       "through raw, so a JSON envelope reaches the parser still wrapped and every answer reads as unparseable");

        if (a.TimeoutMinutes < 1)
            errors.Add($"plan.advisor.timeoutMinutes must be >= 1 (was {a.TimeoutMinutes}) — a zero timeout kills the advisor before it can answer");
    }

    /// <summary>SF0.1 / bug 6: the general form of the advisor block's bug-7 check — a key the type
    /// does not declare is named and refused rather than parsed into a bucket nobody reads. The hint
    /// exists because "unknown key" is not the useful half of the message; "here is the key that
    /// really does this" is.</summary>
    private static void ValidateInertKeys(string where, Dictionary<string, JsonElement>? unknown,
        IReadOnlyList<string> known, Func<string, string> hint, List<string> errors)
    {
        foreach (var key in (unknown?.Keys ?? Enumerable.Empty<string>()).OrderBy(k => k, StringComparer.Ordinal))
        {
            errors.Add($"{where}.{key} is not a field here — nothing reads it, so it cannot do what it " +
                       $"looks like it does. Known fields: {string.Join(", ", known)}.{hint(key)}");
        }
    }

    /// <summary>The one inert key both deleted blocks shared, and the only one worth a sentence: a
    /// pinned model that never reached the agent is a plan claiming one model answered while another
    /// one did.</summary>
    private static string InertModelHint(string key) =>
        key.Equals("model", StringComparison.OrdinalIgnoreCase)
            ? " A session's model comes from pipeline.roles.<role>.model, else stage.agent.model, else plan.agent.model —" +
              " set it in one of those."
            : "";

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
