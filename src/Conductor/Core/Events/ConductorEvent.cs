using System.Text.Json.Serialization;

namespace Conductor.Core.Events;

/// <summary>
/// The B2 spine: a typed, append-only event describing a single Orchestrator transition. Every event
/// is emitted <em>alongside</em> today's <c>state.json</c> writes (additive — resumability never
/// regresses until StateCompat parity is proven in B2.2). Persisted as one compact JSON line in
/// <c>.conductor/events.jsonl</c> with a <c>type</c> discriminator, so <see cref="RunState"/>,
/// timeline, and metrics can later be rebuilt by folding the log (BATON-BRIEF §3.2).
/// </summary>
/// <remarks>
/// Polymorphic via <see cref="System.Text.Json"/> so the fold (B2.2) pattern-matches on the concrete
/// record rather than sniffing a nullable field bag. Envelope fields (<see cref="Seq"/>,
/// <see cref="Ts"/>, <see cref="RunId"/>, <see cref="SessionId"/>) are stamped by <see cref="EventLog"/>
/// at emit time; call sites only populate the payload.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStarted), "runStarted")]
[JsonDerivedType(typeof(StageEntered), "stageEntered")]
[JsonDerivedType(typeof(SessionStarted), "sessionStarted")]
[JsonDerivedType(typeof(SessionFinished), "sessionFinished")]
[JsonDerivedType(typeof(GateFinished), "gateFinished")]
[JsonDerivedType(typeof(CheckpointConfirmed), "checkpointConfirmed")]
[JsonDerivedType(typeof(StageConfirmed), "stageConfirmed")]
[JsonDerivedType(typeof(AttentionRequested), "attentionRequested")]
[JsonDerivedType(typeof(RunFinished), "runFinished")]
[JsonDerivedType(typeof(TokenDelta), "tokenDelta")]
[JsonDerivedType(typeof(OwnerApprovalRequested), "ownerApprovalRequested")]
[JsonDerivedType(typeof(OwnerApprovalGranted),   "ownerApprovalGranted")]
[JsonDerivedType(typeof(McpCallFinished),       "mcpCallFinished")]
[JsonDerivedType(typeof(TaskAdded),             "taskAdded")]
[JsonDerivedType(typeof(TaskStatusChanged),     "taskStatusChanged")]
[JsonDerivedType(typeof(SoftBreakRequested),    "softBreakRequested")]
        [JsonDerivedType(typeof(LaneStarted),           "laneStarted")]
        [JsonDerivedType(typeof(LaneFinished),          "laneFinished")]
        [JsonDerivedType(typeof(MutatingLaneStarted),   "mutatingLaneStarted")]
        [JsonDerivedType(typeof(MutatingLaneFinished),  "mutatingLaneFinished")]
        [JsonDerivedType(typeof(MergeGateVerdict),      "mergeGateVerdict")]
        [JsonDerivedType(typeof(RollbackExecuted),     "rollbackExecuted")]
public abstract record ConductorEvent
{
    /// <summary>Monotonic 1-based ordinal within the log (continues across restarts). Stamped by
    /// <see cref="EventLog"/>; the file's append order is the ground truth if a seq is ever ambiguous.</summary>
    public long Seq { get; init; }

    /// <summary>UTC timestamp the event was emitted (sourced from an injectable <see cref="TimeProvider"/>).</summary>
    public DateTimeOffset Ts { get; init; }

    /// <summary>The logical run this event belongs to (stable across resumes; persisted in RunState).</summary>
    public string RunId { get; init; } = "";

    /// <summary>Correlates the event to a conductor session number (null for run/stage-level events).</summary>
    public string? SessionId { get; init; }
}

/// <summary>The run began (or resumed) — the first event of every process invocation.</summary>
public sealed record RunStarted : ConductorEvent
{
    public required string Plan { get; init; }
    public required string Repo { get; init; }
    public string? Branch { get; init; }
    public string? DriverVersion { get; init; }
    /// <summary>True when a prior session had already run under this <see cref="ConductorEvent.RunId"/>.</summary>
    public bool Resumed { get; init; }
}

/// <summary>The orchestrator advanced to a new stage (git HEAD captured as the audit baseline).</summary>
public sealed record StageEntered : ConductorEvent
{
    public required string StageId { get; init; }
    public string? Title { get; init; }
    public string? StartHead { get; init; }
}

