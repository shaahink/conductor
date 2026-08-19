using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Conductor.Tests;

/// <summary>
/// KS6.1 — the curated Roslynator set, as an executable contract.
/// </summary>
/// <remarks>
/// The package ships 217 rules. Turning them all on would have produced a wall of noise that the next
/// session silences wholesale, so <c>.editorconfig</c> adopts a curated set BY NAME. The curation is the
/// deliverable, which means the thing worth defending is not any one rule — it is the shape: every rule a
/// deliberate decision, every decision carrying the design property it buys or the reason it does not,
/// and the set small enough that a human still reads it.
/// <para/>
/// The vendor's own master switch was supposed to hold that line and does not: measured 2026-08-19,
/// <c>roslynator_analyzers.enabled_by_default = false</c> had no effect from .editorconfig, nor from a
/// repo-root .globalconfig reaching the compiler through <c>EditorConfigFiles</c> — a seeded probe still
/// failed the build on RCS1102, which this repo never adopted. So these tests are the switch.
/// <para/>
/// Both failure modes they exist for are quiet ones. An unknown diagnostic id in an .editorconfig is
/// ignored without a word — no warning, no error, nothing in the build log — so one typo turns an adopted
/// rule into a comment that looks enforced for the rest of the repo's life, and the only symptom is a
/// class of bug that stops being caught. And a rule the package enables at Warning becomes an error here
/// the moment somebody writes the shape that trips it, whether or not anyone chose to have it. Both are
/// checked against the analyzer assemblies themselves, at the version read from the central pin — bump
/// the package without revisiting the set and this goes red rather than surprising a later session.
/// </remarks>
public sealed class KS6_1AnalyzerCurationTests
{
    /// <summary>The band the set has to stay inside. Not a target — a tripwire: a set that has crept past
    /// the ceiling is a firehose again, and one that has fallen to ten was silenced a rule at a time. It
    /// landed at 33 rather than the plan's "roughly 25" because five of them are rules the package forces
    /// a decision on, and all five were worth keeping.</summary>
    private const int MinCurated = 22;
    private const int MaxCurated = 36;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string[] EditorConfig() => File.ReadAllLines(Path.Combine(RepoRoot(), ".editorconfig"));

