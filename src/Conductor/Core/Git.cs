namespace Conductor.Core;

public static class Git
{
    public static ProcResult Exec(string repo, params string[] args)
        => ProcessRunner.Run("git", new[] { "-C", repo }.Concat(args), repo, TimeSpan.FromMinutes(10));

    public static string Head(string repo) => Exec(repo, "rev-parse", "HEAD").Output.Trim();

    public static string Branch(string repo) => Exec(repo, "rev-parse", "--abbrev-ref", "HEAD").Output.Trim();

    public static List<string> CommitsSince(string repo, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return new List<string>();
        var r = Exec(repo, "log", "--oneline", $"{sha}..HEAD");
        if (r.ExitCode != 0) return new List<string>();
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
    }

    public static bool IsDirty(string repo)
        => Exec(repo, "status", "--porcelain").Output.Trim().Length > 0;

    public static string DirtySummary(string repo)
    {
        var lines = Exec(repo, "status", "--porcelain").Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return "clean";
        var shown = lines.Take(8).Select(l => l.Trim());
        return string.Join(", ", shown) + (lines.Length > 8 ? $" (+{lines.Length - 8} more)" : "");
    }

    /// <summary>Returns (ahead, behind) counts vs the upstream tracking branch, or null if the branch has
    /// no upstream configured (e.g. a detached HEAD, a local-only branch, or no remote).</summary>
    public static (int Ahead, int Behind)? AheadBehind(string repo)
    {
        var r = Exec(repo, "rev-list", "--left-right", "--count", "@{upstream}...HEAD");
        if (r.ExitCode != 0) return null;
        var parts = r.Output.Trim().Split('\t');
        if (parts.Length == 2 && int.TryParse(parts[0], out var behind) && int.TryParse(parts[1], out var ahead))
            return (ahead, behind);
        return null;
    }
}
