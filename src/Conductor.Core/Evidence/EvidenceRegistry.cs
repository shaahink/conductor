using Conductor.Core.Events;

namespace Conductor.Core.Evidence;

/// <summary>
/// K5.3 — the registry is a FOLD of <see cref="EvidenceRegistered"/>, not a directory scan. Replaying
/// the log from any point reproduces the same set, which is the same rule <see cref="TaskGraph"/>
/// lives by; a scan-at-read-time registry would answer differently depending on what had since been
/// deleted, and could not say which session produced what.
/// <para>Identity is path + content hash: the same file claimed twice is one artifact, and an edited
/// file at the same path is honestly a second one.</para>
/// </summary>
public sealed class EvidenceRegistry
{
    private readonly List<EvidenceArtifact> _artifacts = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>In registration order — oldest first, like the log.</summary>
    public IReadOnlyList<EvidenceArtifact> Artifacts => _artifacts;

    public int Count => _artifacts.Count;

    public void Fold(IEnumerable<ConductorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var evt in events)
            if (evt is EvidenceRegistered e)
                Add(new EvidenceArtifact(e.Path, e.Kind, e.CheckpointId, e.StageId, e.SessionNumber,
                    e.Sha256, e.Bytes, e.Ts, e.Source));
    }

    /// <summary>True if the artifact was added; false if these exact bytes at this exact path are
    /// already known. This is the whole of the de-duplication, and it is why a watcher can run every
    /// session without re-announcing the same screenshot.</summary>
    public bool Add(EvidenceArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!_keys.Add(artifact.Key)) return false;
        _artifacts.Add(artifact);
        return true;
    }

    public bool Knows(EvidenceArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return _keys.Contains(artifact.Key);
    }

    public IEnumerable<EvidenceArtifact> ForCheckpoint(string checkpointId) =>
        _artifacts.Where(a => string.Equals(a.CheckpointId, checkpointId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The newest <paramref name="count"/>, newest first — what a surface shows.</summary>
    public IReadOnlyList<EvidenceArtifact> Latest(int count) =>
        _artifacts.AsEnumerable().Reverse().Take(Math.Max(0, count)).ToList();

    public static EvidenceRegistry From(IEnumerable<ConductorEvent> events)
    {
        var r = new EvidenceRegistry();
        r.Fold(events);
        return r;
    }
}

/// <summary>
/// K5.3 — the watcher. Stamp-based rather than a <c>FileSystemWatcher</c>, for the reason
/// <c>RunLoop.Reload</c> gives for the same choice: the boundary is where the answer matters, a
/// watcher's callbacks arrive on threads that own no run state, and a missed inotify event is a
/// silent hole. A directory is scanned at the boundary and anything the registry has not seen is new.
/// </summary>
public static class EvidenceWatcher
{
    /// <summary>A run that dropped a thousand files into an evidence directory has a different
    /// problem; announcing all of them is not the fix.</summary>
    public const int MaxPerScan = 50;

    /// <summary>Files bigger than this are registered but never carried by a notification — a
    /// courtesy to K5.4, which has to decide what it can send.</summary>
    public const long LargeFileBytes = 20L * 1024 * 1024;

    /// <summary>Scan the watched directories and return the artifacts the registry has not seen.
    /// Does not mutate the registry: the caller emits the events, and the fold is what records them,
    /// so a crash between scan and emit re-finds the same files instead of losing them.</summary>
    public static async Task<IReadOnlyList<EvidenceArtifact>> ScanAsync(
        IEnumerable<string> directories, EvidenceRegistry known, string repoRoot,
        int? sessionNumber, TimeProvider? time = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(known);

        var found = new List<EvidenceArtifact>();
        foreach (var dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var file in Files(dir))
            {
                if (found.Count >= MaxPerScan) return found;
                var artifact = await EvidenceReader
                    .ReadAsync(file, repoRoot, null, sessionNumber, "watcher", time, ct).ConfigureAwait(false);
                if (artifact is null || known.Knows(artifact)) continue;
                found.Add(artifact);
            }
        }
        return found;
    }

    private static IEnumerable<string> Files(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                : [];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>The directories a run watches: the state dir's own <c>evidence/</c> and the repo's
    /// <c>docs/evidence/</c> — the two <c>AuditCommand</c> has always scanned, so nothing new has to
    /// be configured for an existing plan to start producing artifacts.</summary>
    public static IReadOnlyList<string> DefaultDirectories(string repoRoot, string stateDir) =>
    [
        Path.Combine(stateDir, "evidence"),
        Path.Combine(repoRoot, "docs", "evidence"),
    ];
}
