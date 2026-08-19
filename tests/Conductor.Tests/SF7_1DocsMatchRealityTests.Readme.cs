using System.Reflection;
using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// KS10.2 — the README's fenced commands are checked against the PARSER, not against the prose.
///
/// <para>The front page is the only file most readers ever run a command out of, and it is the file
/// with no test behind it: <see cref="K7_2ReadmeFrontPageTests"/> pins the tab list and the outcome
/// table, and nothing at all pinned the commands. That gap is not theoretical here — K7.2 found
/// `status --no-llm` written in four live scripts and a docs cheat sheet for a flag this engine has
/// never had, and it only looked harmless because Spectre was dropping unknown options on the floor.
/// Program.cs now calls <c>UseStrictParsing()</c>, so a stale flag on the front page is a command
/// that EXITS NON-ZERO for the first person who copies it.</para>
///
/// <para>So: every fenced <c>conductor …</c> line in README.md names a verb <c>Program.cs</c>
/// registers and does not hide, and every option on it is declared by that command's settings type.
/// Both sides are read off the shipped code — the verb list from the source a future session edits,
/// the option list by reflecting the settings types out of the built assembly — so this test cannot
/// pass by agreeing with a list somebody typed.</para>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    /// <summary>Spectre adds these to every command; no settings type declares them.</summary>
    private static readonly HashSet<string> ParserBuiltins =
        new(StringComparer.Ordinal) { "-h", "--help", "-v", "--version" };

    [Fact]
    public void EveryFencedReadmeCommandNamesAVerbTheEngineRegisters()
    {
        var commands = FencedConductorCommands();
        Assert.True(commands.Count >= 8,
            $"only {commands.Count} fenced `conductor` commands found in README.md - the scanner is " +
            "broken, not the README.");

        var shipped = ShippedVerbs();
        var strangers = commands
            .Select(c => c.Tokens.ElementAtOrDefault(1))
            .Where(v => v is not null && !v.StartsWith('-'))
            .Where(v => !shipped.Contains(v!))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(strangers.Count == 0,
            "README.md quotes commands the engine does not ship: " + string.Join(", ", strangers) +
            " - the front page is where a stale verb costs the most, because it is the first one " +
            "anybody types.");
    }

    /// <summary>The half that would have caught `status --no-llm`. Every option written on the front
    /// page is declared by the settings type the command actually binds, so an option that is renamed
    /// or dropped fails here rather than in a reader's terminal.</summary>
    [Fact]
    public void EveryFlagTheReadmeWritesIsDeclaredByTheCommandItIsWrittenOn()
    {
        var wrong = new List<string>();

        foreach (var cmd in FencedConductorCommands())
        {
            var argv = EngineRewrites(cmd.Tokens.Skip(1).ToList());
            var verb = argv.ElementAtOrDefault(0);
            if (verb is null || verb.StartsWith('-')) continue;

            var flags = argv.Skip(1)
                .Where(t => t.Length > 1 && t[0] == '-' && t != "--")
                .Where(t => !ParserBuiltins.Contains(t))
                .ToList();
            if (flags.Count == 0) continue;

            var declared = DeclaredOptions(verb);
            foreach (var flag in flags.Where(f => !declared.Contains(f)))
                wrong.Add($"`{cmd.Line}` -> {verb} declares no {flag} (it declares: " +
                          string.Join(" ", declared.OrderBy(o => o, StringComparer.Ordinal)) + ")");
        }

        Assert.True(wrong.Count == 0,
            "README.md writes options the command does not declare. Strict parsing means each of " +
            "these EXITS NON-ZERO when a reader copies it:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>KS2.1's claim, on the page that makes it. The README tells a reader to type
    /// <c>conductor</c> with nothing after it; that only works because <c>Program.cs</c> rewrites an
    /// empty argv to a hidden <c>hub</c> verb. Pin both ends: if the rewrite is ever removed the front
    /// page's first command becomes the help screen again, silently.</summary>
    [Fact]
    public void TheFirstCommandTheReadmeOffersIsBareConductorAndProgramRewritesItToTheHub()
    {
        var bare = FencedConductorCommands().Where(c => c.Tokens.Count == 1).ToList();
        Assert.True(bare.Count > 0,
            "README.md offers no fenced bare `conductor` command. KS2.1 made typing the tool's own " +
            "name the front door; the front page has to say so, or the change reaches nobody.");

        var program = Doc("src", "Conductor", "Program.cs");
        Assert.Contains("AddCommand<HubCommand>(\"hub\").IsHidden()", program, StringComparison.Ordinal);
        Assert.True(
            Regex.IsMatch(program, @"argv\.Length\s*==\s*0\s*\?\s*\[\s*""hub""\s*\]",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5)),
            "Program.cs no longer rewrites an empty argv to the hub, so plain `conductor` does not " +
            "open it - but README.md still tells the reader to type exactly that.");
    }

    /// <summary>KS12.2 — the argv the PARSER sees, not the argv the reader types. Two of the front
    /// page's verbs are spelled as two words and rewritten to a hidden one-word command before
    /// Spectre is handed anything: <c>history export</c> (KS8.2) and <c>run close|adopt</c> (KS0.2).
    /// A pin that resolves the verb from the first word alone reads <c>conductor history export
    /// --atif</c> as <c>history</c> and calls a working command broken — which is what it did the
    /// first time the README documented the ATIF export.
    ///
    /// <para>The <c>history export</c> arm runs the ENGINE'S OWN rewrite by reflection, so the two
    /// cannot drift apart. <c>run close|adopt</c> is a local function inside top-level statements and
    /// has no reflectable name, so it is mirrored here and
    /// <see cref="ProgramStillPerformsTheArgvRewritesThisPinMirrors"/> is what stops the mirror
    /// rotting.</para></summary>
    private static IReadOnlyList<string> EngineRewrites(IReadOnlyList<string> argv)
    {
        if (argv.Count >= 2 && argv[0] == "run" && argv[1] is "close" or "adopt")
            return ["run-record", .. argv.Skip(1)];

        var rewrite = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "conductor.dll"))
            .GetType("Conductor.VerbRewrites")?
            .GetMethod("HistoryExport", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(rewrite is not null,
            "Conductor.VerbRewrites.HistoryExport is gone from the engine assembly. If the rewrite " +
            "moved, point this pin at the new one - do not drop it, or `conductor history export` " +
            "stops being checked on the page that teaches it.");

        return (string[])rewrite!.Invoke(null, [argv.ToArray()])!;
    }

    /// <summary>The pin on the mirror. <see cref="EngineRewrites"/> can only be honest while
    /// <c>Program.cs</c> still applies both rewrites to the argv it hands the parser; if either call
    /// is dropped, the two-word spelling stops working and this test says so before a reader's
    /// terminal does.</summary>
    [Fact]
    public void ProgramStillPerformsTheArgvRewritesThisPinMirrors()
    {
        var program = Doc("src", "Conductor", "Program.cs");
        var dispatch = program.Split('\n').FirstOrDefault(l => l.Contains("app.RunAsync(", StringComparison.Ordinal));
        Assert.NotNull(dispatch);

        foreach (var rewrite in new[] { "VerbRewrites.HistoryExport", "RewriteRunRecordVerbs", "HubWhenBare" })
            Assert.Contains(rewrite, dispatch!, StringComparison.Ordinal);

        // And the engine's own rewrite does what the mirror assumes it does.
        Assert.Equal(["history-export", "e9e21d10", "--atif"],
            EngineRewrites(["history", "export", "e9e21d10", "--atif"]));
        Assert.Equal(["run-record", "close", "e9e21d10"], EngineRewrites(["run", "close", "e9e21d10"]));
        Assert.Equal(["history", "--json"], EngineRewrites(["history", "--json"]));
    }

    private sealed record FencedCommand(string Line, IReadOnlyList<string> Tokens);

    /// <summary>Every line inside a fenced block that starts a <c>conductor</c> invocation. Trailing
    /// <c>#</c> comments are stripped (the README annotates its blocks that way) and quoted arguments
    /// are kept whole so <c>--from-idea "…"</c> is one token, not five.</summary>
    private static List<FencedCommand> FencedConductorCommands()
    {
        var commands = new List<FencedCommand>();
        var inFence = false;

        foreach (var raw in File.ReadAllLines(Path.Combine(RepoRoot(), "README.md")))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (!inFence) continue;

            var text = line.Trim();
            if (!text.StartsWith("conductor", StringComparison.Ordinal)) continue;
            if (text.Length > 9 && text[9] is not (' ' or '\t')) continue;   // conductor-face, etc.

            var hash = IndexOfComment(text);
            if (hash >= 0) text = text[..hash].TrimEnd();

            commands.Add(new FencedCommand(text, Tokenise(text)));
        }

        return commands;
    }

    /// <summary>The first <c>#</c> that is not inside quotes — the README's block comments.</summary>
    private static int IndexOfComment(string text)
    {
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            else if (text[i] == '#' && !quoted) return i;
        }
        return -1;
    }

    private static List<string> Tokenise(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var ch in text)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>Verb -&gt; the command class <c>Program.cs</c> registers for it, hidden ones excluded.
    /// Source-scanned for the same reason <see cref="K7_2DocsVerbCoverageTests"/> is: Spectre keeps its
    /// configuration private, and the source is what a future session edits.</summary>
    /// <param name="includeHidden">Hidden verbs are excluded from "what the README may name", and
    /// included when a two-word spelling has already been rewritten to one — <c>history-export</c> is
    /// hidden precisely because nobody types it, and its options still have to be real.</param>
    private static Dictionary<string, string> RegisteredCommandTypes(bool includeHidden = false)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(RepoRoot(), "src", "Conductor", "Program.cs")))
        {
            var m = Regex.Match(line, @"AddCommand<(?<type>\w+)>\(""(?<verb>[a-z][a-z0-9-]*)""\)",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));
            if (!m.Success) continue;
            if (!includeHidden && line.Contains(".IsHidden()", StringComparison.Ordinal)) continue;
            map[m.Groups["verb"].Value] = m.Groups["type"].Value;
        }
        return map;
    }

    private static HashSet<string> ShippedVerbs() =>
        new(RegisteredCommandTypes().Keys, StringComparer.Ordinal);

    /// <summary>Every option template on the settings type a verb binds, walked up its base chain so
    /// the shared <c>-p|--plan</c> counts. Read by reflection off the built engine assembly rather than
    /// by grepping the command's source file: the binding is what the parser will do.</summary>
    private static HashSet<string> DeclaredOptions(string verb)
    {
        var registered = RegisteredCommandTypes(includeHidden: true);
        Assert.True(registered.ContainsKey(verb),
            $"Program.cs registers no command for `{verb}` - the README quotes it, or an argv " +
            "rewrite points at a verb that no longer exists.");
        var typeName = registered[verb];

        // The CLI assembly is in this test project's output directory (Conductor.Tests references
        // Conductor.csproj), exactly as K7_2StrictFlagParsingTests relies on - no path to guess.
        var dll = Path.Combine(AppContext.BaseDirectory, "conductor.dll");
        Assert.True(File.Exists(dll), $"app assembly not next to the tests: {dll}");
        var engine = Assembly.LoadFrom(dll);

        var command = engine.GetTypes().FirstOrDefault(t => t.Name == typeName);
        Assert.True(command is not null,
            $"Program.cs registers `{verb}` as {typeName} and no such type is in {engine.GetName().Name}");

        var settings = SettingsTypeOf(command!);
        Assert.True(settings is not null,
            $"{typeName} does not derive from Command<TSettings>/AsyncCommand<TSettings>, so the " +
            "options a README line writes cannot be checked. Teach this test the new shape rather " +
            "than dropping the verb.");

        var options = new HashSet<string>(StringComparer.Ordinal);
        for (var t = settings; t is not null && t != typeof(object); t = t.BaseType)
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic |
                                                 BindingFlags.Instance | BindingFlags.DeclaredOnly))
            foreach (var attr in prop.CustomAttributes.Where(a =>
                         a.AttributeType.Name == "CommandOptionAttribute"))
            {
                if (attr.ConstructorArguments.Count == 0) continue;
                if (attr.ConstructorArguments[0].Value is not string template) continue;

                // "-p|--plan <PLAN>" -> -p, --plan.  The value name is never typed as a flag.
                foreach (var alias in template.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
                             .Split('|', StringSplitOptions.RemoveEmptyEntries))
                    if (alias.StartsWith('-')) options.Add(alias);
            }

        return options;
    }

    private static Type? SettingsTypeOf(Type command)
    {
        for (var t = command.BaseType; t is not null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericArguments().Length == 1)
                return t.GetGenericArguments()[0];
        return null;
    }
}
