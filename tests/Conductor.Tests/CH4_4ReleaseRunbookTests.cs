using System.Reflection;

using Conductor.Core.Release;

namespace Conductor.Tests;

/// <summary>
/// CH4.4 - the owner runbook, generated instead of written.
///
/// <para><b>What these assert, and why by reflection.</b> KS12.3's runbook was hand-written, six of
/// its seven acts went unperformed, and DV7.3 - the next hand-written one - only found that out by
/// accident. The failure is not that a document was wrong; it is that a document cannot notice an
/// act it never mentioned. So the bar here is a PROPERTY over the engine's whole act vocabulary,
/// derived from the const fields themselves rather than from a list written next to them: every act
/// this engine declares is in one of the two orders, and every one of them gets a section in the
/// rendered document. Add a tenth act and forget to wire it, and these fail the same day.</para>
/// </summary>
public class CH4_4ReleaseRunbookTests
{
    /// <summary>Every public string constant on a type - the engine's own vocabulary, read off the
    /// engine rather than restated here, which is the whole point.</summary>
    private static List<string> Vocabulary(Type type) =>
        [.. type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];

    private static IReadOnlyList<ReleaseAct> AllActs(string? tag = "0.6.0") =>
    [
        .. ReleasePerform.MechanicalOrder.Select(n => new ReleaseAct(
            n, ReleaseAct.Mechanical, ReleaseAct.Ready, "the " + n + " act, planned", ["a detail line"])),
        .. ReleasePerform.OwnerActs(new OwnerFacts(
            tag, "master", "owner/name", ["9491891fe700463ba0d876c06280cce2"], AnyConductorLive: false)),
    ];

    private static IReadOnlyList<ReleaseCheck> AllChecks() =>
        [.. Vocabulary(typeof(ReleasePreflight))
            .Select(n => new ReleaseCheck(n, ReleaseCheck.Ok, "the " + n + " line, measured", ["how it was measured"]))];

    private static RunbookFacts Facts(
        IReadOnlyList<ReleaseAct>? acts = null, IReadOnlyList<ReleaseCheck>? checks = null, string? tag = "0.6.0") =>
        new(PlanName: "Charkh - the wheel", Repo: "C:/code/conductor", Branch: "feat/charkh",
            BaseBranch: "master", Tag: tag, InstalledEngine: "0.5.0+e60ae79c",
            GeneratedUtc: "2026-08-27 04:00:00Z", Checks: checks ?? AllChecks(), Acts: acts ?? AllActs(tag));

