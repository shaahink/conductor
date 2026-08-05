using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// K7.2: the front page must describe the product that ships.
///
/// <para>At the v0.4.0 ship README.md said "Eleven tabs (Agent · Sessions · Timeline · Procs ·
/// Console · Templates · Plan · Report · Knowledge · Telegram · Kanban)". The Face had had TEN tabs
/// since the Sarban era: SF1.2 deleted Dev, SF1.3 merged Sessions and Timeline into History and
/// folded Console into Agent's raw-stream mode, and U1.1 added Home. So the most-read page in the
/// repo overcounted, named three surfaces no user could reach, and omitted the tab the Face actually
/// OPENS ON. <c>docs/dev/adr/0004</c> and <c>face-go/STYLE.md</c> were both correct throughout — only
/// the README rotted, because nothing read it.</para>
///
/// <para>Same guard shape as <see cref="K7_2DocsVerbCoverageTests"/> and SC8.3 before it: read the
/// truth off the source a future session will edit, never hand-type the expected list. A test that
/// hand-typed "ten tabs" here would have stayed green through exactly the drift it exists to
/// catch.</para>
/// </summary>
public class K7_2ReadmeFrontPageTests
{
    [Fact]
    public void Readme_NamesTheTabsTheFaceActuallyHas()
    {
        var tabs = FaceTabNames();
        Assert.True(tabs.Count >= 5, $"only {tabs.Count} tabs parsed out of model.go - the scan is broken, not the README");

        var claimed = ReadmeTabRun();
        Assert.True(claimed.SequenceEqual(tabs),
            "README.md's dashboard section names [" + string.Join(" · ", claimed) +
            "] but face-go/internal/tui/model.go ships [" + string.Join(" · ", tabs) +
            "] - fix the README (or the tab, if the tab is what changed).");
    }

    [Fact]
    public void Readme_CountsTheTabsItLists()
    {
        var expected = NumberWord(FaceTabNames().Count);
        var doc = File.ReadAllText(ReadmePath());
        Assert.True(Regex.IsMatch(doc, @"\b" + expected + @" tabs\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)),
            $"README.md should say \"{expected} tabs\" - the Face ships {FaceTabNames().Count}. " +
            "This is the half that was wrong at v0.4.0: the list had drifted AND the count with it.");
    }

    /// <summary>
    /// Every <see cref="SessionOutcome"/> the run loop can reach is either a row in the README's
    /// "what it does when a session ends" table or listed here as deliberately absent. At v0.4.0 the
    /// table was missing <c>AuthFailed</c> - a dead credential parks the run for good
    /// (<c>SessionRunner.cs:396-403</c> goes straight to NeedsHuman, no battery, no backoff) - and
    /// <c>BlockedUntil</c>, which sleeps the loop to an instant a session named. Both change what the
    /// owner has to DO, on the page whose whole promise is that a run can be left alone.
    /// </summary>
    [Fact]
    public void Readme_ExplainsEveryOutcomeTheLoopCanReach()
    {
        // The three the table leaves out on purpose: the owner caused them and is standing right
        // there. `AgentError` is not on this list - it is a row, because nobody causes it.
        var byTheOwner = new HashSet<string>(StringComparer.Ordinal) { "KilledByUser", "Interrupted" };

        var doc = File.ReadAllText(ReadmePath());
        var missing = Enum.GetNames<SessionOutcome>()
            .Where(o => !byTheOwner.Contains(o))
            .Where(o => !doc.Contains("`" + o + "`", StringComparison.Ordinal))
            .OrderBy(o => o, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "README.md's session-outcome table does not name these outcomes: " + string.Join(", ", missing) +
            " - add a row saying what the loop does next, or add it to byTheOwner with the reason.");
    }

    /// <summary>The ten names, in tab order, read off the Go array that renders the strip. Source-
    /// scanned rather than asked of a running Face: the README is a static document and the test that
    /// guards it must not need a terminal.</summary>
    private static List<string> FaceTabNames()
    {
        var model = Path.Combine(RepoRoot(), "face-go", "internal", "tui", "model.go");
        var m = Regex.Match(File.ReadAllText(model), @"var\s+tabNames\s*=\s*\[tabCount\]string\{(?<body>[^}]*)\}",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
        Assert.True(m.Success, "could not find `var tabNames = [tabCount]string{...}` in " + model);

        return Regex.Matches(m.Groups["body"].Value, "\"(?<name>[^\"]+)\"",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2))
            .Select(x => x.Groups["name"].Value).ToList();
    }

    /// <summary>The dashboard section's run of tab names: everything between "tabs:" and the em-dash
    /// that ends the list, split on the interpunct. Anchored on punctuation the sentence needs anyway
    /// rather than on any tab's name, so the test cannot pass by agreeing with itself.</summary>
    private static List<string> ReadmeTabRun()
    {
        var doc = File.ReadAllText(ReadmePath());
        var m = Regex.Match(doc, @"\btabs:(?<run>[^—]+)—",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
        Assert.True(m.Success,
            "README.md has no `<N> tabs: A · B · ... —` run in the dashboard section. " +
            "The list is machine-checked; keep it as one interpunct-separated run.");

        return m.Groups["run"].Value
            .Split('·')
            .Select(s => s.Replace("\r", " ").Replace("\n", " ").Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string NumberWord(int n) => n switch
    {
        8 => "Eight", 9 => "Nine", 10 => "Ten", 11 => "Eleven", 12 => "Twelve", 13 => "Thirteen",
        _ => n.ToString(),
    };

    private static string ReadmePath() => Path.Combine(RepoRoot(), "README.md");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate repo root (Conductor.slnx)");
    }
}
