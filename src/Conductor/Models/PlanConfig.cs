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
    /// <summary>"perSession" (full battery after every session) or "perPhase" (fast-tier gates per
    /// session, full battery only when a stage's checkpoints are all DONE). Default perSession.</summary>
    public string GatePolicy { get; set; } = "perSession";
    public AuditConfig? Audit { get; set; }
    /// <summary>On-demand read-only "what's the status?" agent (dashboard `G` key). Default null → disabled.</summary>
    public StatusAgentConfig? StatusAgent { get; set; }
    public LimitsConfig Limits { get; set; } = new();
    public ReportConfig Report { get; set; } = new();
    public NotifyConfig? Notify { get; set; }
    public TelegramConfig? Telegram { get; set; }
    public string TemplatesDir { get; set; } = "templates";
    public string PromptExtra { get; set; } = "";
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

    [JsonIgnore] public string PlanFilePath { get; private set; } = "";
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

    private void Validate()
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
            var dupes = Stages.GroupBy(s => s.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
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
        else if (!Agent.Args.Any(a => a.Contains("{prompt}"))) errors.Add("plan.agent.args must contain a {prompt} placeholder — agent won't receive instructions without it");

        if (Gates.Any(g => string.IsNullOrWhiteSpace(g.Command)))
            errors.Add("a gate is missing its command — every gate needs a shell command to run");

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

/// <summary>Progress-provider selection + config (B1.3, D-2). `Kind` picks the implementation; the
/// nested blocks configure the non-default providers. The default `markdown-table` needs no config —
/// it reads <see cref="PlanConfig.TrackerPath"/> exactly as Conductor always has.</summary>
public sealed class ProgressConfig
{
    /// <summary>"markdown-table" (default), "script", or "plan-checkpoints".</summary>
    public string Kind { get; set; } = "markdown-table";

    /// <summary>Config for the `script` provider (runs a command that prints checkpoint JSON).</summary>
    public ScriptProviderConfig? Script { get; set; }

    /// <summary>Checkpoints declared inline in the plan (the `plan-checkpoints` provider).</summary>
    public List<PlanCheckpoint>? Checkpoints { get; set; }
}

/// <summary>Per-plan progress conventions (B1.4, R1.3). Every default reproduces Loom's original
/// hard-coded behaviour byte-for-byte; a plan targeting a differently-shaped tracker overrides only
/// what differs. The type also assembles the tracker regexes so the markdown-table provider has a
/// single, configurable source of truth for the row/handoff shapes (F-1).</summary>
public sealed class ProgressConventions
{
    /// <summary>Shared, unmodified defaults (Loom's conventions), used by the static parse facade and
    /// by any <c>CheckpointRow</c> built without explicit conventions.</summary>
    public static ProgressConventions Default { get; } = new();

    /// <summary>ReDoS guard applied to every tracker regex (MA0009, ADR-0001 FU-B0-3). The tracker is
    /// untrusted input, so a pathological pattern can never hang the run.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Regex matching a checkpoint id; an optional <c>stage</c> named group yields the owning
    /// stage id. Default matches Loom/Baton ids (<c>L0.1</c>, <c>B1.4</c>) with stage = the part before
    /// the first dot. Shamshir sets <c>(?&lt;stage&gt;[A-Za-z]+-?\d+)(?:\.\d+)?[a-z]?</c> to admit the
    /// irregular <c>P-0</c>/<c>P3.4b</c>/<c>F5</c> ids.</summary>
    public string StageIdPattern { get; set; } = @"(?<stage>[A-Za-z]+\d+)(?:\.\d+)?[a-z]?";

    /// <summary>Heading opening the handoff block (default <c>## Handoff</c>).</summary>
    public string HandoffMarker { get; set; } = "## Handoff";

    /// <summary>Token an agent writes in the handoff to request a human decision (default <c>HUMAN:</c>).</summary>
    public string HumanToken { get; set; } = "HUMAN:";

    /// <summary>Status keywords grouped by meaning; a cell is classified by leading-keyword prefix
    /// (trailing decoration like <c>DONE ✅</c> is ignored).</summary>
    public StatusVocabulary Status { get; set; } = new();

    private Regex? _stageRx;

    /// <summary>Owning stage for a checkpoint id, honouring <see cref="StageIdPattern"/>'s <c>stage</c>
    /// group when present, else Loom's split-on-first-dot fallback.</summary>
    public string DeriveStageId(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        _stageRx ??= new Regex("^(?:" + StageIdPattern + ")", RegexOptions.IgnoreCase, RegexTimeout);
        if (_stageRx.Match(id) is { Success: true } m && m.Groups["stage"] is { Success: true, Length: > 0 } g)
            return g.Value;
        return id.Split('.')[0];
    }

    public bool IsDone(string status) => StartsWithAny(status, Status.Done);
    public bool IsBlocked(string status) => StartsWithAny(status, Status.Blocked);
    public bool IsInProgress(string status) => StartsWithAny(status, Status.InProgress);

    /// <summary>Does the handoff block ask for a human decision (<see cref="HumanToken"/>)?</summary>
    public bool MentionsHuman(string handoff)
        => !string.IsNullOrEmpty(HumanToken) && handoff.Contains(HumanToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>The per-line checkpoint-row regex for the markdown-table provider, assembled from the
    /// id pattern + status vocabulary. With the defaults this is equivalent to Conductor's original
    /// hard-coded row regex.</summary>
    internal Regex BuildRowRegex()
    {
        var statusAlt = string.Join("|", Status.All()
            .OrderByDescending(w => w.Length)
            .Select(ToWordRegex));
        var pattern =
            @"^\|\s*(?<id>" + StageIdPattern + @")\s*\|(?<title>[^|]*)\|\s*(?<status>" +
            statusAlt + @")(?<rest>[^|]*)\|(?<commit>[^|]*)\|(?<evidence>[^|]*)\|";
        return new Regex(pattern, RegexOptions.IgnoreCase, RegexTimeout);
    }

    /// <summary>Regex extracting the handoff block body (from <see cref="HandoffMarker"/> to the next
    /// level-2 heading or end of file).</summary>
    internal Regex BuildHandoffRegex()
    {
        var pattern = "^" + ToMarkerRegex(HandoffMarker) + @"[^\r\n]*\r?\n(?<body>.*?)(?=^##\s|\z)";
        return new Regex(pattern, RegexOptions.Multiline | RegexOptions.Singleline, RegexTimeout);
    }

    private static bool StartsWithAny(string status, List<string> words)
    {
        // The row regex captures the status keyword with its original inner whitespace (it matches
        // `IN\s+PROGRESS`), so a cell like "IN  PROGRESS" (double space / tab) reaches here verbatim.
        // Collapse whitespace runs on both sides before the prefix test so multi-word keywords still
        // classify — matching the old hard-coded `StartsWith("IN")` intent without its looseness.
        var normalized = CollapseWhitespace(status);
        return words.Exists(w => normalized.StartsWith(CollapseWhitespace(w), StringComparison.OrdinalIgnoreCase));
    }

    // Collapse every run of whitespace to a single space; returns the input unchanged when it holds no
    // consecutive/irregular whitespace, so the common single-space path allocates nothing new.
    private static string CollapseWhitespace(string s)
    {
        var needsWork = s.Contains("  ", StringComparison.Ordinal);
        for (var i = 0; !needsWork && i < s.Length; i++)
            needsWork = char.IsWhiteSpace(s[i]) && s[i] != ' ';   // tab/newline/etc → normalise to space
        if (!needsWork) return s;

        var sb = new StringBuilder(s.Length);
        var prevWs = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevWs) sb.Append(' ');
                prevWs = true;
            }
            else { sb.Append(ch); prevWs = false; }
        }
        return sb.ToString();
    }

    // Words may contain spaces ("IN PROGRESS"); match any run of whitespace between tokens so a
    // double-space or tab in the cell still classifies (matches the original `IN\s+PROGRESS`).
    private static string ToWordRegex(string word)
        => string.Join(@"\s+", word.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));

    private static string ToMarkerRegex(string marker)
        => string.Join(@"\s*", marker.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
}

