using System.Text.Json;

namespace Conductor.Models;

public enum RunStatus { Idle, Running, VerifyingGates, Backoff, Paused, NeedsHuman, Completed, Aborted }

public enum SessionKind { Deliver, Fix, Resume }

public enum SessionOutcome
{
    Advanced,      // gates green, new commits, >=1 checkpoint newly DONE
    Progress,      // gates green, new commits, no new DONE (multi-session stage — fine)
    NoProgress,    // gates green but nothing committed
    GatesRed,      // gates failed after the session
    Stalled,       // no output for stallMinutes — killed
    TimedOut,      // exceeded sessionTimeoutMinutes — killed
    AgentError,    // agent process exited with an error result
    LimitBackoff,  // usage/rate limit detected — waiting it out
    KilledByUser,
    Interrupted,   // conductor itself was killed mid-session (recovered on restart)
}

public sealed class SessionRecord
{
    public int Number { get; set; }
    public string Stage { get; set; } = "";
    public SessionKind Kind { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public SessionOutcome? Outcome { get; set; }
    public string ClaudeSessionId { get; set; } = "";
    public int ResumeCount { get; set; }
    public List<string> NewCommits { get; set; } = new();
    public List<string> NewlyDone { get; set; } = new();
    public string GateSummary { get; set; } = "";
    public decimal? CostUsd { get; set; }
    public int? NumTurns { get; set; }
    public string ResultSummary { get; set; } = "";
}

public sealed class PendingFix
{
    public int FromSession { get; set; }
    public string GateFailures { get; set; } = "";
    public string ProgressSummary { get; set; } = "";
}

public sealed class PendingResume
{
    public int FromSession { get; set; }
    public string ClaudeSessionId { get; set; } = "";
    public string Reason { get; set; } = "";
    public int ResumeCount { get; set; }
}

public sealed class RunState
{
    public string PlanName { get; set; } = "";
    public RunStatus Status { get; set; } = RunStatus.Idle;
    public string? CurrentStage { get; set; }
    public int SessionCounter { get; set; }
    public int AttemptsThisStage { get; set; }
    public int ConsecutiveBackoffs { get; set; }
    public bool StopAfterSession { get; set; }
    public string? AttentionReason { get; set; }
    public List<string> SkippedStages { get; set; } = new();
    public PendingFix? PendingFix { get; set; }
    public PendingResume? PendingResume { get; set; }
    public List<SessionRecord> History { get; set; } = new();
    public DateTime? UpdatedUtc { get; set; }

    public decimal TotalCostUsd => History.Sum(h => h.CostUsd ?? 0m);

    public static RunState LoadOrNew(string path, string planName)
    {
        if (File.Exists(path))
        {
            try
            {
                var s = JsonSerializer.Deserialize<RunState>(File.ReadAllText(path), PlanConfig.JsonOpts);
                if (s != null) return s;
            }
            catch (JsonException)
            {
                // corrupt state — keep a copy, start fresh rather than dying
                File.Copy(path, path + ".corrupt", overwrite: true);
            }
        }
        return new RunState { PlanName = planName };
    }

    public void Save(string path)
    {
        UpdatedUtc = DateTime.UtcNow;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, PlanConfig.JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }
}
