using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SF6.1 — the built-in session and fix templates carry the field lessons. Every assertion here is made
/// against the RENDERED prompt an agent actually receives, not against the source literal, because the
/// tools block is spliced in at render time and the multi-repo rule is conditional on the plan.
/// </summary>
/// <remarks>
/// Each case names the incident that bought it. They are cheap to keep and the failure mode they guard
/// is invisible: a lesson silently dropped from a template costs a whole session before anyone notices.
/// </remarks>
public class SF6_1TemplateLessonsTests
{
    private static PlanConfig Plan() => new()
    {
        Name = "Loom",
        Repo = @"C:\repo",
        Tracker = "LOOM-START.md",
        PlanDoc = "docs/proposal.md",
    };

    private static readonly StageConfig Stage = new() { Id = "L2", Title = "BodyFacts", Sessions = 3 };

    private static string Deliver(PlanConfig? plan = null) => new PromptBuilder(plan ?? Plan()).Deliver(Stage, 5, 2, 6);

    private static string Fix(PlanConfig? plan = null) => new PromptBuilder(plan ?? Plan())
        .Fix(Stage, 5, 2, 6, new PendingFix { FromSession = 4, GateFailures = "### Gate `build` FAILED", ProgressSummary = "commits: 0" });

    /// <summary>devcontext #9: the board sat entirely TODO through a 56-minute delivering session, and the
    /// owner's first question on opening the Face was why nothing was in progress.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MarkingInProgressComesBeforeTheFirstEdit(bool fixSession)
    {
        var p = fixSession ? Fix() : Deliver();

        Assert.Contains("conductor task --in-progress <id>", p, StringComparison.Ordinal);

        // Not merely present — present as an instruction that precedes editing.
        var marker = p.IndexOf("--in-progress", StringComparison.Ordinal);
        var beforeEdit = p.IndexOf("BEFORE your first edit", StringComparison.Ordinal);
        Assert.True(beforeEdit >= 0, "no template says when to mark in progress");
        Assert.True(Math.Abs(beforeEdit - marker) < 400, "the 'before your first edit' rule is not attached to the in-progress verb");
    }

    /// <summary>devcontext #8: a session wrote "CLAIMED" into the tracker at 02:13 and did not call the
    /// claim verb until 02:21. For eight minutes the run looked finished and the board was right to say TODO.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClaimingComesBeforeTheHandoffIsWritten(bool fixSession)
    {
        var p = fixSession ? Fix() : Deliver();

        Assert.Contains("conductor task --done <id> --evidence <path>", p, StringComparison.Ordinal);
        Assert.Contains("BEFORE you write the handoff", p, StringComparison.Ordinal);

        // The ordering has to survive in the step list too: claim, then hand off.
        var claim = p.IndexOf("--done <id> --evidence <path>", StringComparison.Ordinal);
        var handoff = p.IndexOf("handoff block", claim, StringComparison.Ordinal);
        Assert.True(handoff > claim, "the template tells the agent to write the handoff before claiming");
    }

    /// <summary>devcontext #8, second half: the MCP tools arrive deferred in the Claude Code harness, so the
    /// one mandatory channel gained a loading step. The fix asked for the CLI fallback on the SAME line.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredToolsNoteCarriesTheCliFallbackOnTheSameLine(bool fixSession)
    {
        var p = fixSession ? Fix() : Deliver();

        var line = p.Split('\n').FirstOrDefault(l => l.Contains("DEFERRED", StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.Contains("ToolSearch", line, StringComparison.Ordinal);
        Assert.Contains("CLI", line, StringComparison.Ordinal);
    }

    /// <summary>devcontext #5: a session blocked the foreground for 15 minutes and was correctly killed as
    /// stalled. It had the tools block; the rule was not attached to the step that runs the long thing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheGateBatteryStepItselfNamesConductorBg(bool fixSession)
    {
        var p = fixSession ? Fix() : Deliver();

        // Everything above the tools block is the template's own step list.
        var steps = p[..p.IndexOf("## Conductor tools", StringComparison.Ordinal)];
        var batteryStep = steps.Split('\n').FirstOrDefault(l => l.Contains("gate battery", StringComparison.Ordinal) && l.Contains("conductor bg", StringComparison.Ordinal));
        Assert.True(batteryStep is not null, "the step that runs the gate battery does not mention conductor bg");
        Assert.Contains("stall", batteryStep!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>SC3.3's landmine, restated where sessions write prose: a literal brace in a handoff comes
    /// back through prompt composition as an unresolved placeholder and parks the run.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BraceDisciplineIsStatedWhereTheAgentWritesProse(bool fixSession)
    {
        var p = fixSession ? Fix() : Deliver();
        var line = p.Split('\n').FirstOrDefault(l => l.Contains("curly braces", StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.Contains("handoff", line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>sk-platform note 3: two NoProgress verdicts and $3.82 of fix session for a stage whose whole
    /// output was a sibling-repo PR. SC4.3 made declared satellites count; the anchor commit is what carries
    /// the handoff and evidence, which still do not travel.</summary>
    [Fact]
    public void MultiRepoPlansGetTheAnchorCommitRuleWithTheirSatellitesNamed()
    {
        var plan = Plan();
        plan.SatelliteRepos = ["../sk-studio", "../elfine-site"];

        var p = Deliver(plan);

        Assert.Contains("land at least one commit HERE every session", p, StringComparison.Ordinal);
        Assert.Contains("sk-studio", p, StringComparison.Ordinal);
        Assert.Contains("elfine-site", p, StringComparison.Ordinal);
        Assert.Contains("land at least one commit HERE", Fix(plan), StringComparison.Ordinal);
    }

    /// <summary>The other half of the same rule: a single-repo plan must not pay prompt bytes for it.</summary>
    [Fact]
    public void SingleRepoPlansDoNotCarryTheAnchorCommitRule()
    {
        var p = Deliver();
        Assert.DoesNotContain("land at least one commit HERE", p, StringComparison.Ordinal);
        Assert.DoesNotContain("satellite", p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Bug #15 is still open: a composed prompt past ~8191 chars is handed to a cmd.exe agent as a
    /// command-line ARGUMENT, the agent silently never runs, and the run still reports success. Lessons are
    /// prose and prose costs bytes, so the budget is pinned here — including the multi-repo case, which the
    /// existing 6000-char guard never renders. Pay for new prose by cutting old prose, never by raising this.</summary>
    [Fact]
    public void TheLessonsFitTheCommandLineBudgetEvenOnAMultiRepoPlan()
    {
        var plan = Plan();
        plan.SatelliteRepos = ["../sk-studio", "../elfine-site"];

        var tools = ToolContract.Render(plan);
        Assert.True(tools.Length < 6_000, $"tool contract with satellites is {tools.Length} chars — see bug #15 before growing it");

        foreach (var (what, prompt) in new[] { ("deliver", Deliver(plan)), ("fix", Fix(plan)) })
            Assert.True(prompt.Length < 8_000, $"built-in {what} prompt is {prompt.Length} chars — bug #15 drops the agent past ~8191");
    }

    /// <summary>The lessons are prose, and prose is where braces get in. A rendered prompt with a stray
    /// brace is the exact failure SC3.3 parks on, so the templates themselves must stay clean.</summary>
    [Fact]
    public void TheRenderedPromptsCarryNoLiteralBraces()
    {
        var plan = Plan();
        plan.SatelliteRepos = ["../sk-studio"];

        Assert.DoesNotContain('{', Deliver(plan));
        Assert.DoesNotContain('{', Fix(plan));
    }
}
