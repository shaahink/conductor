using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Conductor.Core.Integrations.Github;

/// <summary>One place in the tree a bug names: a repo-relative path and the line span it cites.</summary>
public sealed record SarifBugLocation(string Path, int StartLine, int? EndLine)
{
    public string Cite() => EndLine is { } end && end > StartLine
        ? $"{Path}:{StartLine.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}"
        : $"{Path}:{StartLine.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// DV6.4 — lifts <c>path:line</c> citations out of the free text of a bug row.
///
/// <para>The <c>bugs</c> table has no file column and never had one (<c>v7_bugs.sql</c>): sessions
/// write the citation into the prose, in two shapes. Some write the whole repo-relative path
/// (<c>src/Conductor.Core/Store/StateHome.cs:27-29</c>); most write only the file name
/// (<c>VerdictEngine.cs:370</c>), because that is what a reader needs and the writer knew where it
/// lived. A bare name cannot anchor a code-scanning alert, so it is RESOLVED against the tracked
/// file list and kept only when exactly one tracked file bears it — an ambiguous or unknown name is
/// refused rather than guessed, because an alert on the wrong file is worse than no alert.</para>
///
/// <para>A citation with no line is refused too. SARIF would let us default the region to line 1;
/// that would put every such bug at the top of a file it merely mentions, which reads as a fact and
/// is not one.</para>
/// </summary>
public static partial class SarifBugLocations
{
    /// <summary>The extensions a citation may carry. Deliberately a list and not "anything with a
    /// dot": prose here is full of <c>0.4.1</c>, <c>#79</c> and <c>api.github.com</c>, and every one
    /// of those is a false path if the shape alone decides.</summary>
    private const string Extensions =
        "cs|go|ps1|psm1|md|json|jsonc|js|ts|tsx|razor|csproj|slnx|sln|yml|yaml|sql|sh|html|css|proj|props|targets";

    [GeneratedRegex(
        @"(?<![\w/\\.:])(?<path>[A-Za-z0-9_][\w.+-]*(?:[/\\][\w.+-]+)*\.(?:" + Extensions + @"))" +
        @":(?<start>\d{1,7})(?:\s*[-–]\s*(?<end>\d{1,7}))?(?![\w.])",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex Citation();

    /// <summary>Every distinct location a bug's text names, in the order the text names them. First
    /// wins on the wire: code scanning anchors an alert to <c>locations[0]</c>.</summary>
    public static List<SarifBugLocation> Find(string? text, Func<string, string?> resolve)
    {
        var found = new List<SarifBugLocation>();
        if (string.IsNullOrWhiteSpace(text)) return found;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Citation().Matches(text))
        {
            if (!TryLocation(m, resolve, out var location)) continue;
            if (seen.Add(location.Cite())) found.Add(location);
        }
        return found;
    }

    private static bool TryLocation(
        Match m, Func<string, string?> resolve, [NotNullWhen(true)] out SarifBugLocation? location)
    {
        location = null;
        if (!int.TryParse(m.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || start <= 0) return false;

        var raw = m.Groups["path"].Value.Replace('\\', '/');
        var path = resolve(raw);
        if (path is null) return false;

        int? end = null;
        if (m.Groups["end"].Success
            && int.TryParse(m.Groups["end"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var e)
            && e > start) end = e;

        location = new SarifBugLocation(path, start, end);
        return true;
    }

    /// <summary>Builds the resolver from a repository's tracked files. A citation that already
    /// carries a path must MATCH a tracked file — a path that no longer exists cites a tree that no
    /// longer exists, and code scanning would hang the alert on nothing.</summary>
    public static Func<string, string?> Resolver(IEnumerable<string> trackedFiles)
    {
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in trackedFiles)
        {
            var file = raw.Replace('\\', '/').Trim();
            if (file.Length == 0) continue;
            exact.Add(file);
            var name = file[(file.LastIndexOf('/') + 1)..];
            // Second sighting of a name poisons it to null: ambiguous is refused, not picked.
            byName[name] = byName.ContainsKey(name) ? null : file;
        }

        return cite =>
        {
            // The tracked list is the authority on casing; a citation may not be.
            if (exact.TryGetValue(cite, out var actual)) return actual;
            if (cite.Contains('/', StringComparison.Ordinal))
            {
                // A partial path — "Store/StateHome.cs" — resolves only if exactly one tracked file
                // ends with it; the suffix carries more evidence than a bare name, not less.
                var suffix = "/" + cite;
                string? hit = null;
                foreach (var file in exact)
                {
                    if (!file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (hit is not null) return null;
                    hit = file;
                }
                return hit;
            }
            return byName.TryGetValue(cite, out var single) ? single : null;
        };
    }
}
