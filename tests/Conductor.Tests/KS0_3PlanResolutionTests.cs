using Conductor.Core.Planning;

namespace Conductor.Tests;

/// <summary>
/// KS0.3, bug #20 — the directory you are standing in beats an inherited <c>CONDUCTOR_PLAN</c>.
///
/// <para>Every live-proof rig in three eras worked around this by hand, and one of them did not: the
/// phantom <c>F0</c>–<c>R0</c> stages sitting in <c>plans/karvan/CORE-TRACKER.md</c> today were written
/// by a scratch rig that resolved the DRIVING run's plan out of its inherited environment. The rig had
/// its own repo, its own plan file and its own state dir; none of that was consulted.</para>
///
/// <para>The tests that matter most here are the two that keep the fix from becoming the next bug:
/// an ambiguous directory must NOT override (this repo has eleven plans under <c>plans/</c>, and every
/// in-session <c>conductor task</c> depends on the variable still winning there), and an override must
/// never be silent.</para>
/// </summary>
public sealed class KS0_3PlanResolutionTests
{
    private const string Env = @"C:\code\conductor\plans\karvansara\core.plan.json";
    private const string Rig = @"C:\temp\rig\rig.plan.json";

    private static IReadOnlyList<PlanDiscovery.Candidate> Found(params string[] paths)
        => paths.Select(p => new PlanDiscovery.Candidate(Path.GetFileName(p), p)).ToList();

    [Fact]
    public void TheCwdPlanBeatsTheEnvironmentVariable_ThisIsBug20()
    {
        var choice = PlanResolution.Decide(explicitPlan: null, envPlan: Env, Found(Rig));

        Assert.Equal(Rig, choice.Path);
    }

    [Fact]
    public void AnOverrideIsNeverSilent_AndNamesBothFiles()
    {
        var choice = PlanResolution.Decide(explicitPlan: null, envPlan: Env, Found(Rig));

        Assert.NotNull(choice.Warning);
        Assert.Contains(Env, choice.Warning, StringComparison.Ordinal);
        Assert.Contains(Rig, choice.Warning, StringComparison.Ordinal);
        Assert.Contains("-p", choice.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAmbiguousDirectoryDoesNotOverride_SoAnInSessionClaimStillReachesItsOwnRun()
    {
        // The shape of this repo: nothing at the root, several plans under plans/. If ambiguity could
        // override, every `conductor task` an agent runs would resolve to whichever plan sorted first.
        var many = Found(@"C:\code\conductor\plans\conductor-ux.plan.json",
                         @"C:\code\conductor\plans\conductor.self.plan.json",
                         @"C:\code\conductor\plans\shamshir-p0.plan.json");

        var choice = PlanResolution.Decide(explicitPlan: null, envPlan: Env, many);

        Assert.Equal(Env, choice.Path);
        Assert.Null(choice.Warning);
    }

    [Fact]
    public void AnEmptyDirectoryFallsBackToTheEnvironmentVariable()
    {
        var choice = PlanResolution.Decide(explicitPlan: null, envPlan: Env, Found());

        Assert.Equal(Env, choice.Path);
        Assert.Null(choice.Warning);
    }

    [Fact]
    public void ExplicitDashPOutranksEverything()
    {
        var choice = PlanResolution.Decide(explicitPlan: @"C:\chosen\by\hand.plan.json",
                                           envPlan: Env, Found(Rig));

        Assert.Equal(@"C:\chosen\by\hand.plan.json", choice.Path);
        Assert.Null(choice.Warning);
    }

    [Fact]
    public void TheSameFileSpelledTwoWaysIsNotAnOverride()
    {
        var choice = PlanResolution.Decide(
            explicitPlan: null,
            envPlan: @"C:\code\conductor\plans\..\plans\karvansara\core.plan.json",
            Found(Env));

        Assert.Null(choice.Warning);
        Assert.NotNull(choice.Path);
    }

    [Fact]
    public void OneCandidateAndNoVariableIsAnnounced_NotWarnedAbout()
    {
        var choice = PlanResolution.Decide(explicitPlan: null, envPlan: null, Found(Rig));

        Assert.Equal(Rig, choice.Path);
        Assert.Null(choice.Warning);
        Assert.Contains(Rig, choice.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingToGoOnLeavesTheDecisionToTheShell()
    {
        var choice = PlanResolution.Decide(explicitPlan: null, envPlan: null, Found());

        Assert.Null(choice.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyVariableIsNotAPlan(string blank)
    {
        var choice = PlanResolution.Decide(explicitPlan: null, envPlan: blank, Found());

        Assert.Null(choice.Path);
    }
}
