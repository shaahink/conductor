using Conductor.Models;

namespace Conductor.Core;

/// <summary>SC4.3: the sibling repositories a plan declares under <c>satelliteRepos</c>.
///
/// <para>A run's verdict has always asked one repo whether work happened. On the sk run a stage was
/// delivered entirely in a sibling repo and scored <c>NoProgress</c> — twice, in a plan written to
/// avoid exactly that (sk #3). The primary repo's <c>git log</c> was empty because the work was
/// never supposed to land there. This is the list that makes the question honest: the verdict diffs
/// every declared repo, not just its own.</para>
///
/// <para>Every operation here is best-effort by design. A satellite is a path on someone else's
/// disk: it can be missing, not a git repo, or unreadable, and none of those is a reason to fail a
/// session that did real work. An unusable satellite contributes nothing and says so once.</para>
/// </summary>
public static class SatelliteRepos
{
    /// <summary>The plan's satellite entries resolved to (label, absolute path). Relative entries
    /// resolve against the primary repo root. Blank entries, duplicates and the primary repo itself
    /// are dropped — counting the primary twice would double every commit it lands.</summary>
    public static List<(string Label, string Path)> Resolve(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var outp = new List<(string, string)>();
        if (plan.SatelliteRepos is not { Count: > 0 }) return outp;

        var primary = SafeFullPath(plan.Repo);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan.SatelliteRepos)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var raw = entry.Trim();
            var abs = SafeFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(plan.Repo, raw));
            if (abs.Length == 0) continue;
            if (string.Equals(abs, primary, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(abs)) continue;
            outp.Add((LabelFor(abs), abs));
        }
        return outp;
    }

    /// <summary>Current HEAD of every resolvable satellite, keyed by label. A satellite that is
    /// missing or is not a git repo is simply absent — <see cref="CommitsSince"/> then has no start
    /// marker for it and reports nothing, which is the honest answer.</summary>
    public static Dictionary<string, string> Heads(PlanConfig plan, Action<string>? log = null)
    {
        var heads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, path) in Resolve(plan))
        {
            if (!Directory.Exists(path))
            {
                log?.Invoke($"satelliteRepos: '{label}' ({path}) does not exist — its commits cannot be counted this session");
                continue;
            }
            var r = Git.Exec(path, "rev-parse", "HEAD");
            var sha = r.Output.Trim();
            if (r.ExitCode != 0 || !IsSha(sha))
            {
                log?.Invoke($"satelliteRepos: '{label}' ({path}) is not a git repo with commits — its commits cannot be counted this session");
                continue;
            }
            heads[label] = sha;
        }
        return heads;
    }

    /// <summary>Commits landed in each satellite since the session-start head recorded for it, as
    /// <c>git log --oneline</c> rows suffixed with the satellite's label.</summary>
    /// <remarks>The label is a SUFFIX on purpose: <see cref="Git.IsBookkeepingCommit"/> reads the
    /// subject after the leading sha, so a prefix would hide conductor's own <c>chore(conductor):</c>
    /// commits from SC4.2's filter and hand a satellite the exact false-green SC4.2 closed.</remarks>
    public static List<string> CommitsSince(PlanConfig plan, IReadOnlyDictionary<string, string>? startHeads)
    {
        var outp = new List<string>();
        if (startHeads is not { Count: > 0 }) return outp;
        foreach (var (label, path) in Resolve(plan))
        {
            if (!startHeads.TryGetValue(label, out var start) || !IsSha(start)) continue;
            if (!Directory.Exists(path)) continue;
            foreach (var line in Git.CommitsSince(path, start))
                outp.Add($"{line} [{label}]");
        }
        return outp;
    }

    /// <summary>Human-readable roll-call for the verdict log: the satellites this session actually
    /// watched, or null when the plan declares none.</summary>
    public static string? Describe(PlanConfig plan)
    {
        var list = Resolve(plan);
        return list.Count == 0 ? null : string.Join(", ", list.Select(s => s.Label));
    }

    private static string LabelFor(string absPath)
    {
        var name = Path.GetFileName(absPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? absPath : name;
    }

    private static bool IsSha(string s) => s.Length >= 7 && s.All(Uri.IsHexDigit);

    private static string SafeFullPath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return ""; }
    }
}
