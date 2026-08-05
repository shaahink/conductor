namespace Conductor.Tests;

/// <summary>
/// SF7.1 — the third drift item named by devcontext field note #17, and the worst of the three,
/// because agents READ this file to learn how conductor works.
/// <para><c>.claude/skills/run-conductor/SKILL.md</c> stated the OPPOSITE of the engine's trust
/// model: that a hand-edited tracker <c>DONE</c> row is "discarded" and does not advance the
/// checkpoint. Measured at <c>VerdictEngine.cs</c>, a tracker-only flip is ACCEPTED via the W1.3
/// transition fallback — appended to <c>NewlyDone</c> — with a warning and a <c>legacy-claim</c>
/// ledger row. The page also quoted a console line the engine no longer emits.</para>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    [Fact]
    public void TheRunConductorSkillDescribesTheClaimSignalTheVerdictEngineActuallyUses()
    {
        // Every partial of the verdict engine, not just VerdictEngine.cs: K1.1 lifted the claim rule
        // into VerdictEngine.Claims.cs so the rollover path could share it, and reading one file
        // would report the fallback as removed when it had only moved next door.
        var engine = string.Concat(Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), "src", "Conductor.Core", "Orchestration"), "VerdictEngine*.cs")
            .Select(File.ReadAllText));
        var skill = Doc(".claude", "skills", "run-conductor", "SKILL.md");

        // The fallback is what makes "discarded" false. If it is ever removed, this test tells the
        // next author that the skill may (and must) go back to describing a hard reject.
        var fallbackLives = engine.Contains("accepted via the transition fallback", StringComparison.Ordinal)
            && engine.Contains("[.. graphClaims, .. legacy]", StringComparison.Ordinal);
        Assert.True(fallbackLives,
            "the W1.3 transition fallback is gone from VerdictEngine — a tracker-only DONE flip may no " +
            "longer be accepted. Re-check .claude/skills/run-conductor/SKILL.md, which now says it IS " +
            "accepted-with-a-warning, and this test.");

        Assert.Contains("transition fallback", skill, StringComparison.Ordinal);
        Assert.Contains("legacy-claim", skill, StringComparison.Ordinal);

        // The two specific sentences that were wrong must not come back.
        Assert.DoesNotContain("hand-edited tracker rows are discarded", skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("marked DONE via direct tracker edit", skill, StringComparison.OrdinalIgnoreCase);
    }
}
