namespace Conductor.Core;

public enum ControlAction
{
    PauseAfterSession,
    ResumeRun,
    AbortNow,
    SkipStage,
    KillSession,
    StopAfterSession,
    RetryStage,
    Rollback,
    PauseAfterStage,
    Goto,
}

/// <summary>Live status of one gate in the current battery (for the dashboard's gate timers).</summary>
public sealed record GateProgress(string Name, string State, TimeSpan Elapsed, DateTime? StartUtc = null)
{
    // State ∈ pending | running | pass | fail | warn | skip
    public static GateProgress Pending(string name) => new(name, "pending", TimeSpan.Zero);

    /// <summary>Live elapsed: ticks up for a running gate; fixed duration once finished.</summary>
    public TimeSpan LiveElapsed(DateTime nowUtc)
        => State == "running" && StartUtc is { } s ? nowUtc - s : Elapsed;
}

/// <summary>One stage's roll-up for the hierarchical plan tree (B4.3): progress plus the columns
/// derived from run history — attempts, last outcome, cost — and its full checkpoint list so the
/// tree can expand/collapse sub-checkpoints without a second lookup.</summary>
public sealed record StageProgress
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    /// <summary>confirmed | done | gating | active | skipped | todo (same vocabulary as StageOverview).</summary>
    public string State { get; init; } = "todo";
    /// <summary>Sessions this run has spent on the stage (history count).</summary>
    public int Attempts { get; init; }
    /// <summary>Outcome of the most recent finished session on the stage (empty if none yet).</summary>
    public string LastOutcome { get; init; } = "";
    public decimal CostUsd { get; init; }
    public IReadOnlyList<(string Id, string Title, string Status)> Checkpoints { get; init; }
        = Array.Empty<(string, string, string)>();
}

/// <summary>Immutable view of the run for rendering (dashboard / plain log).</summary>
public sealed record DashboardSnapshot
{
    public string PlanName { get; init; } = "";
    public string Status { get; init; } = "";
    public string? AttentionReason { get; init; }
    public string StageId { get; init; } = "";
    public string StageTitle { get; init; } = "";
    public int SessionNumber { get; init; }
    public string SessionKind { get; init; } = "";
    public int Attempt { get; init; }
    public int MaxAttempts { get; init; }
    public TimeSpan SessionElapsed { get; init; }
    public double LastActivityAgoSec { get; init; }
    /// <summary>True while a headless agent session is actively running (vs conductor doing gates/idle).</summary>
    public bool AgentActive { get; init; }
    public int DoneCount { get; init; }
    public int TotalCount { get; init; }
    /// <summary>All-time cost of finished sessions that recorded a cost (History sum).</summary>
    public decimal TotalCostUsd { get; init; }
    /// <summary>Cost of the currently running session (not yet folded into TotalCostUsd).</summary>
    public decimal SessionCostUsd { get; init; }
    /// <summary>Sessions that ran without a recorded cost (e.g. before opencode-json mode) — unrecoverable.</summary>
    public int UntrackedSessions { get; init; }
    public long TokensInput { get; init; }
    public long TokensOutput { get; init; }
    public long TokensReasoning { get; init; }
    /// <summary>Tokens consumed by the currently running session (not yet folded into Tokens*).</summary>
    public long SessionTokensInput { get; init; }
    public long SessionTokensOutput { get; init; }
    public long SessionTokensReasoning { get; init; }
    /// <summary>First not-done checkpoint in the active stage — what the session is working toward.</summary>
    public string CurrentCheckpoint { get; init; } = "";
    /// <summary>Title of the current checkpoint, shown full-width in the header so its intent is clear.</summary>
    public string CurrentCheckpointTitle { get; init; } = "";
    /// <summary>Non-null when a destructive action (A/K/S) is awaiting confirmation. Shown in the footer.</summary>
    public string? ConfirmPrompt { get; init; }
    public int ResumeCount { get; init; }
    public string GateSummary { get; init; } = "";
    /// <summary>Per-gate live status during a battery (empty when no battery is running).</summary>
    public IReadOnlyList<GateProgress> Gates { get; init; } = Array.Empty<GateProgress>();
    public string Branch { get; init; } = "";
    public DateTime? BackoffUntilUtc { get; init; }
    public IReadOnlyList<(string Id, string Title, string Status)> StageCheckpoints { get; init; } = Array.Empty<(string, string, string)>();
    public IReadOnlyList<(string StageId, int Done, int Total, string State)> StageOverview { get; init; } = Array.Empty<(string, int, int, string)>();
    /// <summary>Full per-stage roll-up (progress + attempts/last-outcome/cost + sub-checkpoints) that
    /// drives the hierarchical plan tree (B4.3). Superset of <see cref="StageOverview"/>.</summary>
    public IReadOnlyList<StageProgress> Stages { get; init; } = Array.Empty<StageProgress>();
}

public interface IProgressSink
{
    void Log(string line);
    void AgentEvent(AgentEvent ev);
    void Snapshot(DashboardSnapshot snap);
    ControlAction? PollControl();
    /// <summary>Live per-gate status pushed by the gate runner (no-op for plain sinks).</summary>
    void GateProgress(IReadOnlyList<GateProgress> gates) { }
}