/// <summary>Status keywords grouped by meaning (B1.4). Loom's defaults; a plan overrides any group to
/// speak its own vocabulary.</summary>
public sealed class StatusVocabulary
{
    public List<string> Done { get; set; } = ["DONE"];
    public List<string> Blocked { get; set; } = ["BLOCKED"];
    public List<string> InProgress { get; set; } = ["IN PROGRESS"];
    public List<string> Todo { get; set; } = ["TODO"];

    internal IEnumerable<string> All() => Done.Concat(Blocked).Concat(InProgress).Concat(Todo);
}

/// <summary>Config for the script progress provider: a plan-owned command whose stdout is a JSON array
/// of checkpoint objects (<c>{ id, title, status, commit, evidence }</c>). Resilient by contract —
/// a missing script or malformed JSON surfaces as a clear error, never a crash (B1.3 trap).</summary>
public sealed class ScriptProviderConfig
{
    /// <summary>PowerShell command line, run in the repo root with real exit-code capture.</summary>
    public string Command { get; set; } = "";
    /// <summary>Working dir relative to repo root (default: repo root).</summary>
    public string? Cwd { get; set; }
    public int TimeoutMinutes { get; set; } = 2;
}

/// <summary>A checkpoint declared inline in the plan JSON (the `plan-checkpoints` provider). Mirrors a
/// tracker row so it folds into the same <c>CheckpointRow</c> contract the engine already consumes.</summary>
public sealed class PlanCheckpoint
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "TODO";
    public string Commit { get; set; } = "";
    public string Evidence { get; set; } = "";
}

