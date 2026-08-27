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

    /// <summary>Bug #40: the label of the declared satellite this tool call TOUCHED, or null. A write
    /// tool whose path resolves inside the satellite touches it; so does a shell command whose text
    /// names the satellite's directory — absolute or relative to the primary repo, either separator —
    /// because <c>git -C ../site commit</c> is how a session lands a commit there without a write
    /// tool ever seeing a path. Reading, listing and grepping a satellite through a write-less tool
    /// touch nothing, which is exactly the KS0 session's whole history with the field guide.</summary>
    public static string? Touched(PlanConfig plan, Events.ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var satellites = Resolve(plan);
        if (satellites.Count == 0) return null;

        if (Providers.ToolEventExtractor.IsWrite(call.Name) && call.Field("path") is { Length: > 0 } path)
        {
            var cleaned = path.Trim().Trim('"', '\'');
            var full = cleaned.Length > 0 && !Path.IsPathRooted(cleaned) && !string.IsNullOrWhiteSpace(plan.Repo)
                ? Path.Combine(plan.Repo, cleaned)
                : cleaned;
            foreach (var (label, satPath) in satellites)
                if (!RepoScope.IsOutside(satPath, full, out _)) return label;
        }

        if (call.Field("command") is { Length: > 0 } command)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var primary = SafeFullPath(plan.Repo);
            foreach (var (label, satPath) in satellites)
            {
                foreach (var spelling in Spellings(satPath, primary))
                    if (command.Contains(spelling, comparison)) return label;
            }
        }
        return null;
    }

    /// <summary>Splits satellite commits into this session's own and the foreign ones, by the label
    /// suffix <see cref="CommitsSince"/> writes: own when the satellite is on <paramref name="touched"/>,
    /// foreign otherwise. Order is preserved on both sides.</summary>
    public static (List<string> Own, List<string> Foreign) Attribute(
        IReadOnlyList<string> satelliteCommits, IReadOnlyCollection<string> touched)
    {
        ArgumentNullException.ThrowIfNull(satelliteCommits);
        ArgumentNullException.ThrowIfNull(touched);
        var own = new List<string>();
        var foreign = new List<string>();
        foreach (var row in satelliteCommits)
        {
            var open = row.LastIndexOf('[');
            var label = open >= 0 && row.EndsWith(']') ? row[(open + 1)..^1] : "";
            (touched.Contains(label, StringComparer.OrdinalIgnoreCase) ? own : foreign).Add(row);
        }
        return (own, foreign);
    }

    /// <summary>The ways a command line can name a satellite: its absolute path and its path relative
    /// to the primary repo, each with both separators. Never the bare label — a word like "site" in a
    /// command is not a directory.</summary>
    private static IEnumerable<string> Spellings(string satPath, string primary)
    {
        var abs = satPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        yield return abs.Replace('\\', '/');
        yield return abs.Replace('/', '\\');
        if (primary.Length == 0) yield break;
        string rel;
        try { rel = Path.GetRelativePath(primary, abs); }
        catch (ArgumentException) { yield break; }
        if (rel.Length == 0 || rel == "." || Path.IsPathRooted(rel)) yield break;
        yield return rel.Replace('\\', '/');
        yield return rel.Replace('/', '\\');
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
