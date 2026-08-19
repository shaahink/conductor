using System.Reflection;
using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// KS12.2 — the level below the verb, which nothing was pinning.
///
/// <para>This suite has now learned the same lesson three times, one layer down each time. SC8.3:
/// the completion script's verb list was hand-typed, so <c>version</c> shipped missing from it and the
/// test stayed green — fixed by reading the list off <c>Program.cs</c>. K7.2: <c>docs/cli.md</c> had
/// no such guard and rotted the same way, with <c>budget</c> and <c>money</c> — the two verbs the
/// release notes led with — absent from the page. KS3.3: <c>docs/plan-config.md</c> called itself
/// "the full schema" while missing nine keys, fixed by deriving the expectation from
/// <c>PlanKeySchema</c>.</para>
///
/// <para><b>The flags were still hand-tended.</b> <see cref="SF7_1DocsMatchRealityTests"/>'s README
/// part pins one direction — every flag the README *writes* must be declared by the command it is
/// written on — and that is the direction a wrong doc fails. The direction a *silent* doc fails had
/// no test at all, and measuring it at KS12.2 found **41 long options across 13 verbs** that the CLI
/// reference had never named, including `task --evidence` and `task --blocked-until`, which this
/// repo's own session prompts instruct agents to use.</para>
///
/// <para><b>Scope, chosen so the bar is satisfiable.</b> Long options only (<c>--flag</c>): a short
/// alias travels in the same template and a reference that names one has named the pair. Hidden
/// commands are out, exactly as in <see cref="K7_2DocsVerbCoverageTests"/> — a verb you cannot reach
/// from <c>--help</c> is not a verb the reference owes a row. Inherited options count once: they are
/// declared on <see cref="Conductor.Commands.PlanSettings"/> and every verb has them.</para>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    [Fact]
    public void TheCliReferenceNamesEveryLongOptionAShippedVerbDeclares()
    {
        var declared = DeclaredLongOptions();

        Assert.True(declared.Count > 60,
            $"only {declared.Count} long options were reflected out of the shipped commands - the " +
            "scan is broken, not the docs");

        var undocumented = UndocumentedOptions(Doc("docs", "cli.md"), declared);

        Assert.True(undocumented.Count == 0,
            $"docs/cli.md never names {undocumented.Count} long option(s) that a shipped verb " +
            $"declares: {string.Join(", ", undocumented)} - give each one a mention on the verb's " +
            "row, or remove it from the command. A flag nobody is told about is a flag nobody uses, " +
            "and this is the direction the README pin cannot see.");
    }

    /// <summary>The pin proving the pin. A docs test that cannot fail is decoration, and the failure
    /// this one must catch is a flag quietly leaving the page — so take one out here and demand the
    /// derivation names exactly it, on each of three verbs whose rows are written differently.</summary>
    [Fact]
    public void RemovingOneDocumentedFlagMakesTheDerivationNameThatExactOption()
    {
        var doc = Doc("docs", "cli.md");
        var declared = DeclaredLongOptions();
        Assert.Empty(UndocumentedOptions(doc, declared));

        foreach (var flag in new[] { "--evidence", "--purpose", "--no-control-plane" })
        {
            Assert.Contains(flag, declared);

            // Blank the flag wherever the page writes it, leaving the rest of the document intact.
            var stale = doc.Replace(flag, "--" + flag.TrimStart('-') + "-was-here", StringComparison.Ordinal);
            Assert.NotEqual(doc, stale);

            Assert.Equal([flag], UndocumentedOptions(stale, declared));
        }
    }

    /// <summary>Every long option the given document does not name, sorted.</summary>
    private static IReadOnlyList<string> UndocumentedOptions(string doc, IReadOnlyCollection<string> declared)
        => [.. declared.Where(f => !NamesOption(doc, f)).OrderBy(f => f, StringComparer.Ordinal)];

    /// <summary>An option counts as documented when the page writes it with a word boundary after it,
    /// so <c>`--note`</c> and <c>`--note &lt;TEXT&gt;`</c> both count and <c>--notify</c> can never
    /// stand in for <c>--note</c>. Deliberately looser than the verb bar about the code span: half
    /// these flags are written inside a fenced block (the <c>run</c> section) where a backtick would
    /// be wrong.</summary>
    private static bool NamesOption(string doc, string flag)
        => Regex.IsMatch(doc, Regex.Escape(flag) + "(?![A-Za-z0-9-])",
            RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>Every long option declared by a settings type of a verb <c>Program.cs</c> registers and
    /// does not hide.
    ///
    /// <para>Reflected rather than source-scanned — unlike the verb list, which cannot be reflected
    /// because Spectre's <c>CommandApp</c> keeps its configuration private. The verb-to-type mapping
    /// still comes from <c>Program.cs</c> for that reason; from the type onward, the settings class is
    /// the generic argument of <c>Command&lt;T&gt;</c>/<c>AsyncCommand&lt;T&gt;</c> and its
    /// <c>CommandOption</c> attributes are the truth.</para></summary>
    private static IReadOnlyCollection<string> DeclaredLongOptions()
    {
        var flags = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var type in ShippedCommandTypes())
        {
            var settings = SettingsTypeOf(type);
            if (settings is null) continue;

            foreach (var property in settings.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                foreach (var attribute in property.GetCustomAttributesData()
                             .Where(a => a.AttributeType.Name == "CommandOptionAttribute"))
                {
                    // Spectre keeps the parsed template private, so read the constructor argument -
                    // the same route SF7_1DocsMatchRealityTests.Readme.cs takes.
                    if (attribute.ConstructorArguments.Count == 0) continue;
                    if (attribute.ConstructorArguments[0].Value is not string template) continue;

                    foreach (var token in template.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Split('|'))
                    {
                        var name = token.Trim();
                        if (name.StartsWith("--", StringComparison.Ordinal) && name.Length > 2) flags.Add(name);
                    }
                }
        }

        return flags;
    }

    /// <summary>The command classes <c>Program.cs</c> registers without <c>.IsHidden()</c>, resolved to
    /// types in the shipped assembly. Mirrors <see cref="K7_2DocsVerbCoverageTests"/>'s scanner
    /// deliberately rather than sharing it: two bars that share a helper can be relaxed together by
    /// one edit, which is the failure this whole suite exists to prevent.</summary>
    private static IReadOnlyList<Type> ShippedCommandTypes()
    {
        var assembly = typeof(Conductor.Commands.PlanSettings).Assembly;
        var program = File.ReadAllLines(Path.Combine(RepoRoot(), "src", "Conductor", "Program.cs"));
        var types = new List<Type>();

        foreach (var line in program)
        {
            var m = Regex.Match(line, @"AddCommand<(?<type>\w+)>\(""[a-z][a-z0-9-]*""\)",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
            if (!m.Success || line.Contains(".IsHidden()", StringComparison.Ordinal)) continue;

            var name = m.Groups["type"].Value;
            var type = assembly.GetTypes().FirstOrDefault(t => t.Name == name);

            Assert.True(type is not null,
                $"Program.cs registers {name} and the shipped assembly declares no such type - the " +
                "scan is broken, not the docs");
            types.Add(type!);
        }

        Assert.True(types.Count > 30,
            $"only {types.Count} command types resolved out of Program.cs - the scan is broken");
        return types;
    }
}
