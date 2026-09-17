using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Conductor.Commands;
using Conductor.Core.Courier;

namespace Conductor.Tests;

/// <summary>PK6.2 — what Peyk added, pinned where the reader looks for it. The general derivations
/// (every verb, every option, every subverb, every plan key) already demand most of it; the first two
/// facts prove this era's surface is INSIDE those nets and that each net names exactly the Peyk item a
/// doc drops. The rest is surface no reflection over the CLI can see - the courier's lives, its loopback
/// paths, the files in its home, the keep-alive interval, the seams, the message templates and the ADR
/// that amends 0008 - so each is derived from the constant or the source that owns it, and each has its
/// own negative control. A property rather than a list wherever the vocabulary grows (trap 22): a new
/// <see cref="CourierLife"/>, a new loopback path or a new notify event fails here until a doc names it.</summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    private static readonly string[] SmallNumberWords =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
        "nineteen", "twenty", "twenty-one", "twenty-two", "twenty-three", "twenty-four", "twenty-five",
    ];

    private const string Adr0008 = "0008-the-courier-outlives-the-run.md";
    private const string Adr0009 = "0009-the-courier-is-its-own-binary-and-one-wire.md";

    // ────────────────────────────── the verbs, switches and keys ─────────

    [Fact]
    public void EveryVerbSwitchSubverbAndKeyPeykAddedIsInsideTheDerivedNetAndDocumentedInBothReferences()
    {
        var cli = Doc("docs", "cli.md");
        var reference = OperatorCommandReference();

        var shipped = ShippedVerbs();
        foreach (var verb in PeykVerbs)
        {
            Assert.Contains(verb, shipped);
            Assert.True(NamedAsCode(cli, verb), $"docs/cli.md does not name `{verb}`");
            Assert.True(NamedAsCode(reference, verb), $"docs/operating.md §2 does not name `{verb}`");
        }

        var declared = DeclaredLongOptions();
        var options = PeykOptions();
        Assert.Subset(new HashSet<string>(declared, StringComparer.Ordinal), new HashSet<string>(options, StringComparer.Ordinal));
        Assert.Empty(UndocumentedOptions(cli, options));
        Assert.Empty(UndocumentedOptions(reference, options));

        var subverbs = DeclaredSubverbs().Where(p => PeykVerbs.Contains(p.Verb, StringComparer.Ordinal)).ToList();
        Assert.Contains(("room", "import"), subverbs);
        Assert.Empty(UndocumentedSubverbs(cli, subverbs));
        Assert.Empty(UndocumentedSubverbs(reference, subverbs));

        Assert.DoesNotContain("stages.deploys", UndocumentedPlanKeys(Doc("docs", "plan-config.md")));
    }

    [Fact]
    public void DroppingOnePeykItemFromADocMakesEachDerivationNameExactlyThatItem()
    {
        // A verb: blank `say` in §2 and leave `room` alone.
        var reference = OperatorCommandReference();
        var noSay = Regex.Replace(reference, @"`(?<c>(?:conductor\s+)?)say(?=[`\s\\])", "`${c}say-was-here",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));
        Assert.NotEqual(reference, noSay);
        Assert.Equal(["say"], PeykVerbs.Where(v => !NamedAsCode(noSay, v)));

        // A switch: --reply-to is gone from the page, every other Peyk option still there.
        var cli = Doc("docs", "cli.md");
        var noReply = cli.Replace("--reply-to", "--reply-was-here", StringComparison.Ordinal);
        Assert.Equal(["--reply-to"], UndocumentedOptions(noReply, PeykOptions()));

        // A subverb: `room import` blanked inside code spans only.
        var subverbs = DeclaredSubverbs().Where(p => PeykVerbs.Contains(p.Verb, StringComparer.Ordinal)).ToList();
        var noImport = Regex.Replace(cli, @"`[^`\n]+`", m =>
                Regex.Replace(m.Value, @"(?<![A-Za-z0-9-])import(?![A-Za-z0-9-])", "import-was-here",
                    RegexOptions.None, TimeSpan.FromSeconds(5)),
            RegexOptions.None, TimeSpan.FromSeconds(10));
        Assert.Equal(["room import"], UndocumentedSubverbs(noImport, subverbs));

        // A key: the `deploys` row deleted.
        var config = Doc("docs", "plan-config.md");
        var noDeploys = string.Join('\n', config.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("| `deploys` |", StringComparison.Ordinal)));
        Assert.NotEqual(config, noDeploys);
        Assert.Equal(["stages.deploys"], UndocumentedPlanKeys(noDeploys));
    }

    // ────────────────────────────── the courier's heartbeat ─────────

    /// <summary>Every life <see cref="CourierVitals"/> can report, in the words it reports it with, is
    /// on the <c>courier status</c> row. Read off <see cref="CourierVitals.Describe"/> itself, so a new
    /// life (or a renamed one) is demanded here the moment it can be printed.</summary>
    [Fact]
    public void TheCourierStatusRowNamesEveryLifeTheHeartbeatCanReportInItsOwnWords()
    {
        var row = CliRow("| `courier status` |");
        Assert.Empty(UnnamedLives(row));

        var stale = row.Replace("`stale", "`st4le", StringComparison.Ordinal);
        Assert.NotEqual(row, stale);
        Assert.Equal(["stale"], UnnamedLives(stale));
    }

    /// <summary>The five-minute promise is the task XML's interval, not a number typed twice; and the
    /// switch that replaces a live courier is the installer's own parameter.</summary>
    [Fact]
    public void TheKeepAliveIntervalAndTheCourierOnlySwitchAreTheOnesTheTaskAndTheInstallerDeclare()
    {
        var phrase = KeepAlivePhrase(CourierTask.KeepAliveInterval);
        Assert.Equal("keep-alive trigger every five minutes", phrase);
        foreach (var doc in new[] { Doc("docs", "cli.md"), Doc("docs", "operating.md") })
            Assert.Contains(phrase, doc, StringComparison.Ordinal);
        // Negative control: the phrase moves with the interval, so a changed trigger turns this red.
        Assert.DoesNotContain(KeepAlivePhrase("PT10M"), Doc("docs", "cli.md"), StringComparison.Ordinal);

        Assert.Matches(@"\[switch\]\$CourierOnly\b", Doc("tools", "install.ps1"));
        foreach (var doc in new[] { Doc("docs", "cli.md"), Doc("docs", "operating.md"), Doc("ARCHITECTURE.md") })
            Assert.Matches(@"-CourierOnly\b", doc);
    }

    // ────────────────────────────── the loopback and the home ─────────

    [Fact]
    public void EveryLoopbackPathAndTheProtocolVersionAreNamedInTheCliReferenceAndTheArchitecture()
    {
        var paths = typeof(CourierEndpoint).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Path", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.Contains("/send", paths);
        Assert.True(paths.Count >= 6, $"only {paths.Count} loopback paths reflected - the scan is broken");

        var cli = Doc("docs", "cli.md");
        var architecture = Doc("ARCHITECTURE.md");
        Assert.Empty(UnnamedPaths(cli, paths));
        Assert.Empty(UnnamedPaths(architecture, paths));
        Assert.Contains($"Protocol {CourierProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)} ", cli, StringComparison.Ordinal);
        Assert.Contains($"`Version = {CourierProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)}`", architecture, StringComparison.Ordinal);

        var noReact = architecture.Replace("/react`", "/react-was-here`", StringComparison.Ordinal);
        Assert.Equal(["/react"], UnnamedPaths(noReact, paths));
    }

    [Fact]
    public void EveryFileTheCourierHomeHoldsIsNamedWhereTheCourierStateIsDescribed()
    {
        var names = CourierHomeEntries();
        Assert.Contains("messages.jsonl", names);
        Assert.Contains("rooms", names);

        foreach (var (doc, text) in new[] { ("docs/cli.md", Doc("docs", "cli.md")), ("ARCHITECTURE.md", Doc("ARCHITECTURE.md")) })
            Assert.True(UnnamedHomeEntries(text, names).Count == 0,
                $"{doc} does not name: {string.Join(", ", UnnamedHomeEntries(text, names))}");

        var cli = Doc("docs", "cli.md");
        var noLedger = cli.Replace("messages.jsonl", "messages-was-here.jsonl", StringComparison.Ordinal);
        Assert.Equal(["messages.jsonl"], UnnamedHomeEntries(noLedger, names));
    }

    // ────────────────────────────── the seams ─────────

    /// <summary>ARCHITECTURE.md says how many <c>public interface I*</c> Core declares and lists them;
    /// the count was stale for a whole era once. Counted from the source, as the document tells a reader
    /// to count it.</summary>
    [Fact]
    public void TheArchitectureSeamCountAndTableAreTheInterfacesCoreDeclares()
    {
        var declared = CoreInterfaces();
        Assert.Contains("ICourierDesk", declared);
        Assert.True(declared.Count > 10, $"only {declared.Count} interfaces scanned - the scan is broken");

        var architecture = Doc("ARCHITECTURE.md");
        Assert.Empty(SeamDrift(architecture, declared));

        var claimed = SmallNumberWords[declared.Count];
        var fewer = architecture.Replace($"declares exactly **{claimed}**", $"declares exactly **{SmallNumberWords[declared.Count - 1]}**", StringComparison.Ordinal);
        Assert.NotEqual(architecture, fewer);
        Assert.Equal([$"says {SmallNumberWords[declared.Count - 1]}, the source declares {declared.Count}"], SeamDrift(fewer, declared));

        var noDesk = string.Join('\n', architecture.Split('\n')
            .Where(l => !l.StartsWith("| `ICourierDesk`", StringComparison.Ordinal)));
        Assert.Equal(["ICourierDesk"], SeamDrift(noDesk, declared));
    }

    // ────────────────────────────── the message templates ─────────

    [Fact]
    public void TheTemplatesSectionNamesEveryNotifyEventTheComposerRendersIncludingTheCards()
    {
        var events = NotifyEvents();
        Assert.Contains("checkpoint-card", events);
        Assert.Contains("stage-card", events);

        var section = Section(Doc("docs", "plan-config.md"), "## `templatesDir`", "\n## ");
        Assert.Contains("<templatesDir>/notify/<event>.md", section, StringComparison.Ordinal);
        Assert.Empty(UnnamedEvents(section, events));

        var noStageCard = section.Replace("`stage-card`", "`stage-card-was-here`", StringComparison.Ordinal);
        Assert.Equal(["stage-card"], UnnamedEvents(noStageCard, events));
    }

    // ────────────────────────────── ADR-0009 ─────────

    /// <summary>ADR-0009 amends 0008: it must restate 0008's conditions (as many as 0008 says it has,
    /// each by its own title) and add exactly the two Peyk needs, D3's and D4's, and the index and 0008
    /// itself must point at it.</summary>
    [Fact]
    public void Adr0009RestatesEveryConditionOf0008ByTitleAndAddsTheTwoNewOnes()
    {
        var old = Doc("docs", "dev", "adr", Adr0008);
        var adr = Doc("docs", "dev", "adr", Adr0009);
        var conditions = ConditionTitles(old);
        Assert.Equal(4, conditions.Count);
        Assert.Empty(AdrDrift(adr, conditions));

        Assert.Contains("adr/" + Adr0009, Doc("docs", "dev", "README.md"), StringComparison.Ordinal);
        Assert.Contains(Adr0009, old, StringComparison.Ordinal);
        Assert.Contains("docs/dev/adr/" + Adr0009, Doc("ARCHITECTURE.md"), StringComparison.Ordinal);

        // Negative controls: a condition dropped, and a new one missing.
        var noIngress = adr.Replace(conditions[1], "a condition that was here", StringComparison.Ordinal);
        Assert.Equal([conditions[1]], AdrDrift(noIngress, conditions));
        var noSix = Regex.Replace(adr, @"(?m)^### 6\. ", "#### was six. ", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.Equal(["5 numbered conditions, expected 6", "no new condition names D4"], AdrDrift(noSix, conditions));
    }

    // ────────────────────────────── helpers ─────────

    private static readonly string[] PeykVerbs = ["say", "room"];

    /// <summary>The long options this era declared: all of <c>say</c>'s and <c>room</c>'s, the claim's
    /// <c>--tell</c> and the release preflight's <c>--courier-task</c>.</summary>
    private static IReadOnlyList<string> PeykOptions()
    {
        static IEnumerable<string> Of(Type settings) => settings
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(p => p.GetCustomAttributesData())
            .Where(a => a.AttributeType.Name == "CommandOptionAttribute")
            .SelectMany(a => ((string)a.ConstructorArguments[0].Value!).Split(' ')[0].Split('|'))
            .Where(t => t.StartsWith("--", StringComparison.Ordinal));

        var all = Of(typeof(SayCommand.Settings)).Concat(Of(typeof(RoomCommand.Settings))).ToList();
        foreach (var (type, flag) in new[] { (typeof(TaskCommand.Settings), "--tell"), (typeof(ReleaseCommand.Settings), "--courier-task") })
        {
            Assert.Contains(flag, Of(type));
            all.Add(flag);
        }
        Assert.Contains("--reply-to", all);
        return [.. all.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)];
    }

    private static string CliRow(string start)
        => Doc("docs", "cli.md").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Single(l => l.StartsWith(start, StringComparison.Ordinal));

    /// <summary>The leading words of each life's line, as <see cref="CourierVitals.Describe"/> prints them,
    /// that the row does not open a code span with.</summary>
    private static IReadOnlyList<string> UnnamedLives(string row)
    {
        var now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        var record = new CourierPresence(CourierProtocol.Version, 4242, "engine", "conductor-courier.exe", null,
            now.AddHours(-1), null, now.AddMinutes(-1));
        return [.. Enum.GetValues<CourierLife>()
            .Select(life => new CourierVitals(life, record, now.AddMinutes(-10)).Describe(now).Split(" (")[0])
            .Distinct(StringComparer.Ordinal)
            .Where(phrase => !row.Contains("`" + phrase, StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)];
    }

    private static string KeepAlivePhrase(string interval)
        => $"keep-alive trigger every {SmallNumberWords[(int)XmlConvert.ToTimeSpan(interval).TotalMinutes]} minutes";

    /// <summary>Each loopback path not written as a code span of its own or after its HTTP verb.</summary>
    private static IReadOnlyList<string> UnnamedPaths(string doc, IEnumerable<string> paths)
        => [.. paths.Where(p => !Regex.IsMatch(doc, "`(?:(?:GET|POST) )?" + Regex.Escape(p) + "`",
                RegexOptions.None, TimeSpan.FromSeconds(5)))
            .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)];

    /// <summary>Every file and directory name <see cref="CourierHome"/> and <see cref="Rooms"/> declare
    /// inside the courier home (not the home's own name), plus the heartbeat's JSON field.</summary>
    private static IReadOnlyList<string> CourierHomeEntries()
    {
        var consts = typeof(CourierHome).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)
                        && (f.Name.EndsWith("FileName", StringComparison.Ordinal) || f.Name.EndsWith("DirName", StringComparison.Ordinal))
                        && f.Name != nameof(CourierHome.DirName))
            .Select(f => (string)f.GetRawConstantValue()!);
        return [.. consts
            .Append(Rooms.DirName)
            .Append(JsonNamingPolicy.CamelCase.ConvertName(nameof(CourierPresence.LastPollUtc)))
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>Each entry the page never writes as a whole token inside a code span.</summary>
    private static IReadOnlyList<string> UnnamedHomeEntries(string doc, IEnumerable<string> names)
    {
        var spans = Regex.Matches(doc, "`(?<span>[^`\n]+)`", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups["span"].Value).ToList();
        return [.. names.Where(n => !spans.Exists(span => Regex.IsMatch(span,
                "(?<![A-Za-z0-9.-])" + Regex.Escape(n) + "(?![A-Za-z0-9.-])", RegexOptions.None, TimeSpan.FromSeconds(5))))
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> CoreInterfaces()
    {
        var core = Path.Combine(RepoRoot(), "src", "Conductor.Core");
        var sep = Path.DirectorySeparatorChar;
        return [.. Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?m)^\s*public\s+(?:partial\s+)?interface\s+(?<name>I[A-Z]\w*)",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5)).Select(m => m.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>What the seams section gets wrong about the declared interfaces: the count it says in
    /// words, and each interface with no table row.</summary>
    private static IReadOnlyList<string> SeamDrift(string architecture, IReadOnlyCollection<string> declared)
    {
        var problems = new List<string>();
        var seams = Section(architecture.Replace("\r\n", "\n", StringComparison.Ordinal), "## The seams", "\n## ");
        var m = Regex.Match(seams, @"declares exactly \*\*(?<word>[a-z-]+)\*\* `public interface I\*`",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));
        if (!m.Success)
            problems.Add("no \"declares exactly **N** `public interface I*`\" sentence");
        else if (Array.IndexOf(SmallNumberWords, m.Groups["word"].Value) != declared.Count)
            problems.Add($"says {m.Groups["word"].Value}, the source declares {declared.Count}");
        foreach (var name in declared)
            if (!Regex.IsMatch(seams, @"(?m)^\|\s*`" + Regex.Escape(name) + "`", RegexOptions.None, TimeSpan.FromSeconds(5)))
                problems.Add(name);
        return problems;
    }

    /// <summary>Every event name a <c>ComposeAsync("name", ...)</c> call renders, from the source.</summary>
    private static IReadOnlyList<string> NotifyEvents()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var sep = Path.DirectorySeparatorChar;
        return [.. Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"ComposeAsync\(""(?<e>[a-z-]+)""",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5)).Select(m => m.Groups["e"].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(e => e, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> UnnamedEvents(string section, IEnumerable<string> events)
        => [.. events.Where(e => !section.Contains("`" + e + "`", StringComparison.Ordinal))];

    /// <summary>0008's condition titles, as many as its own "N conditions make this consistent" sentence says.</summary>
    private static IReadOnlyList<string> ConditionTitles(string adr)
    {
        var said = Regex.Match(adr, @"(?<word>[A-Z][a-z]+) conditions make this consistent",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));
        Assert.True(said.Success, "0008 no longer says how many conditions it has");
        var count = Array.IndexOf(SmallNumberWords, said.Groups["word"].Value.ToLowerInvariant());
        return [.. Regex.Matches(adr, @"(?m)^### (?<n>\d+)\. (?<title>[^\r\n]+?)\s*$",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5))
            .Where(m => int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) <= count)
            .Select(m => m.Groups["title"].Value)];
    }

    /// <summary>What 0009 gets wrong as an amendment: an 0008 condition it does not quote by title, a
    /// numbered-condition count other than 0008's plus two, and a new condition that does not name D3/D4.</summary>
    private static IReadOnlyList<string> AdrDrift(string adr, IReadOnlyList<string> conditions)
    {
        var problems = conditions.Where(t => !adr.Contains(t, StringComparison.Ordinal)).ToList();
        var headings = Regex.Matches(adr, @"(?m)^### (?<n>\d+)\. (?<title>[^\r\n]+)",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5))
            .Select(m => (N: int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture), Title: m.Groups["title"].Value))
            .ToList();
        if (headings.Count != conditions.Count + 2)
            problems.Add($"{headings.Count} numbered conditions, expected {conditions.Count + 2}");
        var added = headings.Where(h => h.N > conditions.Count).Select(h => h.Title).ToList();
        foreach (var decision in new[] { "D3", "D4" })
            if (!added.Exists(t => t.Contains("NEW", StringComparison.Ordinal) && t.Contains($"({decision})", StringComparison.Ordinal)))
                problems.Add($"no new condition names {decision}");
        return problems;
    }
}
