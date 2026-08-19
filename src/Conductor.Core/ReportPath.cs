namespace Conductor.Core;

/// <summary>
/// One rule, shared by every gate class that reads a file its own command just wrote: the declared
/// path may contain a <c>*</c>, and it resolves to the NEWEST match — because the runners that
/// produce these reports name their output after the machine and the clock (VSTest's trx) or after a
/// run directory stamped with the time (Stryker's <c>StrykerOutput</c>), and "newest" is the one
/// this battery just produced.
/// </summary>
internal static class ReportPath
{
    public static string? ResolveNewest(string? declared, string cwd)
    {
        if (string.IsNullOrWhiteSpace(declared)) return null;
        var full = Path.IsPathRooted(declared) ? declared : Path.Combine(cwd, declared);
        if (!full.Contains('*', StringComparison.Ordinal)) return full;
        var dir = Path.GetDirectoryName(full);
        // A wildcard in a DIRECTORY segment (StrykerOutput/*/reports/x.json) cannot be enumerated by
        // a filename pattern, so walk down from the last literal directory instead.
        var literal = FirstLiteralDirectory(dir);
        if (literal is null || !Directory.Exists(literal)) return null;
        var leaf = Path.GetFileName(full);
        try
        {
            return Directory.EnumerateFiles(literal, leaf, SearchOption.AllDirectories)
                .Where(f => Matches(full, f))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The longest prefix of <paramref name="dir"/> that contains no wildcard — the deepest
    /// place enumeration can start from.</summary>
    private static string? FirstLiteralDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        if (!dir.Contains('*', StringComparison.Ordinal)) return dir;
        var parts = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var take = parts.TakeWhile(p => !p.Contains('*', StringComparison.Ordinal)).ToArray();
        return take.Length == 0 ? null : string.Join(Path.DirectorySeparatorChar, take);
    }

    /// <summary>Does the found file satisfy the declared pattern, wildcard directory segments and
    /// all? Segment-wise so <c>StrykerOutput/*/reports/x.json</c> does not also accept
    /// <c>StrykerOutput/a/b/reports/x.json</c>.</summary>
    private static bool Matches(string pattern, string candidate)
    {
        var p = Norm(pattern).Split('/');
        var c = Norm(candidate).Split('/');
        if (p.Length != c.Length) return false;
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == "*") continue;
            if (p[i].Contains('*', StringComparison.Ordinal))
            {
                var head = p[i][..p[i].IndexOf('*', StringComparison.Ordinal)];
                var tail = p[i][(p[i].LastIndexOf('*') + 1)..];
                if (!c[i].StartsWith(head, StringComparison.OrdinalIgnoreCase) ||
                    !c[i].EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return false;
                continue;
            }
            if (!p[i].Equals(c[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');
}
