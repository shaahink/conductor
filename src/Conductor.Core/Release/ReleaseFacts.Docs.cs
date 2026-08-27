namespace Conductor.Core.Release;

/// <summary>Bug #95: one docs row that still carries the pre-release caveat — the file (repo-relative,
/// forward slashes), the 1-based line, and the line's text as found.</summary>
public sealed record DocsRow(string File, int Line, string Text);

/// <summary>Bug #95: the docs rows the tag makes false, as the probe found them. <paramref name="FilesScanned"/>
/// says how wide the measurement was, so "0 rows" over 0 files reads as the non-measurement it is.</summary>
public sealed record DocsFacts(IReadOnlyList<DocsRow> Rows, int FilesScanned)
{
    /// <summary>The fixed phrase every such row carries. The version before it varies; this does not.</summary>
    public const string Caveat = "not in the released binary yet";

    /// <summary>The whole clause the docs act rewrites: <c>New since `vX.Y.Z`; not in the released
    /// binary yet</c>. Matched by shape rather than by the version, because the version named is the
    /// PREVIOUS release and the row is being rewritten for the next one.</summary>
    public static readonly System.Text.RegularExpressions.Regex Clause = new(
        @"New since `v?[0-9][0-9A-Za-z.\-+]*`; " + System.Text.RegularExpressions.Regex.Escape(Caveat),
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>What a row reads after the docs act: the caveat gone, the release it shipped in named.
    /// Only the clause moves; the sentence after the dash, if any, survives untouched.</summary>
    public static string Rewrite(string line, string version)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(version);
        return Clause.Replace(line, "New in `v" + version.TrimStart('v', 'V') + "`");
    }
}