public sealed class AgentConfig
{
    public string Command { get; set; } = "claude";
    /// <summary>Placeholders: {prompt} {sessionId}</summary>
    public List<string> Args { get; set; } = new();
    /// <summary>Used to resume a stalled/interrupted agent session. Placeholders: {prompt} {claudeSessionId}</summary>
    public List<string>? ResumeArgs { get; set; }
    /// <summary>"stream-json" (claude) or "text" (opencode etc.). Legacy selector — kept for back-compat;
    /// when <see cref="Provider"/> is unset the provider is inferred from this (B2.4).</summary>
    public string Output { get; set; } = "stream-json";
    /// <summary>Selects the <c>IAgentProvider</c> adapter by name ("opencode", "claude", "text"). When
    /// empty the adapter is inferred from <see cref="Output"/> so existing plans are unchanged (B2.4, D-11).</summary>
    public string? Provider { get; set; }
    /// <summary>Optional system prompt injected before the base prompt (B7 persona templates).</summary>
    public string? SystemPrompt { get; set; }
    /// <summary>Optional model override for this agent (e.g. "claude-sonnet-4-20250514").</summary>
    public string? Model { get; set; }
    /// <summary>Optional sampling temperature (0.0–2.0). null = default.</summary>
    public double? Temperature { get; set; }
    /// <summary>Optional per-session token ceiling (output tokens).</summary>
    public int? TokenCeiling { get; set; }
    /// <summary>Merges an optional override into this config, returning a new instance.
    /// A field whose value equals the C# default (e.g. "claude" for Command) is treated as unset
    /// and falls back to the base value — so a JSON override like {"systemPrompt":"x"} won't
    /// regress a base Command of "opencode" to "claude" (B7.1).</summary>
    public AgentConfig Merge(AgentConfig? o)
    {
        if (o == null) return this;
        var def = new AgentConfig();
        var m = new AgentConfig
        {
            Command = !string.IsNullOrWhiteSpace(o.Command) && o.Command != def.Command ? o.Command : Command,
            Args = o.Args.Count > 0 ? o.Args : Args,
            ResumeArgs = o.ResumeArgs is { Count: > 0 } ? o.ResumeArgs : ResumeArgs,
            Output = !string.IsNullOrWhiteSpace(o.Output) && o.Output != def.Output ? o.Output : Output,
            Provider = !string.IsNullOrWhiteSpace(o.Provider) ? o.Provider : Provider,
            SystemPrompt = o.SystemPrompt ?? SystemPrompt,
            Model = o.Model ?? Model,
            Temperature = o.Temperature ?? Temperature,
            TokenCeiling = o.TokenCeiling ?? TokenCeiling,
        };
        return m;
    }
}

public sealed class HookConfig
{
    /// <summary>PowerShell command line, run with real exit-code capture. Best-effort: a nonzero exit never blocks the run.</summary>
    public string Command { get; set; } = "";
    /// <summary>Working dir relative to repo root (default: repo root).</summary>
    public string? Cwd { get; set; }
    public int TimeoutMinutes { get; set; } = 3;
}

public sealed class AdvisorConfig
{
    public bool Enabled { get; set; } = true;
    public string Command { get; set; } = "claude";
    public List<string> Args { get; set; } = new();
    /// <summary>"text" or "json" (claude -p --output-format json envelope)</summary>
    public string Output { get; set; } = "text";
    public int TimeoutMinutes { get; set; } = 6;
}

