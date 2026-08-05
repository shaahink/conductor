namespace Conductor.Core.Events;

public sealed record TaskAdded : ConductorEvent
{
    public required string TaskId { get; init; }
    public required string CheckpointId { get; init; }
    public required string Title { get; init; }

    /// <summary>Provenance: plan | tracker | import | human | agent (W1.1). Named Source since B9.1;
    /// the serialized field stays <c>source</c> so historical logs replay unchanged.</summary>
    public required string Source { get; init; }
    public int Order { get; init; }

    /// <summary>W1.1: checkpoint | subtask. Null on pre-W1 events — the fold infers checkpoint
    /// when <see cref="TaskId"/> equals <see cref="CheckpointId"/> (how seeds always wrote them).</summary>
    public string? Kind { get; init; }

    /// <summary>W1.1: owning stage id, carried explicitly for checkpoint-kind items (the seed knows
    /// it from the plan conventions). Null on pre-W1 events — the fold falls back to the
    /// split-on-first-dot default.</summary>
    public string? StageId { get; init; }
}

public sealed record TaskStatusChanged : ConductorEvent
{
    public required string TaskId { get; init; }
    public required string Status { get; init; }

    /// <summary>W1.1: the commit sha a done-claim attributes (checkpoint Commit column). Null =
    /// leave unchanged.</summary>
    public string? Commit { get; init; }

    /// <summary>W1.1: the evidence string a done-claim carries (checkpoint Evidence column).
    /// Null = leave unchanged.</summary>
    public string? Evidence { get; init; }

    /// <summary>W1.1: who changed the status — tracker | engine | agent | human. Null on pre-W1
    /// events. The verdict engine reads agent-sourced done-claims (W1.3).</summary>
    public string? Source { get; init; }
}

public sealed record NoteAdded : ConductorEvent
{
    public required string Kind { get; init; }
    public required string Content { get; init; }
    public string? StageId { get; init; }
}
