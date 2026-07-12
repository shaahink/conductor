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
    public bool VerifyEachDelivery { get; set; } = true; // false: skip per-Deliver Verify, rely on Audit/full battery (M3 stopgap)
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

        return errors;
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