/// <summary>On-demand "what's the status?" agent (dashboard `G` key). Runs read-only: all context is
/// embedded in the prompt and it executes in a scratch cwd, so it can't touch the working repo.</summary>
public sealed class StatusAgentConfig
{
    public bool Enabled { get; set; } = true;
    public string Command { get; set; } = "opencode";
    /// <summary>Placeholders: {prompt}</summary>
    public List<string> Args { get; set; } = new() { "run", "{prompt}", "-m", "deepseek/deepseek-v4-pro" };
    public int TimeoutMinutes { get; set; } = 5;
}

public sealed class StageConfig
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Expected session count from the plan doc; attempt budget = sessions * limits.stageSlackFactor.</summary>
    public int Sessions { get; set; } = 2;
    /// <summary>Optional stage-specific text appended to the session prompt.</summary>
    public string? Notes { get; set; }
    /// <summary>If true, the stage parks at <c>AwaitingOwner</c> even when green — the owner must
    /// approve before the orchestrator advances past it (B3.2).</summary>
    public bool OwnerGate { get; set; }
    /// <summary>Per-stage agent override (B7.1) — merged over the plan default. null = use plan default.
    /// The orchestrator resolves checkpoint ?? stage ?? plan default at session-start time.</summary>
    public AgentConfig? Agent { get; set; }
    /// <summary>Persona name to adopt for this stage (e.g. "architect", "planner", "qa"). Resolved
    /// by <c>PersonaRegistry</c> into a system prompt. null = no persona (B7.2).</summary>
    public string? Persona { get; set; }
    /// <summary>Stage kind: "deliver" (default), "review" (self-review stage, B8.3).
    /// A review stage produces an advisory artifact, not mutations.</summary>
    public string Kind { get; set; } = "deliver";
    /// <summary>Stage IDs that must be completed (confirmed or skipped) before this stage becomes
    /// ready. Execution stays sequential; this only affects readiness ordering (B10.1).</summary>
    public List<string>? DependsOn { get; set; }
    /// <summary>Parent stage id for hierarchical display in tree + report (B10.2). null = root stage.
    /// Parent stages appear above their children in the plan tree with indentation.</summary>
    public string? ParentId { get; set; }
    /// <summary>Optional hook that runs before the stage's first session (B10.3). A non-zero exit
    /// blocks the stage and requests human attention.</summary>
    public HookConfig? PreHook { get; set; }
    /// <summary>Optional hook that runs after the stage is confirmed (B10.3). Best-effort: a non-zero
    /// exit is logged but never blocks completion.</summary>
    public HookConfig? PostHook { get; set; }
}

public sealed class GateConfig
{
    public string Name { get; set; } = "";
    /// <summary>PowerShell command line, run with real exit-code capture.</summary>
    public string Command { get; set; } = "";
    /// <summary>Working dir relative to repo root (default: repo root).</summary>
    public string? Cwd { get; set; }
    /// <summary>Optional gates report but never block.</summary>
    public bool Optional { get; set; }
    /// <summary>Skip the gate while this repo-relative path does not exist yet.</summary>
    public string? SkipIfMissing { get; set; }
    /// <summary>"fast" gates also run per-session under perPhase policy; "full" gates run only at
    /// phase end (and every session under perSession policy). Default "full".</summary>
    public string Tier { get; set; } = "full";
    /// <summary>Gates sharing a truthy parallel flag run concurrently within their battery.</summary>
    public bool Parallel { get; set; }
    /// <summary>If set, this gate only runs while the current stage id is in this list (doc-scoped
    /// gates, e.g. mcp-qa on MCP phases only). Empty/null = runs on every stage.</summary>
    public List<string>? Stages { get; set; }
    public int TimeoutMinutes { get; set; } = 20;

    [JsonIgnore] public bool IsFast => Tier.Equals("fast", StringComparison.OrdinalIgnoreCase);

    public bool AppliesToStage(string? stageId)
        => Stages is not { Count: > 0 } || (stageId != null && Stages.Contains(stageId, StringComparer.OrdinalIgnoreCase));
}

public sealed class AuditConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Max audit sessions per phase before giving up and moving on (or escalating).</summary>
    public int MaxAttempts { get; set; } = 1;
}

