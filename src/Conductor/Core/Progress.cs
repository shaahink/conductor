namespace Conductor.Core;

/// <summary>Immutable view of the run for rendering (dashboard / plain log).</summary>
public sealed record DashboardSnapshot
{
    public string PlanName { get; init; } = "";
    public string Status { get; init; } = "";
    public string? AttentionReason { get; init; }
    public string StageId { get; init; } = "";
    public string StageTitle { get; init; } = "";
    /// <summary>Active persona for the current stage/session (B7.3). null = default.</summary>
    public string? Persona { get; init; }
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
    /// <summary>O3: all-time overhead (gate runtime estimate) from finished sessions.</summary>
    public decimal OverheadCostUsd { get; init; }
    /// <summary>O3: overhead cost of the currently running session (not yet folded into OverheadCostUsd).</summary>
    public decimal SessionOverheadCostUsd { get; init; }
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
    public ToastMessage? ActiveToast { get; init; }
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
    /// <summary>Log with severity — default renders as plain text; the dashboard override captures the severity for colour-coded display.</summary>
    void Log(LogEntry entry) { Log(entry.Text); }
    void AgentEvent(AgentEvent ev);
    void Snapshot(DashboardSnapshot snap);
    /// <summary>Returns the next queued control command, or null. Widened from a bare
    /// <see cref="ControlAction"/> (F5 prep) so every ingress — TUI queue, control.json file, and
    /// the HTTP control plane — carries the same payload (stageId/force/etc.) into one dispatcher.</summary>
    ControlCommand? PollControl();
    /// <summary>Live per-gate status pushed by the gate runner (no-op for plain sinks).</summary>
    void GateProgress(IReadOnlyList<GateProgress> gates) { }
    /// <summary>Transient control-action feedback — rendered as a toast in the dashboard.</summary>
    void Toast(ToastMessage toast) { }
}
