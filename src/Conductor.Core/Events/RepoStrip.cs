namespace Conductor.Core.Events;

/// <summary>
/// B5.4: repo-awareness strip — a live git query (branch, dirty, ahead/behind) surfaced in the report
/// and TUI. Not an event fold (see B5 trap: the repo strip is explicitly a live query).
/// </summary>
public static class RepoStrip
{
    public sealed record RepoInfo(
        string Branch, string Head, bool Dirty, string DirtySummary,
        int Ahead, int Behind, bool HasUpstream, string? Error);

    public static RepoInfo Compute(string repo)
    {
        try
        {
            var br = Git.Exec(repo, "rev-parse", "--abbrev-ref", "HEAD");
            var hd = Git.Exec(repo, "rev-parse", "HEAD");
            // If rev-parse fails, we're not in a git repo — bail with an error.
            if (br.ExitCode != 0 || hd.ExitCode != 0)
                return new RepoInfo("?", "?", false, "?",
                    0, 0, false, br.Output.Trim().Split('\n')[0]);
            var branch = br.Output.Trim();
            var head = hd.Output.Trim();
            var dirty = Git.IsDirty(repo);
            var dirtySum = Git.DirtySummary(repo);
            var ab = Git.AheadBehind(repo);
            return new RepoInfo(branch, head.Length >= 7 ? head[..7] : head,
                dirty, dirtySum,
                ab?.Ahead ?? 0, ab?.Behind ?? 0, ab.HasValue, null);
        }
        catch (Exception ex)
        {
            return new RepoInfo("?", "?", false, "?",
                0, 0, false, ex.Message);
        }
    }

    public static IEnumerable<string> Format(RepoInfo info)
    {
        yield return $"branch: {info.Branch}   HEAD: {info.Head}";
        yield return $"working tree: {(info.Dirty ? info.DirtySummary : "clean")}";
        if (info.HasUpstream)
        {
            var ahead = info.Ahead > 0 ? $"{info.Ahead} ahead" : "";
            var behind = info.Behind > 0 ? $"{info.Behind} behind" : "";
            var sep = ahead.Length > 0 && behind.Length > 0 ? ", " : "";
            yield return $"vs upstream: {(ahead.Length > 0 || behind.Length > 0 ? $"{ahead}{sep}{behind}" : "up to date")}";
        }
        if (info.Error != null)
            yield return $"error: {info.Error}";
    }

    /// <summary>Format suitable for the automatic (heartbeat) report path — omits the volatile HEAD
    /// SHA so timestamp-only rewrites don't create duplicate commits (F-4, A15).</summary>
    public static IEnumerable<string> FormatStable(RepoInfo info)
    {
        yield return $"branch: {info.Branch}";
        yield return $"working tree: {(info.Dirty ? info.DirtySummary : "clean")}";
        if (info.HasUpstream)
        {
            var ahead = info.Ahead > 0 ? $"{info.Ahead} ahead" : "";
            var behind = info.Behind > 0 ? $"{info.Behind} behind" : "";
            var sep = ahead.Length > 0 && behind.Length > 0 ? ", " : "";
            yield return $"vs upstream: {(ahead.Length > 0 || behind.Length > 0 ? $"{ahead}{sep}{behind}" : "up to date")}";
        }
    }
}