public sealed class LimitsConfig
{
    /// <summary>No agent output for this long → session considered stalled and killed (then resumed).</summary>
    public int StallMinutes { get; set; } = 12;
    public int SessionTimeoutMinutes { get; set; } = 240;
    public int MaxResumesPerSession { get; set; } = 2;
    /// <summary>Attempt budget per stage = stage.sessions * this.</summary>
    public int StageSlackFactor { get; set; } = 2;
    /// <summary>Wait time when the agent backend reports a usage/rate limit.</summary>
    public int BackoffMinutes { get; set; } = 30;
    public int MaxBackoffs { get; set; } = 10;
    /// <summary>Maximum total cost (USD) allowed this run before the orchestrator parks at
    /// AwaitingOwner. null = no cap (B3.4).</summary>
    public decimal? MaxRunCostUsd { get; set; }
    /// <summary>Maximum total tokens allowed this run before the orchestrator parks at
    /// AwaitingOwner. null = no cap (B3.4).</summary>
    public long? MaxRunTokens { get; set; }
    /// <summary>If true, the orchestrator parks at <c>AwaitingOwner</c> before each session/commit,
    /// waiting for explicit approval (B3.4).</summary>
    public bool ApprovalMode { get; set; }
    /// <summary>Per-session token budget — a session exceeding this ends <c>RolledOver</c>
    /// with a compact handoff and the next session starts fresh (no attempt burned, B8.5).
    /// null = no per-session limit.</summary>
    public long? MaxSessionTokens { get; set; }
    /// <summary>Soft-break token threshold: when live tokens exceed this (as a fraction of
    /// <c>MaxSessionTokens</c>), the orchestrator injects a "finish current sub-task, write
    /// handoff, end cleanly" nudge signal for the agent (B9.4). Default 0.8 (80%). The
    /// nudge is cooperative — the hard <c>MaxSessionTokens</c> ceiling is the safety net.
    /// Only active when <c>MaxSessionTokens</c> is set. null = 0.8 (default).</summary>
    public double? SoftBreakRatio { get; set; }
}

/// <summary>Opt-in prompt batteries that inject bounded context into every session prompt (B8.5).
/// Each battery is a named, deterministic, byte-capped section. Batteries compose in order.</summary>
public sealed class BatteriesConfig
{
    /// <summary>Include the rolling lessons brief from .conductor/lessons.md.</summary>
    public bool Lessons { get; set; } = true;
    /// <summary>Include a recent-failure digest when the last session didn't verify.</summary>
    public bool RecentFailure { get; set; } = true;
    /// <summary>Max entries to include from lessons (default 3).</summary>
    public int LessonsMaxEntries { get; set; } = 3;
    /// <summary>Total byte cap for the combined battery section in the prompt.</summary>
    public int MaxBytes { get; set; } = 2048;
}

public sealed class ReportConfig
{
    public bool Commit { get; set; } = true;
    public bool Push { get; set; } = true;
    /// <summary>During a long session, rewrite+commit REPORT.md every N minutes with the latest agent
    /// activity so the AFK GitHub view reflects live progress. 0 = only report at session boundaries.</summary>
    public int HeartbeatMinutes { get; set; }
}

public sealed class NotifyConfig
{
    /// <summary>Command run on needs-attention / completion. Placeholders in args: {message}</summary>
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public WebhookNotifyConfig? Webhook { get; set; }
    public WebhookNotifyConfig? Discord { get; set; }
    public WebhookNotifyConfig? Slack { get; set; }
}

public sealed class WebhookNotifyConfig
{
    public string Url { get; set; } = "";
    public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>Telegram bot config for AFK observability + two-way control (B6).
/// Bot token is read from the <c>CONDUCTOR_TELEGRAM_TOKEN</c> environment variable (never committed).</summary>
public sealed class TelegramConfig
{
    /// <summary>Allowed chat IDs; an empty list means no commands are accepted (push-only).
    /// Use numeric IDs (int64 strings) — get them from @userinfobot on Telegram.</summary>
    public List<string> AllowedChatIds { get; set; } = new();

    /// <summary>How often to poll getUpdates when idle (seconds). Default 4.</summary>
    public int PollIntervalSeconds { get; set; } = 4;

    /// <summary>If true, write control.json on callback queries from allowed chats (B6.2).
    /// Default false until B6.2 lands.</summary>
    public bool EnableTwoWay { get; set; }
}
