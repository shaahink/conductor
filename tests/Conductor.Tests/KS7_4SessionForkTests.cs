using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Models;
using Xunit;

namespace Conductor.Tests;

/// <summary>
/// KS7.4 — forking instead of cold-starting a fix or audit session.
/// </summary>
/// <remarks>
/// The measurement this is built on, taken against claude 2.1.235 rather than assumed (evidence file
/// §2): <c>--fork-session</c> composes with <c>--session-id</c> and the CLI honours the id we ask for,
/// the carried conversation arrives as a cache READ (30,098 read / 0 write on a 30k base), and the
/// fork measured 0.15% larger and $0.0001 cheaper than resuming the same conversation. So the design
/// question was never "can we afford to fork" — it was "can we fork without surrendering the session
/// id", and the answer is yes.
/// </remarks>
public class KS7_4SessionForkTests
{
    private static AgentConfig Forking() => new()
    {
        Args = ["-p", "{prompt}", "--session-id", "{sessionId}"],
        ForkArgs = ["-p", "{prompt}", "--resume", "{claudeSessionId}", "--fork-session", "--session-id", "{sessionId}"],
        ForkKinds = ["fix", "audit"],
    };

    private static SessionRecord Done(int n, string stage, string claudeId, SessionKind kind = SessionKind.Deliver) =>
        new()
        {
            Number = n, Stage = stage, Kind = kind, ClaudeSessionId = claudeId,
            StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow,
        };

    // ─────────────────────────── the policy ───────────────────────────

    [Fact]
    public void AFixSessionForksTheStagesMostRecentFinishedSession()
    {
        List<SessionRecord> history = [Done(1, "KS7", "aaa"), Done(2, "KS7", "bbb")];

        Assert.Equal("bbb", SessionFork.BaseFor(history, "KS7", SessionKind.Fix, Forking()));
        Assert.Equal("bbb", SessionFork.BaseFor(history, "KS7", SessionKind.Audit, Forking()));
    }

    [Fact]
    public void ADeliverSessionStartsColdBecauseItIsNotNamedInForkKinds() =>
        Assert.Null(SessionFork.BaseFor([Done(1, "KS7", "aaa")], "KS7", SessionKind.Deliver, Forking()));

    [Fact]
    public void AnotherStagesSessionIsNeverTheBase()
    {
        // The fix is about THIS stage's work. Forking KS6's conversation would carry the wrong context
        // in at full length and read, to anyone watching the tokens, exactly like it was working.
        List<SessionRecord> history = [Done(1, "KS6", "aaa")];
        Assert.Null(SessionFork.BaseFor(history, "KS7", SessionKind.Fix, Forking()));
    }

    [Fact]
    public void TheFirstSessionOfAStageStartsCold() =>
        Assert.Null(SessionFork.BaseFor([], "KS7", SessionKind.Fix, Forking()));

    [Fact]
    public void ASessionStillRunningIsNotForked()
    {
        // Its transcript is mid-write. The most recent FINISHED session of the stage is the base.
        var live = Done(2, "KS7", "bbb");
        live.EndedUtc = null;
        List<SessionRecord> history = [Done(1, "KS7", "aaa"), live];

        Assert.Equal("aaa", SessionFork.BaseFor(history, "KS7", SessionKind.Fix, Forking()));
    }

    [Fact]
    public void APlanWithNoForkTemplateNeverForksHoweverItsKindsAreNamed()
    {
        // Opt-in twice over: only the plan knows whether its agent CLI can fork at all, so a kinds
        // list without a template must not silently produce a resume.
        var cfg = new AgentConfig { Args = ["-p", "{prompt}"], ForkKinds = ["fix", "audit"] };
        Assert.Null(SessionFork.BaseFor([Done(1, "KS7", "aaa")], "KS7", SessionKind.Fix, cfg));
    }

    [Fact]
    public void AnExistingPlanIsUnchangedByUpgrading()
    {
        var cfg = new AgentConfig { Args = ["-p", "{prompt}", "--session-id", "{sessionId}"] };
        foreach (var kind in Enum.GetValues<SessionKind>())
            Assert.Null(SessionFork.BaseFor([Done(1, "KS7", "aaa")], "KS7", kind, cfg));
    }

    // ─────────────────────────── the args ───────────────────────────

    [Fact]
    public void ForkArgsCarryTheBaseIdAndTheNewIdInDifferentPlaces()
    {
        // The whole reason a fork is usable: {claudeSessionId} is what we resume FROM, {sessionId} is
        // the id conductor keeps for its own record, and the CLI honours both.
        var args = AgentSession.ResolveArgs(
            Forking().ForkArgs!, "do the fix", sessionId: "new-id", resumeClaudeId: "base-id", model: null);

        Assert.Equal(
            ["-p", "do the fix", "--resume", "base-id", "--fork-session", "--session-id", "new-id"],
            args);
    }

    [Fact]
    public void ForksIsCaseInsensitiveBecauseAPlanIsHandWritten()
    {
        Assert.True(SessionFork.Forks(["Fix"], SessionKind.Fix));
        Assert.True(SessionFork.Forks(["AUDIT"], SessionKind.Audit));
        Assert.False(SessionFork.Forks(["fix"], SessionKind.Audit));
        Assert.False(SessionFork.Forks([], SessionKind.Fix));
        Assert.False(SessionFork.Forks(null, SessionKind.Fix));
    }

    [Fact]
    public void MergeCarriesForkSettingsFromAStageOverride()
    {
        var merged = new AgentConfig { Args = ["-p", "{prompt}"] }.Merge(Forking());

        Assert.Equal(Forking().ForkArgs, merged.ForkArgs);
        Assert.Equal(["fix", "audit"], merged.ForkKinds);
    }
}
