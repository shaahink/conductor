using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// DV7.2 — the level below the flag, which nothing was pinning either.
///
/// <para>This suite keeps learning the same lesson one layer down. SC8.3 read the completion
/// script's verb list off <c>Program.cs</c>; K7.2 did the same for <c>docs/cli.md</c>; KS3.3 derived
/// the plan keys from <c>PlanKeySchema</c>; KS12.2 reflected the long options. Every one of those
/// derivations starts at <c>AddCommand&lt;T&gt;("verb")</c> — and <b>a subverb is not registered
/// there.</b> <c>conductor courier install</c> is a string in a switch inside
/// <c>CourierCommand</c>, invisible to all four bars.</para>
///
/// <para><b>What that cost, measured at DV7.2.</b> The courier's entire lifecycle —
/// <c>install</c>, <c>uninstall</c>, <c>restart</c>, <c>stop</c> — was absent from
/// <c>docs/operating.md</c>, the page an agent driving conductor is pointed at, for the whole era
/// that shipped it: a daemon you can only <c>run</c> by hand reads as the supported shape.
/// <c>inbox parked</c> — the dead-letter box, and the reason "a note is never dropped" is true — had
/// no row in <c>docs/cli.md</c>. And <c>github backfill</c>, an alias the engine accepts at
/// <c>GithubCommand.cs:88</c>, appeared in neither page. All four were green.</para>
///
/// <para><b>Where the truth is read from.</b> Spectre keeps its configuration private and a subverb
/// is not a type, so this is a source scan of the six command files that take a
/// <c>[CommandArgument(0, "[VERB]")]</c>, over the two shapes this repo actually dispatches with:
/// a switch arm (<c>"install" =&gt;</c>, <c>"" or "status" =&gt;</c>) and a guard clause
/// (<c>verb is not ("sync" or "backfill" or "sarif")</c>). The empty default arm is not a subverb.
/// Where an arm carries aliases — <c>"new" or "add" or "file"</c> — only the <b>first</b> spelling is
/// demanded: a reference owes one spelling per capability, and requiring <c>ls</c>, <c>close</c> and
/// <c>resolve</c> as well would buy noise rather than coverage.</para>
///
/// <para><b>What counts as documented,</b> chosen against how these pages are actually written rather
/// than against a shape that would force a rewrite: some rows spell the pair out
/// (<c>`inbox parked`</c>), others compress the family (<c>`bg`</c> … <c>`start|status|logs|stop`</c>,
/// <c>`plan new/set/reload/add-stage/import`</c>). So a subverb is documented when some line carries a
/// code span naming its parent verb <b>and</b> a code span on that same line names the subverb as a
/// whole token. Both halves must be inside backticks — prose mentioning "run" on the courier's row
/// must not stand in for the verb.</para>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    [Fact]
    public void TheCliReferenceNamesEverySubverbACommandDispatchesOn()
    {
        var declared = DeclaredSubverbs();

        Assert.True(declared.Count > 25,
            $"only {declared.Count} subverbs came out of the command sources - the scan is broken, " +
            "not the docs");

        var undocumented = UndocumentedSubverbs(Doc("docs", "cli.md"), declared);

        Assert.True(undocumented.Count == 0,
            $"docs/cli.md never names {undocumented.Count} subverb(s) the engine dispatches on: " +
            $"{string.Join(", ", undocumented)} - give each one a mention on its verb's row. " +
            "AddCommand<T>(\"verb\") does not see these, so no other bar in this suite can.");
    }

    /// <summary>Section 2 only, the same slice
    /// <see cref="TheOperatingGuideFullCommandReferenceNamesEveryShippedVerb"/> holds the top-level verbs to: it calls
    /// itself the full command reference, and an agent that cannot find <c>courier install</c> there
    /// concludes there is no such thing.</summary>
    [Fact]
    public void TheOperatorCommandReferenceNamesEverySubverbToo()
    {
        var declared = DeclaredSubverbs();
        var undocumented = UndocumentedSubverbs(OperatorCommandReference(), declared);

        Assert.True(undocumented.Count == 0,
            $"docs/operating.md section 2 never names {undocumented.Count} subverb(s): " +
            $"{string.Join(", ", undocumented)} - section 2 is the reference an agent driving " +
            "conductor is handed, and a lifecycle it cannot see is a lifecycle it will not use.");
    }

    /// <summary>The pin proving the pin. A docs test that cannot fail is decoration, so take one
    /// subverb off each page and demand the derivation names exactly it — chosen across three rows
    /// written three different ways: a spelled-out pair, a pipe-separated family, and a
    /// slash-separated one.</summary>
    [Fact]
    public void RemovingOneDocumentedSubverbMakesTheDerivationNameThatExactPair()
    {
        var declared = DeclaredSubverbs();
        var doc = Doc("docs", "cli.md");
        Assert.Empty(UndocumentedSubverbs(doc, declared));

        foreach (var (verb, sub) in new[] { ("courier", "install"), ("bg", "logs"), ("plan", "reload") })
        {
            Assert.Contains((verb, sub), declared);

            // Blank this subverb wherever the page writes it inside a code span, leaving every other
            // row - and the same word in prose - exactly as it was.
            var stale = Regex.Replace(doc, @"`[^`\n]+`", m =>
                Regex.Replace(m.Value, @"(?<![A-Za-z0-9-])" + Regex.Escape(sub) + @"(?![A-Za-z0-9-])",
                    sub + "-was-here", RegexOptions.None, TimeSpan.FromSeconds(5)),
                RegexOptions.None, TimeSpan.FromSeconds(10));
            Assert.NotEqual(doc, stale);

            var found = UndocumentedSubverbs(stale, declared);
            Assert.Contains($"{verb} {sub}", found);
        }
    }

    /// <summary>Every declared pair the given document does not name, as "verb subverb", sorted.</summary>
    private static IReadOnlyList<string> UndocumentedSubverbs(
        string doc, IReadOnlyCollection<(string Verb, string Sub)> declared)
        => [.. declared.Where(p => !NamesSubverb(doc, p.Verb, p.Sub))
                       .Select(p => $"{p.Verb} {p.Sub}")
                       .OrderBy(s => s, StringComparer.Ordinal)];

    /// <summary>Documented = one line carrying a code span that names the parent verb and a code span
    /// that names the subverb, both as whole tokens. One line because every one of these pages writes
    /// a verb family as a table row.</summary>
    private static bool NamesSubverb(string doc, string verb, string sub)
    {
        foreach (var line in doc.Split('\n'))
        {
            var spans = Regex.Matches(line, "`(?<span>[^`\n]+)`",
                                      RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5))
                             .Select(m => m.Groups["span"].Value).ToList();
            if (spans.Count == 0) continue;
            if (!spans.Any(s => Token(s, verb))) continue;
            if (spans.Any(s => Token(s, sub))) return true;
        }
        return false;
    }

    private static bool Token(string span, string word)
        => Regex.IsMatch(span, @"(?<![A-Za-z0-9-])" + Regex.Escape(word) + @"(?![A-Za-z0-9-])",
            RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>Section 2 of <c>docs/operating.md</c> — the full command reference, sliced the same
    /// way the top-level verb bar slices it.</summary>
    private static string OperatorCommandReference()
    {
        var doc = Doc("docs", "operating.md");
        var from = doc.IndexOf("## 2. ", StringComparison.Ordinal);
        var to = doc.IndexOf("## 3. ", StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from,
            "docs/operating.md no longer has a '## 2. ' .. '## 3. ' command reference - the slice is " +
            "broken, not the docs");
        return doc[from..to];
    }

    /// <summary>Every (verb, subverb) pair the shipped commands dispatch on. Source-scanned: a subverb
    /// is a string literal in a switch arm or a guard clause, not a registered type, so there is
    /// nothing to reflect.</summary>
    private static IReadOnlyCollection<(string Verb, string Sub)> DeclaredSubverbs()
    {
        var dir = Path.Combine(RepoRoot(), "src", "Conductor", "Commands");
        var pairs = new List<(string, string)>();

        foreach (var path in Directory.GetFiles(dir, "*Command.cs").OrderBy(p => p, StringComparer.Ordinal))
        {
            var source = File.ReadAllText(path);
            if (!source.Contains("""CommandArgument(0, "[VERB]")""", StringComparison.Ordinal)) continue;

            var verb = Path.GetFileNameWithoutExtension(path);
            verb = verb[..^"Command".Length].ToLowerInvariant();

            var subs = new List<string>();

            // Shape 1 - a switch arm: `"install" =>`, `"" or "status" =>`, `"new" or "add" =>`.
            // Only the first spelling is demanded; the empty default arm is not a subverb.
            foreach (Match m in Regex.Matches(source,
                         """^\s*"(?<sub>[a-z0-9-]*)"(?:\s+or\s+"[a-z0-9-]*")*\s+=>""",
                         RegexOptions.Multiline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5)))
            {
                var sub = m.Groups["sub"].Value;
                if (sub.Length > 0) subs.Add(sub);
            }

            // Shape 2 - a guard clause: `verb is not ("sync" or "backfill" or "sarif")`. Here every
            // alternative IS a spelling the engine accepts, so all of them are demanded.
            foreach (Match m in Regex.Matches(source,
                         """\bis\s+(?:not\s+)?\(\s*"(?<first>[a-z0-9-]+)"(?<rest>(?:\s+or\s+"[a-z0-9-]+")+)\s*\)""",
                         RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5)))
            {
                subs.Add(m.Groups["first"].Value);
                foreach (Match alt in Regex.Matches(m.Groups["rest"].Value, "\"(?<sub>[a-z0-9-]+)\"",
                             RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5)))
                    subs.Add(alt.Groups["sub"].Value);
            }

            foreach (var sub in subs.Distinct(StringComparer.Ordinal)) pairs.Add((verb, sub));
        }

        Assert.True(pairs.Count > 25,
            $"only {pairs.Count} (verb, subverb) pairs were scanned out of {dir} - the scan is broken");
        return pairs;
    }
}
