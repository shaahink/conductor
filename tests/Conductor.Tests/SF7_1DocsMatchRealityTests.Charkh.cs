using System.Reflection;
using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// CH3.3 — what Charkh added, and the two blind spots measuring it exposed.
///
/// <para>This suite keeps learning the same lesson one layer down. SC8.3: the completion script's
/// verb list was hand-typed. K7.2: <c>docs/cli.md</c> rotted the same way. KS3.3: the plan-config
/// page called itself "the full schema" while missing nine keys. KS12.2: 41 long options had never
/// been named. Each time the fix was to DERIVE the expectation. CH3.1 measured the layer below
/// KS12.2's and found the derivation still too loose in one direction; CH1.3 walked straight through
/// a gap in another.</para>
///
/// <para><b>The gap.</b> <c>TrackerDocNamesEveryRuntimeArtifactTheEngineCanWrite</c> scanned
/// <c>src/Conductor/</c> — the shell. CH1.3 put the writer of <c>ci-status.json</c> in
/// <c>Conductor.Core</c>, behind a <c>const</c>, and the bar stayed green while the artifact shipped
/// undocumented. Widening the scan to <c>src/</c> and following a const to its value surfaced
/// <b>six</b> artifacts across three eras, not one.</para>
///
/// <para><b>The looseness.</b> <c>TheCliReferenceNamesEveryLongOptionAShippedVerbDeclares</c> asks
/// whether the page names an option ANYWHERE. So <c>--home</c>, written on the <c>spend</c> row,
/// counted as documented for the six other verbs that declare it and whose rows never mentioned it.
/// A reader looking up <c>budget</c> does not read the whole page.</para>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    // ---------------------------------------------------------------- runtime artifacts

    /// <summary>The pin proving the pin, for the widened artifact scan. Take one name out of the
    /// runtime-files block and the derivation must come back holding exactly it — including
    /// <c>ci-status.json</c>, whose writer lives in the assembly the old scan never looked at.</summary>
    [Fact]
    public void RemovingOneDocumentedArtifactMakesTheDerivationNameThatExactFile()
    {
        var block = RuntimeFilesBlock(Doc("docs", "tracker.md"));
        var artifacts = RuntimeArtifactsTheEngineNames();

        // One const in Core (CH1.3's), one literal in Core, one directory - the three shapes
        // the widened scan added. `run.db` is deliberately not here: it lives under the state
        // HOME, not the plan's state dir, so it is not this derivation's to find.
        foreach (var name in new[] { "ci-status.json", "settings.session.json", "inbox" })
        {
            Assert.Contains(name, artifacts);

            // The mangled form must not CONTAIN the name, or blanking it blanks nothing.
            var stale = block.Replace(name, name[..1] + "~gone~" + name[1..], StringComparison.Ordinal);
            Assert.NotEqual(block, stale);

            var missing = artifacts
                .Where(a => !stale.Contains(a, StringComparison.Ordinal))
                .ToList();

            Assert.Equal([name], missing);
        }
    }

    // ---------------------------------------------------------------- flag placement

    /// <summary>CH3.1's finding, made a bar: an option must be named where the reader who is looking
    /// up THAT VERB will find it — the verb's own table row, its fenced help listing, or a paragraph
    /// that names the verb and the option together.</summary>
    [Fact]
    public void TheCliReferenceNamesEveryOptionOnTheRowOfTheVerbThatDeclaresIt()
    {
        var blocks = DocBlocks(Doc("docs", "cli.md"));
        var declared = OptionsByVerb();

        Assert.True(declared.Count > 30,
            $"only {declared.Count} verbs resolved out of Program.cs - the scan is broken, not the docs");

        var misplaced = MisplacedOptions(blocks, declared);

        Assert.True(misplaced.Count == 0,
            $"docs/cli.md names {misplaced.Count} option(s) nowhere near the verb that declares them: " +
            $"{string.Join(", ", misplaced)}. A reader looking up one verb does not read the whole " +
            "page, so an option written only on another verb's row is an option they will not find.");
    }

    /// <summary>The pin proving the pin. Blank an option out of the block that carries it and the
    /// derivation must name that exact verb-and-option pair — on three verbs whose rows are written
    /// three different ways: a table row, a fenced help listing, and the shared <c>--home</c>
    /// paragraph that is the reason this bar exists at all.</summary>
    [Fact]
    public void BlankingOneOptionMakesTheDerivationNameThatExactVerbAndOption()
    {
        var doc = Doc("docs", "cli.md");
        var declared = OptionsByVerb();
        Assert.Empty(MisplacedOptions(DocBlocks(doc), declared));

        foreach (var (verb, flag) in new[]
                 {
                     ("watches", "--ports"),      // a table row
                     ("run", "--detach"),         // a fenced help listing
                     ("budget", "--home"),        // the paragraph shared by every catalogue reader
                 })
        {
            Assert.Contains(flag, declared[verb]);

            var stale = doc.Replace(flag, "--" + flag.TrimStart('-') + "-was-here", StringComparison.Ordinal);
            Assert.NotEqual(doc, stale);

            var misplaced = MisplacedOptions(DocBlocks(stale), declared);
            Assert.Contains(verb + " " + flag, misplaced);
        }
    }

    // ---------------------------------------------------------------- the demo GIF caption

    /// <summary>CH2.1 extended the tour to the courier's tab, the inbox pane and the run switcher.
    /// The README went on saying "Seven screens" and listing the tour it had before — the front page
    /// of a project about docs that do not rot, describing a GIF it no longer matched, one day after
    /// the recording. CH2.2 made the recorder write down what it recorded FROM; this reads that file
    /// back and holds the caption to it.</summary>
    [Fact]
    public void TheReadmeCaptionCountsTheStopsTheDemoManifestRecords()
    {
        var stops = ManifestStops();

        Assert.True(stops is >= 3 and <= 20,
            $"docs/assets/demo.manifest.json records {stops} visits - the manifest is broken, not the caption");

        Assert.Equal(stops, CaptionStops(Doc("README.md")));
    }

    /// <summary>The pin proving the pin. A tour that gains or loses a stop must turn this red, and
    /// the caption is what a reader sees first — so seed it in both directions.</summary>
    [Fact]
    public void ACaptionThatCountsTheOldTourIsRed()
    {
        var readme = Doc("README.md");
        var stops = ManifestStops();
        Assert.Equal(stops, CaptionStops(readme));

        foreach (var wrong in new[] { "Seven", "Ten" })
        {
            var stale = Regex.Replace(readme, "<sub>[A-Z][a-z]+ stops", "<sub>" + wrong + " stops",
                RegexOptions.None, TimeSpan.FromSeconds(5));
            Assert.NotEqual(readme, stale);
            Assert.NotEqual(stops, CaptionStops(stale));
        }
    }

    // ---------------------------------------------------------------- derivations

    /// <summary>The fenced tree under <c>## Runtime files</c>, and nothing else: a name that happens
    /// to appear in an unrelated example elsewhere on the page is not documentation.</summary>
    private static string RuntimeFilesBlock(string doc)
    {
        var section = doc[doc.IndexOf("## Runtime files", StringComparison.Ordinal)..];
        var open = section.IndexOf("```", StringComparison.Ordinal);
        var close = section.IndexOf("```", open + 3, StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, "docs/tracker.md has no fenced tree under '## Runtime files'");
        return section[(open + 3)..close];
    }

    /// <summary>How many stops the recorder wrote into the manifest. Read as text rather than
    /// deserialised: the manifest is the recorder's own output, and a DTO here would be one more
    /// thing to keep in step with it.</summary>
    private static int ManifestStops()
    {
        var manifest = Doc("docs", "assets", "demo.manifest.json");
        var open = manifest.IndexOf("\"visits\"", StringComparison.Ordinal);
        Assert.True(open >= 0, "docs/assets/demo.manifest.json has no `visits` array");
        var close = manifest.IndexOf(']', open);
        var visits = manifest[open..close];
        // Quitting ends the tour; it is not a stop on it.
        return Regex.Matches(visits, "\"name\"", RegexOptions.None, TimeSpan.FromSeconds(5)).Count
             - Regex.Matches(visits, "\"quit\"", RegexOptions.None, TimeSpan.FromSeconds(5)).Count;
    }

    /// <summary>The number the caption says out loud, in words, or -1 when it says none.</summary>
    private static int CaptionStops(string readme)
    {
        var m = Regex.Match(readme, "<sub>(?<word>[A-Za-z]+) stops through the Face",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));
        if (!m.Success) return -1;
        string[] words =
        [
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen",
            "eighteen", "nineteen", "twenty",
        ];
        return Array.FindIndex(words,
            w => string.Equals(w, m.Groups["word"].Value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every "verb option" pair the page writes nowhere the verb is named, sorted.</summary>
    private static IReadOnlyList<string> MisplacedOptions(
        IReadOnlyList<string> blocks, IReadOnlyDictionary<string, IReadOnlyCollection<string>> declared)
    {
        var misplaced = new List<string>();

        foreach (var (verb, options) in declared)
        {
            var mine = blocks.Where(b => NamesVerb(b, verb)).ToList();
            foreach (var flag in options)
                if (!mine.Exists(b => NamesOptionHere(b, flag)))
                    misplaced.Add(verb + " " + flag);
        }

        misplaced.Sort(StringComparer.Ordinal);
        return misplaced;
    }

    private static bool NamesVerb(string block, string verb)
        => Regex.IsMatch(block, "(`|conductor )" + Regex.Escape(verb) + "(?![A-Za-z0-9-])",
               RegexOptions.None, TimeSpan.FromSeconds(5))
           || Regex.IsMatch(block, "\\A" + Regex.Escape(verb) + "  +",
               RegexOptions.None, TimeSpan.FromSeconds(5));

    private static bool NamesOptionHere(string block, string flag)
        => Regex.IsMatch(block, Regex.Escape(flag) + "(?![A-Za-z0-9-])",
            RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>The page as units of meaning. A table ROW is a block of its own — each row is a
    /// different verb, and letting a whole table count as one context would credit every verb in it
    /// with every option in it. A fenced listing is one block. Everything else is a paragraph, so a
    /// sentence naming eight verbs on one line and the option on the next stays one statement. The
    /// same split <c>tools/ch3/docs-surface-diff.py</c> makes, restated here rather than shared,
    /// because two bars that share a helper can be relaxed together by one edit.</summary>
    private static IReadOnlyList<string> DocBlocks(string doc)
    {
        var blocks = new List<string>();
        var current = new List<string>();
        var fenced = false;

        void Flush()
        {
            if (current.Count > 0) blocks.Add(string.Join("\n", current));
            current.Clear();
        }

        foreach (var raw in doc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                fenced = !fenced;
                continue;
            }
            if (!fenced && line.TrimStart().StartsWith('|'))
            {
                Flush();
                blocks.Add(line);
                continue;
            }
            if (line.Trim().Length == 0) Flush();
            else current.Add(line);
        }

        Flush();
        return blocks;
    }

    /// <summary>Verb to the long options its settings type declares. Deliberately a second scanner
    /// beside the one <c>Flags.cs</c> keeps: that one answers "does the page name this flag at all",
    /// this one answers "where", and a shared helper would let one edit relax both.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> OptionsByVerb()
    {
        var assembly = typeof(Conductor.Commands.PlanSettings).Assembly;
        var program = File.ReadAllLines(Path.Combine(RepoRoot(), "src", "Conductor", "Program.cs"));
        var byVerb = new SortedDictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

        foreach (var line in program)
        {
            var m = Regex.Match(line, "AddCommand<(?<type>\\w+)>\\(\"(?<verb>[a-z][a-z0-9-]*)\"\\)",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
            if (!m.Success || line.Contains(".IsHidden()", StringComparison.Ordinal)) continue;

            var type = assembly.GetTypes().FirstOrDefault(t => t.Name == m.Groups["type"].Value);
            var settings = type is null ? null : SettingsTypeOf(type);
            if (settings is null) continue;

            var flags = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var property in settings.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                foreach (var attribute in property.GetCustomAttributesData()
                             .Where(a => a.AttributeType.Name == "CommandOptionAttribute"))
                {
                    if (attribute.ConstructorArguments.Count == 0) continue;
                    if (attribute.ConstructorArguments[0].Value is not string template) continue;
                    foreach (var token in template.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Split('|'))
                    {
                        var name = token.Trim();
                        // -p/--plan and the two Spectre builtins ride on every verb by inheritance;
                        // documenting them on fifty rows would be noise, and cli.md says it once.
                        if (name.StartsWith("--", StringComparison.Ordinal) && name.Length > 2
                            && name is not ("--plan" or "--help" or "--version")) flags.Add(name);
                    }
                }

            if (flags.Count > 0) byVerb[m.Groups["verb"].Value] = flags;
        }

        return byVerb;
    }
}
