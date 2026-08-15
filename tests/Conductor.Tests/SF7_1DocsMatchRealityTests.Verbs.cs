using System.Text.RegularExpressions;
using Conductor.Core;

namespace Conductor.Tests;

/// <summary>
/// KS10.2 — the verb surface, pinned in both directions and on both pages.
///
/// <para><see cref="K7_2DocsVerbCoverageTests"/> pins one direction on one page: every shipped verb is
/// named in <c>docs/cli.md</c>. Three gaps outlived it, and the karvansara era widened all three —
/// it added eleven verbs and sub-verbs (<c>preflight</c>, <c>spend</c>, <c>github</c>, <c>watches</c>,
/// <c>catalogue</c>, <c>plan new</c>, <c>run close</c>, <c>run adopt</c>, <c>face --archive</c>, …)
/// while <c>docs/operating.md</c>'s "Full command reference" — the page an AGENT is pointed at, and the
/// one this repo's own session prompts cite — still described the Sarban surface.</para>
///
/// <list type="number">
/// <item>operating.md calls its §2 a FULL command reference. Nothing checked that it was full: five
/// verbs (<c>journey</c>, <c>heartbeat</c>, <c>demo</c>, <c>version</c>, <c>update</c>) plus every
/// machine-level verb Karvan and Karvansara added were missing when this test was written.</item>
/// <item>The converse direction, which no test had at all: a verb that is DELETED stays documented
/// forever, because K7.2 only ever asks whether the doc is missing something.</item>
/// <item>The control section is the operator's contract with a live run, so it is derived from
/// <see cref="ControlAction"/> — the vocabulary the engine can actually dispatch — the same way the
/// wake table is derived from <c>WatchReason</c>.</item>
/// </list>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    /// <summary>§2 says "Full command reference". A verb absent from it is a capability the agent
    /// reading that page does not know it has.</summary>
    [Fact]
    public void TheOperatingGuideFullCommandReferenceNamesEveryShippedVerb()
    {
        var verbs = ShippedVerbs();
        Assert.True(verbs.Count > 30,
            $"only {verbs.Count} verbs parsed out of Program.cs - the scan is broken, not the docs");

        // Only §2 counts. The page mentions verbs in workflows and safety rules too, and a passing
        // mention three sections away is not a reference entry - that leniency is exactly how
        // `history`, `ps`, `catalogue` and `spend` read as documented while §2 had no row for them.
        var section = Section(Doc("docs", "operating.md"), "## 2. ", "## 3. ");

        var missing = verbs.Where(v => !NamedAsCode(section, v))
            .OrderBy(v => v, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "docs/operating.md's \"Full command reference\" does not name these shipped verbs: " +
            string.Join(", ", missing) + " - give each a row, or stop calling §2 full.");
    }

    /// <summary>The direction K7.2 cannot see. Every verb <c>docs/cli.md</c> puts in the first cell of
    /// a reference-table row must be a verb the engine registers — otherwise a removed verb keeps its
    /// entry forever and the page teaches a command that exits non-zero.</summary>
    [Fact]
    public void TheCliReferenceDocumentsNoVerbTheEngineHasStoppedShipping()
    {
        var shipped = ShippedVerbs();

        var documented = Regex.Matches(Doc("docs", "cli.md"), @"(?m)^\|\s*`(?<span>[^`]+)`",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups["span"].Value.Split(' ', '/')[0].Trim())
            .Where(v => Regex.IsMatch(v, "^[a-z][a-z0-9-]*$", RegexOptions.None, TimeSpan.FromSeconds(5)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(documented.Count > 20,
            $"only {documented.Count} verb rows parsed out of docs/cli.md - the scan is broken, not the doc");

        var ghosts = documented.Where(v => !shipped.Contains(v))
            .OrderBy(v => v, StringComparer.Ordinal).ToList();

        Assert.True(ghosts.Count == 0,
            "docs/cli.md has reference rows for verbs Program.cs does not register: " +
            string.Join(", ", ghosts) + " - a deleted verb keeps its documentation forever unless " +
            "something asks this question. Delete the row, or un-hide the command.");
    }

    /// <summary>Both completion generators read one <c>Verbs</c> constant, and <c>B11_2Tests</c> pins
    /// that constant against <c>Program.cs</c>. What nothing pinned is the third place: a verb can be
    /// tab-completable and registered and still absent from the page a reader is sent to. This is the
    /// three-way agreement stated once — completion list, cli.md, operating.md §2.</summary>
    [Fact]
    public void TheCompletionListTheCliReferenceAndTheOperatingGuideAgreeOnTheVerbSet()
    {
        var completion = Doc("src", "Conductor", "Commands", "CompletionCommand.cs");
        var constant = Section(completion, "private const string Verbs =", ";");
        var completable = new HashSet<string>(
            Regex.Matches(constant, @"""(?<chunk>[^""]*)""", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5))
                .SelectMany(m => m.Groups["chunk"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            StringComparer.Ordinal);

        Assert.True(completable.Count > 30,
            $"only {completable.Count} verbs parsed out of CompletionCommand's Verbs constant - the " +
            "scan is broken, not the completion script");

        var cli = Doc("docs", "cli.md");
        var operating = Section(Doc("docs", "operating.md"), "## 2. ", "## 3. ");

        var undocumented = completable
            .Where(v => !NamedAsCode(cli, v) || !NamedAsCode(operating, v))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.True(undocumented.Count == 0,
            "these verbs tab-complete but are not on both doc pages (cli.md and operating.md §2): " +
            string.Join(", ", undocumented) + ". A verb you can only discover by pressing TAB is a " +
            "verb nobody finds.");
    }

    /// <summary>Every intent the engine can dispatch against a LIVE run has a row in operating.md's
    /// control section. Derived from <see cref="ControlAction"/> rather than from a typed list, the
    /// same shape as the <c>WatchReason</c> wake table: a new action the engine can execute and the
    /// operator was never told about is a red test, not a surprise at 3am.</summary>
    [Fact]
    public void TheControlSectionNamesEveryIntentTheEngineCanDispatch()
    {
        // The CLI verb that queues each action. Some are not one-to-one - `plan reload` queues
        // ReloadPlan, `rollover` queues SetRollover, `abort` is the only way to StopAfterSession from
        // the CLI - which is precisely why the mapping is written down instead of guessed.
        var verbFor = new Dictionary<ControlAction, string>
        {
            [ControlAction.PauseAfterSession] = "pause",
            [ControlAction.ResumeRun] = "resume",
            [ControlAction.AbortNow] = "abort",
            [ControlAction.SkipStage] = "skip",
            [ControlAction.KillSession] = "kill",
            [ControlAction.StopAfterSession] = "abort",
            [ControlAction.RetryStage] = "retry-stage",
            [ControlAction.Rollback] = "rollback",
            [ControlAction.PauseAfterStage] = "pause-after-stage",
            [ControlAction.Goto] = "goto",
            [ControlAction.Heartbeat] = "heartbeat",
            [ControlAction.ReloadPlan] = "plan reload",
            [ControlAction.SetRollover] = "rollover",
        };

        var control = Section(Doc("docs", "operating.md"), "### Control a LIVE run", "\n### ");

        foreach (var action in Enum.GetValues<ControlAction>())
        {
            Assert.True(verbFor.ContainsKey(action),
                $"ControlAction.{action} is new - give it a row in docs/operating.md's control " +
                "section and a verb here. An intent the engine can dispatch and the operator was " +
                "never told about is the whole failure mode this test exists for.");

            var verb = verbFor[action];
            Assert.True(NamedAsCode(control, verb.Split(' ')[0]),
                $"docs/operating.md's control section does not name `{verb}`, which is how " +
                $"ControlAction.{action} is reached from a terminal.");
        }
    }

    /// <summary>A verb counts as documented when it appears as a code span, never as bare prose — the
    /// same bar <see cref="K7_2DocsVerbCoverageTests"/> sets, and for the same reason: the trailing
    /// boundary is what stops <c>`tasks`</c> standing in for <c>task</c>.</summary>
    private static bool NamedAsCode(string doc, string verb) =>
        Regex.IsMatch(doc, @"`(?:conductor\s+)?" + Regex.Escape(verb) + @"(?:`|\s|\\)",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));

    private static string Section(string doc, string from, string to)
    {
        var start = doc.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find '{from.Trim()}' - the document was restructured");
        var end = doc.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        return end > start ? doc[start..end] : doc[start..];
    }
}
