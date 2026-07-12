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
[JsonDerivedType(typeof(NoteAdded),             "noteAdded")]
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

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(ConductorEvent))]
public sealed partial class EventJsonContext : JsonSerializerContext;
