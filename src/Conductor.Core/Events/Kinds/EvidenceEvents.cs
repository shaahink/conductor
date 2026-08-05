namespace Conductor.Core.Events;

/// <summary>K5.3: an evidence artifact entered the run. Emitted when a claim's <c>--evidence</c>
/// string turns out to name a real file, and when a watched directory gains one. The event IS the
/// registry — <see cref="EvidenceRegistry"/> is a fold of these, so a run's evidence replays from the
/// log like everything else rather than being re-scanned from disk at read time.</summary>
public sealed record EvidenceRegistered : ConductorEvent
{
    /// <summary>Repo-relative when the file is inside the repo, else absolute. Forward slashes.</summary>
    public required string Path { get; init; }

    /// <summary>image | video | audio | text | data | archive | binary (<c>EvidenceKinds</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>Content hash: the identity of the artifact, so a re-registration is not a duplicate.</summary>
    public required string Sha256 { get; init; }

    public long Bytes { get; init; }

    public string? CheckpointId { get; init; }

    public string? StageId { get; init; }

    /// <summary>The session that produced it. Null when a watcher found it outside a session.</summary>
    public int? SessionNumber { get; init; }

    /// <summary>claim | watcher — how the engine came to know about it.</summary>
    public required string Source { get; init; }
}
