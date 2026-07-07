using System.Text.Json;
using System.Text.Json.Serialization;

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
    public List<StageConfig> Stages { get; set; } = new();
    public List<GateConfig> Gates { get; set; } = new();
    public LimitsConfig Limits { get; set; } = new();
    public ReportConfig Report { get; set; } = new();
    public NotifyConfig? Notify { get; set; }
    public string TemplatesDir { get; set; } = "templates";
    public string PromptExtra { get; set; } = "";

    [JsonIgnore] public string PlanFilePath { get; private set; } = "";
    [JsonIgnore] public string PlanDir => Path.GetDirectoryName(PlanFilePath) ?? ".";
    [JsonIgnore] public string StateDir => Path.Combine(Repo, ".conductor");
    [JsonIgnore] public string TrackerPath => Path.Combine(Repo, Tracker);

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

public sealed class AdvisorConfig
{
    public bool Enabled { get; set; } = true;
    public string Command { get; set; } = "claude";
    public List<string> Args { get; set; } = new();
    /// <summary>"text" or "json" (claude -p --output-format json envelope)</summary>
    public string Output { get; set; } = "text";
    public int TimeoutMinutes { get; set; } = 6;
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
    public int TimeoutMinutes { get; set; } = 20;
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
}

public sealed class NotifyConfig
{
    /// <summary>Command run on needs-attention / completion. Placeholders in args: {message}</summary>
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();
}
