using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Models;

public enum RunStatus { Idle, Running, VerifyingGates, Backoff, Paused, NeedsHuman, AwaitingOwner, Completed, Aborted }

public enum SessionKind { Deliver, Fix, Resume, Audit, Verify }

/// <summary>Why the run parked at <see cref="RunStatus.AwaitingOwner"/> — decides what an owner
/// approval means (B3.2/B3.4). <c>OwnerGate</c>: the stage is green and confirms on approve.
/// <c>ApprovalMode</c>: parked before a session; approve runs exactly the next session then parks
/// again. <c>Budget</c>: a cost/token cap tripped; approve resets the budget window and continues.</summary>
public enum AwaitingOwnerReason { OwnerGate, ApprovalMode, Budget }

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
    RolledOver,    // session ended cleanly at token budget — handoff written, no attempt burned (B8.5)
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
    /// <summary>O3: estimated overhead (gate runtime) cost for this session, stored as a separate
    /// category from agent API cost so reports and TUI can show "Agent: $X | Gates: $Y".</summary>
    public decimal? OverheadCostUsd { get; set; }
    public int? NumTurns { get; set; }
    public long? TokensInput { get; set; }
    public long? TokensOutput { get; set; }
    public long? TokensReasoning { get; set; }
    public long? TokensCacheRead { get; set; }
    public int Attempt { get; set; }
    public string ResultSummary { get; set; } = "";

    [JsonIgnore] public long TokensTotal =>
        (TokensInput ?? 0) + (TokensOutput ?? 0) + (TokensReasoning ?? 0) + (TokensCacheRead ?? 0);
}

public sealed class PendingFix
{
    public int FromSession { get; set; }
    public string GateFailures { get; set; } = "";
    public string ProgressSummary { get; set; } = "";
    public string VerifierFindings { get; set; } = "";
    public int? VerifierScore { get; set; }
}

public sealed class PendingVerify
{
    public int FromSession { get; set; }
    public string StageId { get; set; } = "";
    public string StageStartHead { get; set; } = "";
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

/// <summary>P2: a stage that will be audited in parallel with the next stage's deliver.
/// The audit runs as a read-only lane against the pinned commit SHA.</summary>
public sealed class PendingParallelAudit
{
    public string StageId { get; set; } = "";
    /// <summary>Commit HEAD when the stage was confirmed — the audit diffs from here.</summary>
    public string StageStartHead { get; set; } = "";
}

/// <summary>P2: severity of an audit finding from a parallel audit lane.</summary>
public enum AuditFindingSeverity
{
    None,
    Low,
    Medium,
    High,
}

/// <summary>P2: outcome of a parallel audit lane that ran concurrently with deliver.</summary>
public sealed class ParallelAuditOutcome
{
    public string StageId { get; set; } = "";
    public AuditFindingSeverity MaxSeverity { get; set; }
    public string Findings { get; set; } = "";
    public bool Completed { get; set; }
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
    public HashSet<string> SkippedStages { get; set; } = new(StringComparer.Ordinal);
    public PendingFix? PendingFix { get; set; }
    public PendingVerify? PendingVerify { get; set; }
    public PendingResume? PendingResume { get; set; }
    public PendingPhaseGate? PendingPhaseGate { get; set; }
    public PendingAudit? PendingAudit { get; set; }
    /// <summary>P2: when a stage is confirmed and the audit will run in parallel with the next
    /// stage's deliver. Cleared when the audit completes.</summary>
    public PendingParallelAudit? PendingParallelAudit { get; set; }
    /// <summary>P2: outcome of the last completed parallel audit. Read by the next deliver session
    /// to inject findings and by the orchestrator to decide whether a fix is needed.</summary>
    public ParallelAuditOutcome? ParallelAuditOutcome { get; set; }
    /// <summary>Stages whose full battery has passed (and audit completed). SelectStage skips these,
    /// so a stage with red phase-gates is never advanced past even when its tracker rows read DONE.</summary>
    public HashSet<string> ConfirmedStages { get; set; } = new(StringComparer.Ordinal);
    /// <summary>P4: stages whose chore(conductor): commits have already been squashed on confirm.
    /// Prevents re-squashing on repeated confirm calls (idempotency).</summary>
    public HashSet<string> SquashedStages { get; set; } = new(StringComparer.Ordinal);
    /// <summary>P4: per-stage start HEAD commit (the commit before any session work for this
    /// stage). Populated by <c>ScheduleGateOrAudit</c> and consumed by the squash-on-confirm
    /// logic so the rebase window is correct even when the owner-approval path defers confirmation.</summary>
    public Dictionary<string, string> StageStartHeads { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Stages whose auto-fix audit has completed, to avoid re-auditing on resume.</summary>
    public HashSet<string> AuditedStages { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Stages whose owner has explicitly approved via CLI/TUI (B3.2). An owner-gated stage
    /// cannot advance past <see cref="RunStatus.AwaitingOwner"/> until its id appears here.</summary>
    public HashSet<string> OwnerApprovedStages { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Stages whose preHook has already executed (B10.3). Prevents re-running the hook on
    /// resume/crash-recovery; a stage id in this list means the preHook succeeded at least once.</summary>
    public HashSet<string> PreHookRunStages { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Why the run is parked at <see cref="RunStatus.AwaitingOwner"/> (B3.2/B3.4). Persisted so
    /// an approval after restart does the right thing — confirm the stage vs. resume a session vs. reset
    /// the budget window. Null when not parked (or a legacy state.json, treated as an owner-gate).</summary>
    public AwaitingOwnerReason? AwaitingOwnerReason { get; set; }
    public List<SessionRecord> History { get; set; } = new();
    /// <summary>Signature (HEAD sha + gate-set) of the last full battery that passed green — lets the
    /// orchestrator skip re-running an identical battery on an unchanged tree (e.g. across restarts).</summary>
    public string? LastGreenGateSig { get; set; }
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>Cumulative run cost accrued since the last budget reset (C3). Survives crashes so a run
    /// killed mid-accrual before it parks still counts toward <c>maxRunCostUsd</c> on restart.</summary>
    public decimal PerRunCostUsd { get; set; }
    /// <summary>Cumulative run tokens accrued since the last budget reset (C3). Same crash-survival
    /// semantics as <see cref="PerRunCostUsd"/>.</summary>
    public long PerRunTokens { get; set; }
    /// <summary>O3: cumulative overhead cost (gate runtime estimate) since the last budget reset.
    /// Same crash-survival semantics as <see cref="PerRunCostUsd"/>.</summary>
    public decimal PerRunOverheadCostUsd { get; set; }

    public decimal TotalCostUsd => History.Sum(h => h.CostUsd ?? 0m);
    public decimal TotalOverheadCostUsd => History.Sum(h => h.OverheadCostUsd ?? 0m);
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
                if (s != null)
                {
                    // The state must belong to the plan we were asked to run. Without this check, starting a
                    // NEW plan in a repo that has run an old one silently adopts the old plan's state — the
                    // new plan opens mid-run at session 33 with a pending resume it knows nothing about,
                    // against stages that merely happen to share an id. The old run is archived, not deleted:
                    // it is the only record of what happened.
                    if (!string.IsNullOrEmpty(s.PlanName) && !string.Equals(s.PlanName, planName, StringComparison.Ordinal))
                    {
                        var archived = Path.Combine(
                            Path.GetDirectoryName(path) ?? ".",
                            $"state.{s.PlanName}.{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
                        File.Move(path, archived, overwrite: true);
                        return new RunState { PlanName = planName };
                    }
                    return s;
                }
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
