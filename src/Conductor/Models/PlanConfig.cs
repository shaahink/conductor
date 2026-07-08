using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Conductor.Models;

/// <summary>The per-mega-plan configuration file (e.g. plans/loom.plan.json).</summary>
public sealed class PlanConfig
{
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
    public string TemplatesDir { get; set; } = "templates";
    public string PromptExtra { get; set; } = "";

    [JsonIgnore] public string PlanFilePath { get; private set; } = "";
    [JsonIgnore] public string PlanDir => Path.GetDirectoryName(PlanFilePath) ?? ".";
    [JsonIgnore] public string StateDir => Path.Combine(Repo, ".conductor");
    [JsonIgnore] public string TrackerPath => Path.Combine(Repo, Tracker);
    [JsonIgnore] public bool PerPhaseGates => GatePolicy.Equals("perPhase", StringComparison.OrdinalIgnoreCase);

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
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Repo) || !Directory.Exists(Repo)) errors.Add($"repo not found: '{Repo}'");
        else if (!File.Exists(TrackerPath)) errors.Add($"tracker not found: {TrackerPath}");
        if (Stages.Count == 0) errors.Add("no stages defined");
        if (string.IsNullOrWhiteSpace(Agent.Command)) errors.Add("agent.command missing");
        if (!Agent.Args.Any(a => a.Contains("{prompt}"))) errors.Add("agent.args must contain a {prompt} placeholder");
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid plan config:\n  - " + string.Join("\n  - ", errors));
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
        => words.Exists(w => status.StartsWith(w, StringComparison.OrdinalIgnoreCase));

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
    /// <summary>"stream-json" (claude) or "text" (opencode etc.)</summary>
    public string Output { get; set; } = "stream-json";
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
}
