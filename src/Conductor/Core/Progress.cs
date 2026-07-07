namespace Conductor.Core;

public enum ControlAction
{
    PauseAfterSession,
    ResumeRun,
    AbortNow,
    SkipStage,
    KillSession,
    StopAfterSession,
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
    public int DoneCount { get; init; }
    public int TotalCount { get; init; }
    public decimal TotalCostUsd { get; init; }
    public decimal SessionCostUsd { get; init; }
    public long TokensInput { get; init; }
    public long TokensOutput { get; init; }
    public long TokensReasoning { get; init; }
    /// <summary>First not-done checkpoint in the active stage — what the session is working toward.</summary>
    public string CurrentCheckpoint { get; init; } = "";
    public int ResumeCount { get; init; }
    public string GateSummary { get; init; } = "";
    public string Branch { get; init; } = "";
    public DateTime? BackoffUntilUtc { get; init; }
    public IReadOnlyList<(string Id, string Status)> StageCheckpoints { get; init; } = Array.Empty<(string, string)>();
    public IReadOnlyList<(string StageId, int Done, int Total, string State)> StageOverview { get; init; } = Array.Empty<(string, int, int, string)>();
}

public interface IProgressSink
{
    void Log(string line);
    void AgentEvent(AgentEvent ev);
    void Snapshot(DashboardSnapshot snap);
    ControlAction? PollControl();
}
