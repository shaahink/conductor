using System.Text.Json;

namespace Conductor.Models;

public enum RunStatus { Idle, Running, VerifyingGates, Backoff, Paused, NeedsHuman, AwaitingOwner, Completed, Aborted }

public enum SessionKind { Deliver, Fix, Resume, Audit }

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
    public long? TokensInput { get; set; }
    public long? TokensOutput { get; set; }
    public long? TokensReasoning { get; set; }
    public long? TokensCacheRead { get; set; }
    public int Attempt { get; set; }
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

/// <summary>A stage whose checkpoints are all DONE and now owes a full-battery verification
/// (and, once green, an audit). Persisted so an interrupted phase-gate run is redone on restart.</summary>
public sealed class PendingPhaseGate
{
    public string StageId { get; set; } = "";
    /// <summary>Commit HEAD when the stage's last checkpoint flipped DONE — the audit diffs from here.</summary>
    public string StageStartHead { get; set; } = "";
}

/// <summary>A stage whose full battery is green and now owes an auto-fix audit + honest handover.</summary>
public sealed class PendingAudit
{
    public string StageId { get; set; } = "";
    public string StageStartHead { get; set; } = "";
}

public sealed class RunState
{
    public string PlanName { get; set; } = "";
    /// <summary>Stable id for the logical run, shared by every event in <c>.conductor/events.jsonl</c>
    /// (B2). Generated once, persisted, and reused across restarts so a resumed run keeps one event
    /// stream. Empty on a state.json written before B2 → the orchestrator mints one on first use.</summary>
    public string RunId { get; set; } = "";
    public RunStatus Status { get; set; } = RunStatus.Idle;
    public string? CurrentStage { get; set; }
    /// <summary>Git HEAD when the current stage was first entered — the audit diffs from here.</summary>
    public string? CurrentStageStartHead { get; set; }
    public int SessionCounter { get; set; }
    public int AttemptsThisStage { get; set; }
    public int ConsecutiveBackoffs { get; set; }
    public bool StopAfterSession { get; set; }
    /// <summary>If true, the orchestrator parks at <c>Paused</c> after the current stage's checkpoints
    /// are all confirmed, rather than advancing automatically (B3.3).</summary>
    public bool PauseAfterStage { get; set; }
    public string? AttentionReason { get; set; }
    public List<string> SkippedStages { get; set; } = new();
    public PendingFix? PendingFix { get; set; }
    public PendingResume? PendingResume { get; set; }
    public PendingPhaseGate? PendingPhaseGate { get; set; }
    public PendingAudit? PendingAudit { get; set; }
    /// <summary>Stages whose full battery has passed (and audit completed). SelectStage skips these,
    /// so a stage with red phase-gates is never advanced past even when its tracker rows read DONE.</summary>
    public List<string> ConfirmedStages { get; set; } = new();
    /// <summary>Stages whose auto-fix audit has completed, to avoid re-auditing on resume.</summary>
    public List<string> AuditedStages { get; set; } = new();
    /// <summary>Stages whose owner has explicitly approved via CLI/TUI (B3.2). An owner-gated stage
    /// cannot advance past <see cref="RunStatus.AwaitingOwner"/> until its id appears here.</summary>
    public List<string> OwnerApprovedStages { get; set; } = new();
    public List<SessionRecord> History { get; set; } = new();
    /// <summary>Signature (HEAD sha + gate-set) of the last full battery that passed green — lets the
    /// orchestrator skip re-running an identical battery on an unchanged tree (e.g. across restarts).</summary>
    public string? LastGreenGateSig { get; set; }
    public DateTime? UpdatedUtc { get; set; }

    public decimal TotalCostUsd => History.Sum(h => h.CostUsd ?? 0m);
    public long TotalTokensInput => History.Sum(h => h.TokensInput ?? 0);
    public long TotalTokensOutput => History.Sum(h => h.TokensOutput ?? 0);
    public long TotalTokensReasoning => History.Sum(h => h.TokensReasoning ?? 0);

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
