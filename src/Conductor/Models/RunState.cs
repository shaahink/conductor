using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Models;

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
    /// <summary>G3.3: the run is parked because <c>limits.maxSessions</c> was reached. Distinguishes a
    /// cap-park from an operator pause so a live plan reload that raises/clears the cap can auto-resume
    /// exactly this park and no other.</summary>
    public bool ParkedBySessionCap { get; set; }
    public string? AttentionReason { get; set; }
    /// <summary>SC2.2: the instant <see cref="AttentionReason"/> was raised. Without it the reason is a
    /// sticky sentence with no age — a park from four hours ago and one from four seconds ago read
    /// identically on status, the report and Telegram. Stamped and cleared together with the reason by
    /// <see cref="SetAttention"/>; null on a state.json written before SC2.2.</summary>
    public DateTime? AttentionSinceUtc { get; set; }
    public HashSet<string> SkippedStages { get; set; } = new(StringComparer.Ordinal);
    public PendingFix? PendingFix { get; set; }
    public PendingVerify? PendingVerify { get; set; }
    public PendingResume? PendingResume { get; set; }
    public PendingPhaseGate? PendingPhaseGate { get; set; }
    public PendingAudit? PendingAudit { get; set; }
    /// <summary>M4.1: checkpoint ids claimed by the last deliver session, awaiting engine confirmation
    /// after green gates + verifier pass. Cleared once confirmed or on retry.</summary>
    public List<string> PendingConfirmation { get; set; } = new();
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
    /// <summary>Per-stage current workflow step index (0-based). Used by WorkflowEngine to resume
    /// from the correct step after a crash or restart (M3.1). Key = stage id.</summary>
    public Dictionary<string, int> WorkflowStepIndices { get; set; } = new(StringComparer.Ordinal);
    /// <summary>When true, gates are skipped for the current stage (per-stage override, M3.2).
    /// Reset at stage-enter; read by SessionRunner before running the gate battery.</summary>
    public bool SkipGatesThisStage { get; set; }
    /// <summary>When true, commits are not required for the current stage (per-stage override, M3.2).
    /// Reset at stage-enter; read by VerdictEngine when evaluating session outcomes.</summary>
    public bool SkipCommitThisStage { get; set; }
    /// <summary>When true, verification is advisory-only for the current stage (M3.2).
    /// Reset at stage-enter; read by VerdictEngine when deciding whether to queue PendingVerify.</summary>
    public bool SkipVerificationThisStage { get; set; }
    /// <summary>P5: the session-scoped rollover override — the "this run only" knob layered over
    /// <c>limits.maxSessionTokens</c> by the <c>set-rollover</c> control verb, NEVER by a plan-file
    /// write. null = the plan decides; 0 = rollover forced OFF this run; &gt;0 = the per-session
    /// token cap this run. Lives in run state so it evaporates when the run ends, by design.</summary>
    public long? MaxSessionTokensThisRun { get; set; }
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

    /// <summary>SC2.2: the attempt number the NEXT session on this stage will report. <see
    /// cref="AttemptsThisStage"/> counts attempts already spent, so every line that announces a session
    /// it is about to queue must say this — the phase-gate RED line said <c>attempt 1/2</c> three
    /// seconds before the session it queued announced itself as <c>attempt 2/2</c> (devcontext #19).</summary>
    [JsonIgnore]
    public int NextAttemptNumber => AttemptsThisStage + 1;

    /// <summary>SC2.2: raise or clear the attention reason and its timestamp together, so no surface can
    /// show a reason without an age or an age without a reason. Pass null to clear.</summary>
    public void SetAttention(string? reason, DateTime? nowUtc = null)
    {
        AttentionReason = reason;
        AttentionSinceUtc = reason == null ? null : (nowUtc ?? DateTime.UtcNow);
    }

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
