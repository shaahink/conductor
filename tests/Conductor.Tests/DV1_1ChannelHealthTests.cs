using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Github;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// DV1.1 — channel health is loud.
///
/// <para><b>The seeded regression is a real, dated, measured failure.</b>
/// <c>plans/karvansara/edge.plan.json</c> set <c>"github": { "enabled": true, "liveMirror": true,
/// "runHistoryIssue": true }</c>. No <c>CONDUCTOR_GITHUB_TOKEN</c> was present in the engine's
/// environment. <c>GithubMirror.TryCreate</c> wrote two lines to <c>.conductor/conductor.log</c> —
/// at 19:09:25 and again at 19:17:03 — and returned null. The run then executed twenty-four
/// checkpoints across twenty-three sessions for $324 and posted ZERO issues; every one of the 33
/// issues on <c>shaahink/conductor</c> belongs to the earlier core run
/// (docs/dev/OBSERVABILITY-AND-MARKET-2026-08-22.md §2.2 cause 1).</para>
///
/// <para><b>What these tests pin</b> is that the same plan, with the same absent token, now says so
/// in the three places an operator actually looks — the REPORT.md header, <c>/status</c>, and the
/// owner queue — and is refused at preflight instead of warned about. The negative half is pinned
/// just as hard: a healthy channel and an absent channel are both SILENT in all three, or the
/// surface becomes noise and gets skipped, which is the failure this checkpoint exists to end.</para>
/// </summary>
public sealed class DV1_1ChannelHealthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-dv11-{Guid.NewGuid():N}");
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    public DV1_1ChannelHealthTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, ".conductor"));
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { /* best effort */ }
    }

    /// <summary>The edge run's block, verbatim. <c>repo</c> is set explicitly for the same reason the
    /// real plan set it — so the destination never depends on a git remote — which also keeps this
    /// test from shelling out.</summary>
    private PlanConfig EdgePlan(bool withGithub = true) => new()
    {
        Name = "divan-dv1-1",
        Repo = _dir.Replace("\\", "/", StringComparison.Ordinal),
        Tracker = "TRACKER.md",
        Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
        Stages = [new StageConfig { Id = "DV1", Title = "The channel that says so", Sessions = 1 }],
        Github = withGithub
            ? new GithubConfig { Enabled = true, Repo = "shaahink/conductor", LiveMirror = true, RunHistoryIssue = true }
            : null,
    };

    private static RunState State() => new()
    {
        PlanName = "divan-dv1-1",
        RunId = "dv11",
        Status = RunStatus.Running,
        CurrentStage = "DV1",
        SessionCounter = 23,
    };

    private static TrackerSnapshot Track() => new() { HandoffBlock = "", Checkpoints = [] };

    // ---- the probe itself ------------------------------------------------------------------------

    /// <summary>The probe asks the mirror's OWN questions. If this ever disagrees with
    /// <c>GithubMirror.TryCreate</c>, the health surface is lying in the most dangerous direction —
    /// green about a mirror that refuses to exist — so the refusal sentence is asserted to be the
    /// mirror's, not a second copy of it.</summary>
    [Fact]
    public void TheEdgeBlockWithNoToken_ProbesDead_InTheMirrorsOwnWords()
    {
        var plan = EdgePlan();
        var github = ChannelHealthProbe.Collect(plan).Single(c => c.Channel == ChannelHealthProbe.GithubChannel);

        Assert.Equal(ChannelState.Dead, github.State);
        Assert.True(github.IsLoud);
        Assert.Contains(GithubIdentity.MissingTokenRefusal(plan)[0], github.Detail, StringComparison.Ordinal);
        Assert.Contains(GithubIdentity.MissingTokenRefusal(plan)[1], github.Detail, StringComparison.Ordinal);
        Assert.Equal("setx CONDUCTOR_GITHUB_TOKEN <token>", github.FixCommand);

        // And it is derived, not stored: the mirror is never constructed here, and TryCreate itself
        // still returns null for the same plan. The two answers come from one set of facts.
        Assert.Null(GithubMirror.TryCreate(plan, null, "dv11", _ => { }));
    }

    /// <summary>The clearing half, the way every other owner-queue source is asserted: change the one
    /// piece of state that resolves it and watch the entry go. Nothing is written and nothing has to
    /// be garbage-collected, which is why a token that appears mid-run heals the surface by itself.
    /// </summary>
    [Fact]
    public void TheTokenAppearing_ClearsTheChannel_WithNothingToCollect()
    {
        var plan = EdgePlan();
        Assert.Equal(ChannelState.Dead, Github(plan).State);

        try
        {
            Environment.SetEnvironmentVariable(GithubIdentity.TokenEnvVar, "ghp-not-a-real-token");
            var healed = Github(plan);
            Assert.Equal(ChannelState.Ready, healed.State);
            Assert.False(healed.IsLoud);
            Assert.Contains("shaahink/conductor", healed.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(OwnerQueue.Collect(plan, State(), Track(), Now), i => i.Kind == "channel");
        }
        finally
        {
            Environment.SetEnvironmentVariable(GithubIdentity.TokenEnvVar, null);
        }
    }

    // ---- surface 1: the REPORT.md header ----------------------------------------------------------

    [Fact]
    public void TheEdgeFailure_ReachesTheReportHeader()
    {
        var report = Reporter.Build(EdgePlan(), State(), Track(), lastGates: null);
        var header = Header(report);

        Assert.Contains("**Channels:**", header, StringComparison.Ordinal);
        Assert.Contains("github DEAD", header, StringComparison.Ordinal);
        Assert.Contains("enabled in the plan but no token", header, StringComparison.Ordinal);
        Assert.Contains("setx CONDUCTOR_GITHUB_TOKEN <token>", header, StringComparison.Ordinal);
    }

    /// <summary>A working mirror gets ONE quiet token in the roll-up and no warning line. The roll-up
    /// itself is unconditional: "the report does not mention github" and "github is fine" must not
    /// read the same, which is the ambiguity that let the edge run's report look complete.</summary>
    [Fact]
    public void AHealthyMirror_IsNamedButNotShouted_AndAnAbsentOneIsNeitherMissingNorLoud()
    {
        try
        {
            Environment.SetEnvironmentVariable(GithubIdentity.TokenEnvVar, "ghp-not-a-real-token");
            var header = Header(Reporter.Build(EdgePlan(), State(), Track(), lastGates: null));
            Assert.Contains("github ready", header, StringComparison.Ordinal);
            Assert.DoesNotContain("⚠ Channel", header, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GithubIdentity.TokenEnvVar, null);
        }

        var noBlock = Header(Reporter.Build(EdgePlan(withGithub: false), State(), Track(), lastGates: null));
        Assert.Contains("github off", noBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("⚠ Channel", noBlock, StringComparison.Ordinal);
    }

    // ---- surface 2: /status -----------------------------------------------------------------------

    [Fact]
    public void TheEdgeFailure_ReachesSlashStatus()
    {
        var plan = EdgePlan();
        var composer = new MessageComposer(plan, State(), ProgressProviderFactory.Create(plan), null, _ => { });
        var status = composer.StatusText();

        Assert.Contains("Channels:", status, StringComparison.Ordinal);
        Assert.Contains("<b>github DEAD</b>", status, StringComparison.Ordinal);
        Assert.Contains("enabled in the plan but no token", status, StringComparison.Ordinal);
        Assert.Contains("<code>setx CONDUCTOR_GITHUB_TOKEN &lt;token&gt;</code>", status, StringComparison.Ordinal);
    }

    /// <summary>Telegram renders as HTML, and the fix command carries angle brackets. Unescaped they
    /// are a tag Telegram rejects — the whole push fails, and the surface that exists to report a
    /// dead channel becomes a second dead channel.</summary>
    [Fact]
    public void TheFixCommandIsHtmlEscaped_SoTheReportOfADeadChannelDoesNotKillThePush()
    {
        var plan = EdgePlan();
        var composer = new MessageComposer(plan, State(), ProgressProviderFactory.Create(plan), null, _ => { });
        Assert.DoesNotContain("<token>", composer.StatusText(), StringComparison.Ordinal);
    }

    // ---- surface 3: the owner queue ---------------------------------------------------------------

    [Fact]
    public void TheEdgeFailure_ReachesTheOwnerQueue_WithTheCommandThatClearsIt()
    {
        var plan = EdgePlan();
        var items = OwnerQueue.Collect(plan, State(), Track(), Now);
        var channel = Assert.Single(items, i => i.Kind == "channel");

        Assert.Equal("channel-github", channel.Id);
        Assert.Contains("github is DEAD", channel.Title, StringComparison.Ordinal);
        Assert.Equal("setx CONDUCTOR_GITHUB_TOKEN <token>", channel.Command);
        // The honest unblocks line: a dead mirror stops no stage. What it costs is the record.
        Assert.Contains("record", channel.Unblocks, StringComparison.Ordinal);

        var rendered = OwnerQueue.Render(plan, State(), items, Now);
        Assert.Contains("github is DEAD", rendered, StringComparison.Ordinal);
        Assert.Contains("`setx CONDUCTOR_GITHUB_TOKEN <token>`", rendered, StringComparison.Ordinal);
        // The "nothing is waiting on you" branch is the one the edge run would have rendered.
        Assert.DoesNotContain("Nothing is waiting on you", rendered, StringComparison.Ordinal);
    }

    /// <summary>It ranks below the three parks and above everything the run is merely owed — the run
    /// standing still still wins, but a channel silently losing the record beats a skipped stage
    /// nobody is waiting on.</summary>
    [Fact]
    public void ADeadChannel_RanksUnderAParkAndOverASkippedStage()
    {
        var plan = EdgePlan();
        var state = State();
        state.Status = RunStatus.Paused;
        state.AttentionReason = "paused by the operator";
        state.SkippedStages.Add("DV1");

        var kinds = OwnerQueue.Collect(plan, state, Track(), Now).Select(i => i.Kind).ToList();
        Assert.Equal(["park", "channel", "skippedStage"], kinds);
    }

    // ---- preflight: refused, not warned -----------------------------------------------------------

    /// <summary>The preflight half of the checkpoint. Before DV1.1 this was a <c>warn</c>, which
    /// doctor prints in yellow and its exit code ignores — so the edge run passed its own preflight
    /// with the mirror already dead.</summary>
    [Fact]
    public void TheEdgeFailure_IsRefusedAtPreflight_NotWarnedAbout()
    {
        var checks = DoctorCommand.CheckChannels(EdgePlan());
        var github = Assert.Single(checks, c => c.Name == ChannelHealthProbe.GithubChannel);

        Assert.Equal("fail", github.State);
        Assert.Contains("no GitHub token", github.Message, StringComparison.Ordinal);

        // Every configured channel gets a row, not just the broken one.
        Assert.Contains(checks, c => c.Name == ChannelHealthProbe.TelegramChannel);
    }

    /// <summary>A channel nobody asked for is not a fault. Without this the tightening above would
    /// turn every plan with no github block into a failing preflight, and the checkpoint would have
    /// traded a missed signal for an ignored one.</summary>
    [Fact]
    public void AChannelThePlanNeverAskedFor_IsNotAPreflightFailure()
    {
        var checks = DoctorCommand.CheckChannels(EdgePlan(withGithub: false));
        Assert.DoesNotContain(checks, c => c.State == "fail");
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static ChannelHealth Github(PlanConfig plan)
        => ChannelHealthProbe.Collect(plan).Single(c => c.Channel == ChannelHealthProbe.GithubChannel);

    /// <summary>The header block — everything above the first <c>##</c> section. Asserting against it
    /// rather than the whole document is the point: the edge run's report had plenty of text, and
    /// none of it was in the block an operator reads first.</summary>
    private static string Header(string report)
    {
        var at = report.IndexOf("\n## ", StringComparison.Ordinal);
        return at < 0 ? report : report[..at];
    }
}
