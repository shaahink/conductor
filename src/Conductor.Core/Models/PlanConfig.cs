using Conductor.Core.Store;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Conductor.Models;

/// <summary>The per-mega-plan configuration file (e.g. plans/loom.plan.json).</summary>
public sealed partial class PlanConfig
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
    /// <summary>KS4.5: the optional advisory second-model review. Off unless present and enabled, and
    /// deliberately NOT part of <see cref="Advisor"/>: the advisor's answer moves the run, the judge's
    /// answer is only ever recorded.</summary>
    public JudgeConfig? Judge { get; set; }
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
    /// <summary>KS4.1: path to a JSON array of <see cref="GateConfig"/> loaded as HOLDOUT gates —
    /// engine-only checks the agent never sees. Relative to the plan file's dir, and it must be
    /// OUTSIDE the repo working tree: see <see cref="HoldoutGateSource"/> for why that is the point.</summary>
    public string? HoldoutGates { get; set; }
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
    /// <summary>DV3.3 — the courier block: how this machine turns a voice note into text, and (from
    /// DV4) the daemon that receives one. null → transcription is not configured, which is a
    /// supported state: a voice note still files, with its audio, and the reply says it was not
    /// transcribed.</summary>
    public CourierConfig? Courier { get; set; }
    /// <summary>DV5.2 / findings §2.3 CL-1 — the cloud lane. null (the default, and what every
    /// existing plan carries) → the engine never spawns cloud work at all. Even non-null it is off
    /// until <see cref="CloudLaneConfig.Enabled"/> says otherwise: out there there is no meter, no
    /// stall watchdog and no control plane, so the lane is asked for, never assumed.</summary>
    public CloudLaneConfig? Cloud { get; set; }

    /// <summary>KS9.1: push-only GitHub mirror of the board and the diary. null (the default, and what
    /// every existing plan carries) → the mirror does not exist. Nothing inbound, ever — see
    /// <see cref="GithubConfig"/>.</summary>
    public GithubConfig? Github { get; set; }
    /// <summary>SF5.2: the babysitter <c>conductor watch</c> invokes on wake, with the brief on stdin.
    /// null → nothing runs on wake unless <c>--hook</c> says so.</summary>
    public SupervisorConfig? Supervisor { get; set; }
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
    // KS3.3: `mutatingLanes` used to be declared here. It was parsed, round-tripped by every editor,
    // documented with a seven-field table — and read by no code path in src/, for the whole life of
    // the field. The Tier B machinery it appeared to configure (MutatingLaneRunner, worktree
    // isolation, the merge gate) is real but reachable from exactly one direction:
    // LaneCoordinator.FollowupEntryToMutatingLane, built from .conductor/followups.md after a stage
    // confirms. A plan that declared the block got silence. Removing the property is what makes the
    // silence audible: the key now resolves to nothing, so `plan set` refuses it and `doctor` names
    // it as inert instead of the plan claiming a feature it never had.
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
    /// <summary>The per-run scratch and discovery directory INSIDE the working tree: logs,
    /// transcripts, evidence, <c>control-plane.json</c>, the engine lock, and the tracked
    /// deliverables (<c>REPORT.md</c>, <c>followups.md</c>, <c>handovers/</c>). K3.1 took only
    /// <c>run.db</c> out of it — see <see cref="RunDbPath"/>.</summary>
    [JsonIgnore] public string StateDir => Path.Combine(Repo, StateHome.ScratchDirName);

    private StateResolution? _state;

    /// <summary>K3.1: where this plan's history lives — a machine-level home keyed by repo path plus
    /// plan name, not <c>&lt;repo&gt;/.conductor/run.db</c>. Resolved once per loaded plan; the
    /// first resolution imports a pre-K3.1 database if one is sitting in the working tree.</summary>
    [JsonIgnore] public string RunDbPath => ResolveState().RunDbPath;

    /// <summary>The full resolution — path, which precedence rule produced it, and what (if
    /// anything) was imported. Commands that report on state (<c>doctor</c>) want all three;
    /// everything else wants <see cref="RunDbPath"/>.</summary>
    public StateResolution ResolveState() => _state ??= StateHome.Resolve(Repo, Name);
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
        // KS4.1: holdout gates join plan.Gates here, and the location rule keeping their commands out
        // of the agent's working tree is enforced BEFORE Validate, so a plan breaking it never runs.
        HoldoutGateSource.Apply(cfg);
        cfg.Validate();
        return cfg;
    }

    /// <summary>KS3.2 — persist through the parse-preserving writer: only what actually changed is
    /// spliced into the file, so `//` comments, key order, formatting and defaults-the-file-never-
    /// carried all survive every <c>add-stage</c>, import apply and Face edit. This used to be a
    /// whole-file re-serialisation, which dropped every comment and materialised every default.</summary>
    public void Save()
    {
        BumpVersion();
        Core.Planning.PlanDocumentEditor.Save(this);
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