/// <summary>An agent session was spawned.</summary>
public sealed record SessionStarted : ConductorEvent
{
    public int Number { get; init; }
    public required string StageId { get; init; }
    public required string Kind { get; init; }
    public int Attempt { get; init; }
    public int MaxAttempts { get; init; }
    /// <summary>The provider/agent session id (today's <c>ClaudeSessionId</c>) used for resume.</summary>
    public string? AgentSessionId { get; init; }
    /// <summary>Active persona for this session (B7.3). null = default.</summary>
    public string? Persona { get; init; }
}

/// <summary>An agent session ended with an independently-verified outcome.</summary>
public sealed record SessionFinished : ConductorEvent
{
    public int Number { get; init; }
    public required string StageId { get; init; }
    public required string Outcome { get; init; }
    public IReadOnlyList<string> NewCommits { get; init; } = [];
    public IReadOnlyList<string> NewlyDone { get; init; } = [];
    public decimal? CostUsd { get; init; }
    public long? TokensInput { get; init; }
    public long? TokensOutput { get; init; }
    public long? TokensReasoning { get; init; }
    public long? TokensCacheRead { get; init; }
}

/// <summary>A gate finished as part of a battery — the trust-model verification surface.</summary>
public sealed record GateFinished : ConductorEvent
{
    public required string Name { get; init; }
    public bool Passed { get; init; }
    public bool Skipped { get; init; }
    public bool Optional { get; init; }
    public int ExitCode { get; init; }
    public long DurationMs { get; init; }
    /// <summary>Which battery ran it: <c>session</c>, <c>phase</c>, or <c>completion</c>.</summary>
    public string? Scope { get; init; }
}

/// <summary>A checkpoint row flipped DONE in a gate-green, committed session (an <c>Advanced</c> outcome).</summary>
public sealed record CheckpointConfirmed : ConductorEvent
{
    public required string CheckpointId { get; init; }
    public required string StageId { get; init; }
}

/// <summary>A stage's full battery (and audit, if enabled) confirmed it — the orchestrator advances past it.</summary>
public sealed record StageConfirmed : ConductorEvent
{
    public required string StageId { get; init; }
    public bool Audited { get; init; }
}

/// <summary>The run paused for a human decision (needs-attention).</summary>
public sealed record AttentionRequested : ConductorEvent
{
    public required string Reason { get; init; }
}

/// <summary>The plan completed (all checkpoints DONE and confirmed by the final battery).</summary>
public sealed record RunFinished : ConductorEvent
{
    public required string Status { get; init; }
    public int Sessions { get; init; }
    public int CheckpointsDone { get; init; }
    public int CheckpointsTotal { get; init; }
}

/// <summary>
/// A per-step token/cost delta emitted by the provider on every <c>step_finish</c> (R2.6, fixes F-3
/// live-token lag). The <see cref="LiveMetrics"/> projection folds these into per-session live totals;
/// the dashboard, report, and Telegram all consume them from one event-log source.
/// </summary>
public sealed record TokenDelta : ConductorEvent
{
    public long Input { get; init; }
    public long Output { get; init; }
    public long Reasoning { get; init; }
    public long CacheRead { get; init; }
    public decimal CostUsd { get; init; }
}

/// <summary>
/// An owner-gated stage reached green (all checkpoints DONE, full battery passed): the orchestrator
/// parks at <c>AwaitingOwner</c> until the human approves (B3.2).
/// </summary>
public sealed record OwnerApprovalRequested : ConductorEvent
{
    public required string StageId { get; init; }
}

/// <summary>
/// The owner approved an <c>OwnerApprovalRequested</c> stage, via CLI (<c>conductor approve</c>),
/// TUI key, or (B6) Telegram callback — the orchestrator now advances past it.
/// </summary>
public sealed record OwnerApprovalGranted : ConductorEvent
{
    public required string StageId { get; init; }
}