    /// <summary>The lines under one section header, up to the next header.</summary>
    private static List<string> Section(string header)
    {
        var lines = new List<string>();
        var inside = false;
        foreach (var raw in EditorConfig())
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inside = string.Equals(line, header, StringComparison.Ordinal);
                continue;
            }
            if (inside) lines.Add(raw);
        }
        return lines;
    }

    /// <summary>
    /// Any configured diagnostic, not just a well-formed <c>RCS####</c>.
    /// </summary>
    /// <remarks>
    /// Matching the id shape here was the first version and it had a hole: <c>RCS1O43</c> — letter O for
    /// zero — did not match, so the line was invisible to every test in this class while being equally
    /// invisible to the compiler. A typo that the checker cannot see is exactly the failure this class
    /// exists to catch, so the id is captured loosely and judged afterwards by its prefix.
    /// </remarks>
    private static readonly Regex Configured =
        new(@"^dotnet_diagnostic\.(?<id>[^.\s]+)\.severity\s*=\s*(?<severity>\w+)\s*(#\s*(?<why>.*))?$",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));

    private static List<(string Id, string Severity, string Why)> CuratedRules(string header)
    {
        var found = new List<(string, string, string)>();
        foreach (var line in Section(header))
        {
            var m = Configured.Match(line.Trim());
            if (!m.Success) continue;
            var id = m.Groups["id"].Value;
            if (!id.StartsWith("RCS", StringComparison.OrdinalIgnoreCase)) continue;
            found.Add((id, m.Groups["severity"].Value, m.Groups["why"].Value.Trim()));
        }
        return found;
    }

    /// <summary>The inert switch stays out. It reads as protection and provides none — see the type remarks.</summary>
    [Fact]
    public void TheMasterSwitchThatDoesNothingIsNotWrittenDownAsThoughItDid()
    {
        var config = string.Join("\n", EditorConfig());
        Assert.DoesNotContain("\nroslynator_analyzers.enabled_by_default", config, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAdoptedRuleIsAnErrorAndCarriesItsReason()
    {
        var rules = Adopted();
        Assert.NotEmpty(rules);
        foreach (var (id, severity, why) in rules)
        {
            Assert.True(severity == "error", $"{id} is adopted at '{severity}'. The curated set IS the build gate; " +
                                             "a rule worth naming is worth failing on, and one that is not belongs at none " +
                                             "with a reason, not at a severity nobody ever sees.");
            Assert.True(why.Length >= 20, $"{id} has no reason beside it. Every adoption records the design property it buys — " +
                                          "that sentence is what lets the next session judge the rule instead of inheriting it.");
        }
    }

    [Fact]
    public void EveryRelaxationInTheTestTreeCarriesItsReasonToo()
    {
        foreach (var (id, severity, why) in CuratedRules("[tests/**/*.cs]"))
        {
            Assert.True(severity == "none", $"{id}: the test section only ever relaxes; adopt in [*.cs] instead.");
            Assert.True(why.Length >= 20, $"{id} is switched off for tests with no reason. Switching a rule off is the move " +
                                          "this stage exists to make expensive — say what a test does differently, or leave it on.");
        }
    }

    /// <summary>The rules turned ON in the main tree — refusals live in the same section but are not the set.</summary>
    private static List<(string Id, string Severity, string Why)> Adopted() =>
        CuratedRules("[*.cs]").Where(r => r.Severity != "none").ToList();

    [Fact]
    public void TheSetStaysSmallEnoughToRead()
    {
        var n = Adopted().Count;
        Assert.True(n is >= MinCurated and <= MaxCurated,
            $"{n} rules adopted; the band is {MinCurated}-{MaxCurated}. Above it the set is a firehose again, " +
            "below it somebody has been deleting rules one at a time. Moving the band is a decision, not a fix.");
    }

    [Fact]
    public void EveryAdoptedIdIsARealDiagnosticInThePinnedAnalyzer()
    {
        var shipped = Shipped();
        foreach (var (id, _, _) in CuratedRules("[*.cs]").Concat(CuratedRules("[tests/**/*.cs]")))
        {
            Assert.True(shipped.ContainsKey(id),
                $"{id} is not a diagnostic the pinned Roslynator knows. An unknown id in .editorconfig is ignored in " +
                "silence, so this line has been enforcing nothing. Fix the id or drop the line.");
        }
    }

    /// <summary>
    /// The one that makes "everything else off" true rather than declared.
    /// </summary>
    /// <remarks>
    /// Measured on 2026-08-19: <c>roslynator_analyzers.enabled_by_default = false</c> does not take
    /// effect in this toolchain — not from .editorconfig, and not from a repo-root .globalconfig reaching
    /// the compiler through EditorConfigFiles either. A seeded probe still failed the build on RCS1102, a
    /// rule this repo never adopted. So the master switch is not what keeps the set curated; this test is.
    /// <para/>
    /// What can actually break this build is narrow and knowable: a rule the package enables by default at
    /// Warning or above, because <c>TreatWarningsAsErrors</c> turns it into an error. Every one of those
    /// must be a decision written down — adopted at error, or refused at none with the reason. The list
    /// comes from the analyzer assemblies themselves, so a package bump that introduces a new one goes red
    /// here until somebody decides, instead of surfacing as a mystery build failure three sessions later.
    /// </remarks>
    [Fact]
    public void EveryRuleThatCouldFailThisBuildIsEitherAdoptedOrRefusedByName()
    {
        var configured = CuratedRules("[*.cs]").ToDictionary(r => r.Id, r => r.Severity, StringComparer.Ordinal);
        var undecided = Shipped()
            .Where(r => r.Value.EnabledByDefault && r.Value.Severity >= DiagnosticSeverity.Warning)
            .Select(r => r.Key)
            .Where(id => !configured.ContainsKey(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(undecided.Count == 0,
            $"{string.Join(", ", undecided)} — the pinned Roslynator enables these at Warning or above, and this " +
            "repo makes warnings errors, so each can fail the build without anyone having chosen it. Adopt it at " +
            "error with the design property it buys, or set it to none with the reason it is not worth having.");
    }

    [Fact]
    public void ARefusalIsAlwaysARecordedDecision()
    {
        var shipped = Shipped();
        foreach (var (id, severity, why) in CuratedRules("[*.cs]").Where(r => r.Severity == "none"))
        {
            Assert.True(shipped.ContainsKey(id), $"{id} is refused but is not a rule the pinned analyzer ships.");
            Assert.True(why.Length >= 20, $"{id} is switched off with no reason. Refusing a rule is a decision this " +
                                          "stage makes expensive on purpose: say what it costs and what it fails to buy.");
            Assert.Equal("none", severity);
        }
    }

    /// <summary>The Roslynator version pinned centrally, so the analyzers under test are the build's own.</summary>
    private static string PinnedVersion()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Packages.props"));
        var m = Regex.Match(props, "PackageVersion\\s+Include=\"Roslynator\\.Analyzers\"\\s+Version=\"(?<version>[^\"]+)\"",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
        Assert.True(m.Success, "Roslynator.Analyzers is not pinned in Directory.Packages.props.");
        return m.Groups["version"].Value;
    }

    [Fact]
    public void ThePackageReachesEveryProject()
    {
        var build = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        Assert.Contains("Roslynator.Analyzers", build, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(PinnedVersion()));
    }

    /// <summary>
    /// What the pinned analyzer actually ships: every diagnostic it can report, with the severity and the
    /// enabled-by-default flag it was compiled with.
    /// </summary>
    /// <remarks>
    /// Read out of the analyzer assemblies rather than a list committed beside the config, because a list
    /// is something a session can edit to agree with its own typo and a shipped assembly is not. This is
    /// the ground truth the rest of the class compares .editorconfig against.
    /// </remarks>
    private static IReadOnlyDictionary<string, (DiagnosticSeverity Severity, bool EnabledByDefault)> Shipped()
    {
        if (_shipped is not null) return _shipped;

        var dir = AnalyzerDirectory();
        var found = new Dictionary<string, (DiagnosticSeverity, bool)>(StringComparer.Ordinal);
        var assembly = Assembly.LoadFrom(Path.Combine(dir, "Roslynator.CSharp.Analyzers.dll"));
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(DiagnosticAnalyzer).IsAssignableFrom(type)) continue;

            DiagnosticAnalyzer analyzer;
            try { analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(type)!; }
            catch (MissingMethodException) { continue; }

            foreach (var d in analyzer.SupportedDiagnostics)
                found[d.Id] = (d.DefaultSeverity, d.IsEnabledByDefault);
        }

        Assert.True(found.Count > 100,
            $"only {found.Count} diagnostics found under {dir} — the reflection, not the curation, is what broke.");
        _shipped = found;
        return found;
    }

    private static IReadOnlyDictionary<string, (DiagnosticSeverity Severity, bool EnabledByDefault)>? _shipped;

    private static string AnalyzerDirectory()
    {
        foreach (var root in NugetRoots())
        {
            var dir = Path.Combine(root, "roslynator.analyzers", PinnedVersion(), "analyzers", "dotnet", "roslyn4.7", "cs");
            if (Directory.Exists(dir)) return dir;
        }

        throw new DirectoryNotFoundException(
            $"Roslynator.Analyzers {PinnedVersion()} is in no package cache ({string.Join(", ", NugetRoots())}). " +
            "The solution cannot have restored, so this is a broken environment rather than a broken curation.");
    }

    private static IEnumerable<string> NugetRoots()
    {
        var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) yield return Path.Combine(profile, ".nuget", "packages");
    }
}
