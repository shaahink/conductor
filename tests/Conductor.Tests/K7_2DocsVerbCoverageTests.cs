using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// K7.2: the CLI reference must name every verb the engine ships.
///
/// SC8.3 already learned this lesson once, for shell completion: the expected verb list was
/// hand-typed, so `version` shipped missing from both completion scripts and the test stayed green.
/// The fix was to read the list off <c>Program.cs</c> (see <c>B11_2DoctorAndCompletionTests</c>).
/// The docs page never got the same guard and rotted the same way — at the v0.4.0 ship,
/// <c>budget</c>, <c>money</c>, <c>history</c>, <c>ps</c> and <c>watch</c> were all reachable from
/// tab-complete and absent from <c>docs/cli.md</c>, and <c>budget</c>/<c>money</c> are the two verbs
/// the release notes lead with. Adding a verb is now two places, and the second is enforced here.
/// </summary>
public class K7_2DocsVerbCoverageTests
{
    [Fact]
    public void CliReference_NamesEveryShippedVerb()
    {
        var verbs = RegisteredVerbs();
        Assert.True(verbs.Count > 30,
            $"only {verbs.Count} verbs parsed out of Program.cs - the scan is broken, not the docs");

        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "cli.md"));
        var undocumented = verbs.Where(v => !Mentions(doc, v)).OrderBy(v => v, StringComparer.Ordinal).ToList();

        Assert.True(undocumented.Count == 0,
            "docs/cli.md does not name these shipped verbs: " + string.Join(", ", undocumented) +
            " - document them there, or hide the command in Program.cs if it is not a verb to reach for.");
    }

    /// <summary>A verb counts as documented when it appears as a code span - <c>`verb`</c>,
    /// <c>`verb --flag`</c> or <c>`conductor verb`</c>. Bare prose does not count, because the page is
    /// a reference and every other verb on it is written that way. The trailing boundary is what keeps
    /// <c>`tasks`</c> from standing in for <c>task</c>.</summary>
    private static bool Mentions(string doc, string verb) =>
        Regex.IsMatch(doc, @"`(?:conductor\s+)?" + Regex.Escape(verb) + @"(?:`|\s)",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));

    /// <summary>Every verb <c>Program.cs</c> registers and does not hide. Source-scanned rather than
    /// reflected because Spectre's <c>CommandApp</c> keeps its configuration private, and the source
    /// is the thing a future session will edit. Mirrors the scanner in
    /// <c>B11_2DoctorAndCompletionTests</c>, deliberately: the two bars must not be able to drift
    /// apart by sharing a helper that one of them later relaxes.</summary>
    private static HashSet<string> RegisteredVerbs()
    {
        var program = Path.Combine(RepoRoot(), "src", "Conductor", "Program.cs");
        var verbs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(program))
        {
            var m = Regex.Match(line, @"AddCommand<\w+>\(""(?<verb>[a-z][a-z0-9-]*)""\)",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
            if (!m.Success) continue;
            if (line.Contains(".IsHidden()", StringComparison.Ordinal)) continue;
            verbs.Add(m.Groups["verb"].Value);
        }
        return verbs;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate repo root (Conductor.slnx)");
    }
}