/// <summary>
/// An MCP-compatible tool call completed (B5.4). Emitted by the agent provider when it parses a
/// tool result from the stream. Forward-looking: the B9 MCP task server will emit these once MCP
/// integration lands; until then the projection folds synthetic test streams only.
/// </summary>
public sealed record McpCallFinished : ConductorEvent
{
    public required string ToolName { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
}

/// <summary>
/// A sub-task was added beneath a checkpoint (B9.1). Tasks are advisory break-points — the checkpoint
/// table stays the verified contract (D-8). Emitted by the planner persona decomposition.
/// </summary>
public sealed record TaskAdded : ConductorEvent
{
    public required string TaskId { get; init; }
    public required string CheckpointId { get; init; }
    public required string Title { get; init; }
    /// <summary>Who/what created this task: <c>planner</c>, <c>agent</c> (via MCP), or <c>human</c>.</summary>
    public required string Source { get; init; }
    public int Order { get; init; }
}

/// <summary>
/// A sub-task's status changed (B9.1). Tracked by the <see cref="TaskGraph"/> projection.
/// Allowed transitions: todo → in_progress → done (or todo → skipped).
/// </summary>
public sealed record TaskStatusChanged : ConductorEvent
{
    public required string TaskId { get; init; }
    public required string Status { get; init; }
}

/// <summary>
/// The soft-break threshold was crossed mid-session (B9.4): live tokens exceeded the
/// <c>SoftBreakRatio</c> fraction of <c>MaxSessionTokens</c>. A cooperative nudge signal
/// was written for the agent to finish the current sub-task, write a handoff, and end
/// cleanly. The hard <c>MaxSessionTokens</c> ceiling remains as the safety net.
/// </summary>
public sealed record SoftBreakRequested : ConductorEvent
{
    public long LiveTokens { get; init; }
    public long TokenBudget { get; init; }
    public string? CurrentCheckpointId { get; init; }
}

/// <summary>
/// A Tier A read-only analysis lane was dispatched to the worker pool (B12.2). Emitted when the lane
/// begins execution inside the pool (not when it is enqueued).
/// </summary>
public sealed record LaneStarted : ConductorEvent
{
    public required string LaneId { get; init; }
    public required string Kind { get; init; }
    public string? StageId { get; init; }
}

/// <summary>
/// A Tier A read-only analysis lane completed (success, failure, or cancellation) (B12.2).
/// </summary>
public sealed record LaneFinished : ConductorEvent
{
    public required string LaneId { get; init; }
    public required string Kind { get; init; }
    public required string Outcome { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>
/// A Tier B mutating lane was dispatched to an isolated <c>git worktree</c> (B12.3).
/// The lane can freely mutate files in its own worktree without affecting the primary tree.
/// </summary>
public sealed record MutatingLaneStarted : ConductorEvent
{
    public required string LaneId { get; init; }
    public required string Kind { get; init; }
    public string? StageId { get; init; }
    public string? ScratchBranch { get; init; }
}

/// <summary>
/// A Tier B mutating lane finished execution (B12.3). The <see cref="Outcome"/> is the lane-level
/// result (success/failure/error) — the merge gate verdict is a separate <see cref="MergeGateVerdict"/>
/// event emitted after battery verification.
/// </summary>
public sealed record MutatingLaneFinished : ConductorEvent
{
    public required string LaneId { get; init; }
    public required string Kind { get; init; }
    public required string Outcome { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }
    public bool AgentCommitted { get; init; }
}

/// <summary>
/// The merge gate verdict for a Tier B mutating lane (B12.3). Emitted after the full battery runs
/// on the integrated tree (base branch + lane's scratch branch merged). If <see cref="Passed"/>
/// is true the lane's changes were accepted into the primary tree; if false the lane is rejected
/// and its branch is torn down without merging.
/// </summary>
public sealed record MergeGateVerdict : ConductorEvent
{
    public required string LaneId { get; init; }
    public required string Kind { get; init; }
    public bool Passed { get; init; }
    public int TotalGates { get; init; }
    public int PassedCount { get; init; }
    public int FailedCount { get; init; }
    public string? FailureSummary { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>
/// A rollback was executed: the orchestrator reset the working tree to a prior HEAD (B5.1 / C3).
/// Emitted after `git reset --hard` succeeds so the event-log timeline/replay can reconstruct the
/// rollback — the report and Telegram will surface it alongside every other destructive action.
/// </summary>
public sealed record RollbackExecuted : ConductorEvent
{
    public required string StageId { get; init; }
    public required string FromSha { get; init; }
    public required string ToSha { get; init; }
    public bool Forced { get; init; }
}

/// <summary>Source-generated (de)serialisation for the event log — NDJSON, compact, camelCase, string
/// enums, nulls omitted. Reused for both the writer and the fold/replay readers (B2.2/B2.3).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(ConductorEvent))]
public sealed partial class EventJsonContext : JsonSerializerContext;