    /// <summary>The vocabulary tripwire, and it fires before any rendering happens: an act the engine
    /// declares but never orders is an act no verb performs and no document mentions.</summary>
    [Fact]
    public void EveryActTheEngineDeclaresIsInOneOfTheTwoOrders()
    {
        var declared = Vocabulary(typeof(ReleasePerform)).ToHashSet(StringComparer.Ordinal);
        var ordered = ReleasePerform.MechanicalOrder.Concat(ReleasePerform.OwnerOrder)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared, ordered);
    }

    /// <summary>The property that replaces the hand-written runbook: every declared act has its own
    /// section, found by its heading rather than by the word appearing somewhere on the page - "tag"
    /// and "merge" are ordinary English and would pass a substring test by accident.</summary>
    [Fact]
    public void TheRunbookGivesEveryDeclaredActItsOwnSection()
    {
        var document = ReleaseRunbook.Render(Facts());

        foreach (var act in Vocabulary(typeof(ReleasePerform)))
            Assert.Contains("### " + act + " \u2014", document, StringComparison.Ordinal);
    }

    /// <summary>The same, for the six measured lines.</summary>
    [Fact]
    public void TheRunbookGivesEveryDeclaredPreconditionItsOwnRow()
    {
        var document = ReleaseRunbook.Render(Facts());

        foreach (var check in Vocabulary(typeof(ReleasePreflight)))
            Assert.Contains("| `" + check + "` |", document, StringComparison.Ordinal);
    }

    /// <summary>KS12.3's actual failure mode, stated as an assertion: an act that needs a person is
    /// never rendered in the vocabulary of one that has been handled.</summary>
    [Fact]
    public void NoOwnerActIsEverRenderedAsDoneOrAsNothing()
    {
        var owner = ReleasePerform.OwnerActs(new OwnerFacts(
            "0.6.0", "master", "owner/name", [], AnyConductorLive: false));
        var document = ReleaseRunbook.Render(Facts(acts: owner));

        Assert.Equal(ReleasePerform.OwnerOrder.Count, owner.Count);
        foreach (var act in owner)
        {
            Assert.Equal(ReleaseAct.Stopped, act.State);
            Assert.Contains("### " + act.Name + " \u2014 **YOURS**", document, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("**done**", document, StringComparison.Ordinal);
        Assert.DoesNotContain("**already true**", document, StringComparison.Ordinal);
    }

    /// <summary>The commands are IN the document. A runbook that named the acts but made the reader
    /// go and find the invocation would be the prose it replaces.</summary>
    [Fact]
    public void TheOwnerActsCarryTheCommandsTheOwnerTypes()
    {
        var document = ReleaseRunbook.Render(Facts());

        Assert.Contains("`conductor release perform --tag 0.6.0 --yes`", document, StringComparison.Ordinal);
        Assert.Contains("`git push origin master`", document, StringComparison.Ordinal);
        Assert.Contains("tools/install.ps1", document, StringComparison.Ordinal);
        Assert.Contains("conductor github sync --backfill 9491891fe700463ba0d876c06280cce2", document, StringComparison.Ordinal);
    }

    /// <summary>An unnamed release leaves the hole visible in the command rather than inventing a
    /// number - the one judgement the whole verb exists to refuse.</summary>
    [Fact]
    public void AnUnnamedReleaseKeepsThePlaceholderAndSaysTheNumberIsYours()
    {
        var document = ReleaseRunbook.Render(Facts(tag: null));

        Assert.Contains("conductor release perform --tag <x.y.z> --yes", document, StringComparison.Ordinal);
        Assert.Contains("the version number is yours", document, StringComparison.Ordinal);
        Assert.DoesNotContain("--tag 0.6.0", document, StringComparison.Ordinal);
    }

    /// <summary>Measured on the CH4.4 rig: a detail line that OPENS with a command token and then
    /// carries on as a sentence is prose, and code-spanning the whole paragraph made it unreadable.
    /// A line that already carries a backtick was written as markdown by whoever wrote the act, and
    /// is left exactly as it is rather than being re-marked up around its own spans.</summary>
    [Fact]
    public void ASentenceThatMerelyOpensWithACommandIsNotCodeSpanned()
    {
        var prose = "tools/install.ps1 stops the courier at step 0 and puts it back on the new engine "
            + "- re-check courier status after the reinstall";
        var authored = "tools/install.ps1, then `conductor version` to confirm it matches the tag";
        var real = "git push origin master";
        var acts = new[] { new ReleaseAct("publish", ReleaseAct.Owner, ReleaseAct.Stopped, "h", [prose, authored, real]) };

        var document = ReleaseRunbook.Render(Facts(acts: acts));

        Assert.Contains("- " + prose, document, StringComparison.Ordinal);
        Assert.Contains("- " + authored, document, StringComparison.Ordinal);
        Assert.Contains("- `" + real + "`", document, StringComparison.Ordinal);
    }

    /// <summary>Generated twice from the same facts, byte for byte. The timestamp is an input, not a
    /// clock reading, so a regenerated runbook diffs to exactly what changed in the tree.</summary>
    [Fact]
    public void TheSameFactsRenderTheSameBytes()
    {
        Assert.Equal(ReleaseRunbook.Render(Facts()), ReleaseRunbook.Render(Facts()));
    }

    /// <summary>A headline is whatever the machine said - a git message, a path, a branch name. One
    /// pipe in it would silently eat the column after it and the table would still LOOK fine, which
    /// is the failure this project keeps paying for.</summary>
    [Fact]
    public void AMeasuredHeadlineCarryingAPipeCannotBreakTheTable()
    {
        var checks = new[] { new ReleaseCheck("merge", ReleaseCheck.Fail, "ahead 3 | behind 1", []) };
        var document = ReleaseRunbook.Render(Facts(checks: checks));

        var row = document.Split('\n').Single(l => l.StartsWith("| **RED** | `merge` |", StringComparison.Ordinal));
        // Three columns means four UNESCAPED delimiters. The escaped one is still a pipe character,
        // so counting raw pipes would agree with a broken table just as readily.
        Assert.Equal(4, row.Replace("\\|", "", StringComparison.Ordinal).Count(c => c == '|'));
        Assert.Contains("ahead 3 \\| behind 1", row, StringComparison.Ordinal);
    }

    /// <summary>It says, twice and in the reader's own words, that it performed nothing - and it
    /// says what exit 2 means, because "the era-close finished" and "the verb exited 0" are not the
    /// same sentence and reading one for the other is how a document gets believed.</summary>
    [Fact]
    public void TheDocumentSaysItPerformedNothingAndWhatExitTwoMeans()
    {
        var document = ReleaseRunbook.Render(Facts());

        Assert.Contains("Nothing in this document was performed.", document, StringComparison.Ordinal);
        Assert.Contains("Nothing was merged, tagged, moved, installed or pushed", document, StringComparison.Ordinal);
        Assert.Contains("Exit **2** is what a finished era-close looks like", document, StringComparison.Ordinal);
    }
}
