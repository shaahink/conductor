namespace Conductor.Core;

/// <summary>
/// SC7.1 (devcontext #11) — answers "did the agent write that outside the work tree?".
/// </summary>
/// <remarks>
/// Only answerable at all because SC7.1 stopped truncating tool arguments mid-string: the check needs
/// a WHOLE <c>file_path</c>, and until now a path that sat past character 150 of the argument blob
/// simply was not in the capture. The verdict could not report what the transcript never held.
/// <para>Deliberately conservative. A path it cannot resolve is reported as inside, not outside: an
/// operator reading "3 file(s) written outside the repo" must be able to trust the number, and a
/// false positive on an unparseable path costs more than a missed exotic one.</para>
/// </remarks>
public static class RepoScope
{
    /// <summary>Windows paths are case-insensitive; anywhere else they are not. Getting this backwards
    /// would report every <c>C:\Code\…</c> vs <c>C:\code\…</c> write as an escape.</summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True when <paramref name="path"/> resolves to somewhere OUTSIDE
    /// <paramref name="root"/>. A relative path is resolved against <paramref name="root"/>, which is
    /// the agent process's working directory (<c>AgentSession.Start</c> spawns it there), so
    /// <c>src/App.cs</c> is inside and <c>../other/App.cs</c> is not.</summary>
    /// <param name="full">The resolved absolute path, set whenever this returns true.</param>
    public static bool IsOutside(string? root, string? path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;
        var cleaned = path.Trim().Trim('"', '\'');
        if (cleaned.Length == 0) return false;
        try
        {
            var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var target = Path.GetFullPath(Path.IsPathRooted(cleaned) ? cleaned : Path.Combine(rootFull, cleaned));
            var trimmed = Path.TrimEndingDirectorySeparator(target);
            if (trimmed.Equals(rootFull, PathComparison)) return false;
            if (trimmed.StartsWith(rootFull + Path.DirectorySeparatorChar, PathComparison)) return false;
            if (trimmed.StartsWith(rootFull + Path.AltDirectorySeparatorChar, PathComparison)) return false;
            full = target;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a path this process can resolve — say nothing rather than accuse.
            return false;
        }
    }

    /// <summary>As <see cref="IsOutside(string?, string?, out string)"/>, but a path inside any repo
    /// the plan DECLARED as a satellite is inside. SC4.3 established that a plan naming sibling repos
    /// means work legitimately lands there; flagging those as strays would make the note noise for
    /// exactly the plans that were most careful about saying where work goes.</summary>
    public static bool IsOutside(string? root, IReadOnlyCollection<string> alsoInside, string? path, out string full)
    {
        ArgumentNullException.ThrowIfNull(alsoInside);
        if (!IsOutside(root, path, out full)) return false;
        foreach (var other in alsoInside)
        {
            if (!IsOutside(other, full, out _)) { full = ""; return false; }
        }
        return true;
    }
}
