namespace Conductor.Core.Worktrees;

/// <summary>One worktree as the sweep sees it: git's row, plus whether conductor owns it and whether the
/// run that made it is still alive.</summary>
public sealed record WorktreeStatus(
    WorktreeEntry Entry,
    bool ConductorOwned,
    AttemptMarker? Marker,
    bool OwnerAlive)
{
    /// <summary>Reapable = ours, and the process that made it is gone. A foreign worktree is never
    /// reapable no matter how stale it looks, and neither is a live run's.</summary>
    public bool Reapable => ConductorOwned && !OwnerAlive;
}

/// <summary>KS4.4 — finds and reaps the attempt worktrees a killed run left behind.</summary>
/// <remarks>
/// <para>A run that dies between "create the attempt tree" and "drop it" leaves a full checkout on disk
/// and a git administrative record pointing at it. Nothing cleaned those up before this: they accumulate
/// silently, they cost gigabytes, and — the part that actually bites — <c>git worktree add</c> refuses a
/// branch name still claimed by a stale record, so a leak eventually stops new attempts from starting.
/// The sweep runs at engine startup, where a leak from the previous process is exactly what is on disk.</para>
/// <para>Two rules keep it safe. It only ever considers trees whose branch or directory carries
/// <see cref="AttemptWorktree.Prefix"/>, so a worktree a human made — or another tool's — is invisible to
/// it. And when the sidecar marker names a process that is still alive, the tree belongs to a run in
/// flight (this machine hosts more than one at a time by design) and is left alone.</para>
/// </remarks>
public static class WorktreeSweeper
{
    /// <summary>Every worktree git knows about, classified. The main worktree is excluded — it is the
    /// repo, and no sweep should ever have it in hand.</summary>
    public static List<WorktreeStatus> Survey(string repo)
    {
        var mainPath = Normalize(repo);
        var result = new List<WorktreeStatus>();
        foreach (var e in Git.WorktreeList(repo))
        {
            if (Normalize(e.Path) == mainPath) continue;
            var marker = AttemptWorktree.ReadMarker(e.Path);
            var owned = IsConductorOwned(e, marker);
            result.Add(new WorktreeStatus(e, owned, marker, owned && OwnerAlive(marker)));
        }
        return result;
    }

    /// <summary>True when this tree is conductor's to reap: the branch or the directory carries the
    /// attempt/lane prefix, or a sidecar marker claims it. Anything else belongs to a human.</summary>
    private static bool IsConductorOwned(WorktreeEntry e, AttemptMarker? marker)
    {
        if (marker is not null) return true;
        if (e.Branch is { } b && (b.StartsWith(AttemptWorktree.Prefix, StringComparison.Ordinal)
                                  || b.StartsWith("conductor-lane-", StringComparison.Ordinal))) return true;
        var dir = System.IO.Path.GetFileName(e.Path.TrimEnd('/', '\\'));
        return dir.StartsWith(AttemptWorktree.Prefix, StringComparison.Ordinal)
               || dir.StartsWith("conductor-mutating-", StringComparison.Ordinal);
    }

    /// <summary>No marker means we cannot prove an owner is alive, and a tree carrying our prefix with no
    /// marker is either pre-KS4.4 or had its sidecar removed — both are leaks. A marker whose pid still
    /// resolves to a process started at the recorded time is a LIVE run and is protected.</summary>
    private static bool OwnerAlive(AttemptMarker? marker)
        => marker is not null && PidLiveness.LooksAlive(marker.Pid, marker.PidStartUtc);

    /// <summary>How many conductor attempt trees are live right now — the number a concurrency limit
    /// counts against, so a run cannot exceed its lane budget through trees it forgot it had.</summary>
    public static int LiveCount(string repo)
        => Survey(repo).Count(s => s.ConductorOwned && s.OwnerAlive);

    /// <summary>Reap every orphaned attempt tree. Returns one line per tree acted on, suitable for the
    /// log and for the <c>worktree</c> verb's output.</summary>
    /// <param name="dryRun">Report what would be reaped and touch nothing.</param>
    public static List<string> Reap(string repo, bool dryRun = false, Action<string>? log = null)
    {
        var lines = new List<string>();
        foreach (var s in Survey(repo).Where(s => s.Reapable))
        {
            if (dryRun)
            {
                lines.Add($"would reap {s.Entry.Path}{Describe(s)}");
                continue;
            }
            var dropped = WorktreeDrop.DropAttempt(repo, s.Entry.Path, s.Entry.Branch, log);
            var line = dropped.Clean
                ? $"reaped {s.Entry.Path}{Describe(s)}"
                : $"reaped {s.Entry.Path}{Describe(s)} — " + string.Join("; ", Problems(dropped));
            lines.Add(line);
            log?.Invoke("worktree sweep: " + line);
        }
        // A record whose directory is already gone costs nothing to keep but blocks its branch name.
        Git.WorktreePrune(repo);
        return lines;
    }

    private static IEnumerable<string> Problems(WorktreeDropResult r)
    {
        if (!r.TreeRemoved) yield return $"directory survived: {r.TreeError}";
        if (r.BranchKept is { } b) yield return $"branch '{b}' KEPT (holds unmerged commits)";
    }

    private static string Describe(WorktreeStatus s)
        => s.Marker is { } m ? $" (run {m.RunId}, stage {m.StageId} attempt {m.Attempt}, pid {m.Pid} gone)" : "";

    private static string Normalize(string path)
    {
        try { return System.IO.Path.GetFullPath(path).TrimEnd('/', '\\').Replace('\\', '/').ToLowerInvariant(); }
        catch { return path.TrimEnd('/', '\\').Replace('\\', '/').ToLowerInvariant(); }
    }
}
