using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Integrations.Cloud;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>DV5.1 / findings §2.3 CL-2 and §6.8 — the <c>/cloud</c> admin verb.
///
/// <para>Two things are pinned here that are easy to lose. The first is §6.8: a cloud session clones
/// from the REMOTE, so every refusal below asserts both that the owner was told the exact git state
/// and that the CLI was never reached — a preflight that refuses and spawns anyway is worse than no
/// preflight, because the owner now has a wrong answer they were warned about.</para>
///
/// <para>The second is §2.4 item 1: there is no meter for a cloud session, so its cost is the word
/// <c>unknown</c>. The tests below assert the word is present and that no zero is anywhere near it.</para>
///
/// <para>The create direction is not tested for success because it has none: measured against claude
/// <see cref="CloudCliFacts.MeasuredVersion"/> on <see cref="CloudCliFacts.MeasuredOn"/>, starting a
/// cloud session is interactive-only. See <c>.conductor/evidence/DV5/dv5.1-cloud-flags.md</c>.</para></summary>
public sealed class DV5_1CloudVerbTests : IDisposable
{
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string Other = "2222222222222222222222222222222222222222";
    private const string SessionId = "session_01k3q7wz9c";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dv5-cloud-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeChannel _channel = new();

    public DV5_1CloudVerbTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ────────────────────────────── §6.8, the preflight ──────────────────────────────

    [Fact]
    public void A_dirty_tree_is_refused_and_the_files_are_named()
    {
        var v = CloudPreflight.Judge(Snap(dirty: true, dirtyCount: 2, summary: "M src/Foo.cs\nM src/Bar.cs"), Sha);

        Assert.Equal(CloudPreflightVerdict.DirtyTree, v.Verdict);
        Assert.Contains("2 uncommitted changes", v.Detail, StringComparison.Ordinal);
        Assert.Contains("M src/Foo.cs", v.Detail, StringComparison.Ordinal);
        Assert.Contains("M src/Bar.cs", v.Detail, StringComparison.Ordinal);
    }

    /// <summary>The distinction <see cref="GitSnapshot"/>'s own doc comment forbids conflating: a
    /// branch with no upstream is not a branch that is level with one.</summary>
    [Fact]
    public void A_branch_that_was_never_pushed_is_refused_as_never_pushed_not_as_in_sync()
    {
        var v = CloudPreflight.Judge(Snap(upstream: null, ahead: null, behind: null), Sha);

        Assert.Equal(CloudPreflightVerdict.NoUpstream, v.Verdict);
        Assert.Contains("has no upstream", v.Detail, StringComparison.Ordinal);
        Assert.Contains("never been pushed", v.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_remote_that_does_not_have_the_branch_is_refused_by_name()
    {
        var v = CloudPreflight.Judge(Snap(), remoteTipSha: null);

        Assert.Equal(CloudPreflightVerdict.RemoteMissingBranch, v.Verdict);
        Assert.Contains("origin/feat/x does not answer for feat/x", v.Detail, StringComparison.Ordinal);
    }

    /// <summary>The case the tracking counters cannot see. <c>git status</c> compares against the last
    /// fetch, so a branch can read "up to date" while the remote — the thing a cloud session actually
    /// clones — points somewhere else. Both shas are quoted, because "they differ" from a phone is an
    /// instruction to go and look.</summary>
    [Fact]
    public void A_remote_tip_that_is_not_head_is_refused_and_both_shas_are_quoted()
    {
        var v = CloudPreflight.Judge(Snap(ahead: 0, behind: 0), Other);

        Assert.Equal(CloudPreflightVerdict.RemoteDiffersFromHead, v.Verdict);
        Assert.Contains(Sha[..8], v.Detail, StringComparison.Ordinal);
        Assert.Contains(Other[..8], v.Detail, StringComparison.Ordinal);
        Assert.Contains("would clone the remote's commit", v.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_detached_head_is_refused_because_a_cloud_session_clones_a_branch()
    {
        var v = CloudPreflight.Judge(Snap(branch: "", detached: true), Sha);

        Assert.Equal(CloudPreflightVerdict.DetachedHead, v.Verdict);
        Assert.Contains("HEAD is detached", v.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_with_no_commit_is_refused_rather_than_read_as_clean()
    {
        var v = CloudPreflight.Judge(GitSnapshot.None, null);

        Assert.Equal(CloudPreflightVerdict.NothingToClone, v.Verdict);
        Assert.False(v.Ok);
    }

    [Fact]
    public void Only_a_clean_tree_whose_remote_has_the_same_commit_passes()
    {
        var v = CloudPreflight.Judge(Snap(), Sha);

        Assert.True(v.Ok);
        Assert.Contains("origin/feat/x has the same commit", v.Detail, StringComparison.Ordinal);
    }

    // ────────────────────────────── the create direction ──────────────────────────────

    [Fact]
    public async Task A_create_on_a_dirty_tree_quotes_the_git_state_and_never_reaches_the_cli()
    {
        var spy = new SpyCli();
        var verb = new CloudVerb(spy, _ => CloudPreflight.Judge(Snap(dirty: true, dirtyCount: 1, summary: "M src/Foo.cs"), Sha));

        var r = await verb.RunAsync(_dir, "rig", "sweep the docs", CancellationToken.None);

        Assert.Equal("refusedGit", r.Action);
        Assert.False(r.Spawned);
        Assert.Empty(spy.Calls);
        Assert.Contains("M src/Foo.cs", r.Reply, StringComparison.Ordinal);
        Assert.Contains("clones from the remote", r.Reply, StringComparison.Ordinal);

        // The command is withheld on purpose: running it now walks into the same trap.
        Assert.DoesNotContain("claude --cloud", r.Reply, StringComparison.Ordinal);
    }

    /// <summary>The measured refusal, handed to the owner in the platform's own words. A paraphrase
    /// here is how an owner ends up debugging conductor instead of the CLI.</summary>
    [Fact]
    public async Task A_create_on_a_clean_tree_quotes_the_cli_refusal_verbatim_and_the_exact_command()
    {
        var spy = new SpyCli();
        var verb = new CloudVerb(spy, _ => CloudPreflight.Judge(Snap(), Sha));

        var r = await verb.RunAsync(_dir, "rig", "sweep the docs", CancellationToken.None);

        Assert.Equal("refusedCreate", r.Action);
        Assert.False(r.Spawned);
        Assert.Empty(spy.Calls);
        Assert.Contains(CloudCliFacts.RefusalWithoutTty, r.Reply, StringComparison.Ordinal);
        Assert.Contains("claude --cloud \"sweep the docs\"", r.Reply, StringComparison.Ordinal);
    }

    /// <summary>There is no create seam to be tempted by, and this is what stops one appearing by
    /// accident when the platform changes and somebody remembers the findings doc rather than
    /// re-measuring. Stated as the invariant rather than as a method count, so the seam can grow the
    /// calls that ARE headless — DV5.2 added the review one — without the rule going quiet.</summary>
    [Fact]
    public void No_call_on_the_cloud_seam_starts_a_cloud_session()
    {
        var starters = typeof(ICloudCli).GetMethods()
            .Select(m => m.Name)
            .Where(n => n.Contains("Create", StringComparison.Ordinal)
                     || n.Contains("Start", StringComparison.Ordinal)
                     || n.Contains("New", StringComparison.Ordinal)
                     || n.Contains("Spawn", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(starters);
        Assert.NotEmpty(typeof(ICloudCli).GetMethods());
    }

    // ────────────────────────────── the follow-up direction ──────────────────────────────

    /// <summary>The argument order the CLI's own refusal message spells out. Pinned because it is the
    /// one invocation shape that works, and it was measured, not guessed.</summary>
    [Fact]
    public void The_follow_up_argv_is_p_message_then_cloud_id()
        => Assert.Equal(["-p", "how is it going", "--cloud", SessionId],
                        CloudCliFacts.FollowUpArgs(SessionId, "how is it going"));

    [Fact]
    public async Task A_follow_up_reaches_the_cli_with_the_session_and_the_message()
    {
        var spy = new SpyCli { Answer = new CloudCliResult(0, "I rewrote the README.", "", false) };
        var verb = new CloudVerb(spy, _ => CloudPreflight.Judge(Snap(), Sha));

        var r = await verb.RunAsync(_dir, "rig", $"{SessionId} how is it going", CancellationToken.None);

        Assert.Equal("followUp", r.Action);
        Assert.True(r.Spawned);
        Assert.Equal((_dir, SessionId, "how is it going"), Assert.Single(spy.Calls));
        Assert.Contains("I rewrote the README.", r.Reply, StringComparison.Ordinal);
    }

    /// <summary>§2.4 item 1, the honesty rule. A run that quietly prices cloud work at $0 because it
    /// could not see the meter is the class of lie KS4 was built to catch.</summary>
    [Fact]
    public async Task Cloud_spend_is_reported_as_unknown_and_never_as_a_zero()
    {
        var verb = new CloudVerb(new SpyCli(), _ => CloudPreflight.Judge(Snap(), Sha));

        foreach (var argument in new[] { "", $"{SessionId} hello", "start something" })
        {
            var r = await verb.RunAsync(_dir, "rig", argument, CancellationToken.None);

            Assert.Equal(CloudCliFacts.UnknownCost, r.Cost);
            foreach (var zero in new[] { "$0", "0.00", "cost: 0" })
                Assert.DoesNotContain(zero, r.Reply, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A bare id becomes a tappable link only when the CLI itself printed one. The engine
    /// never invents the URL shape — it has never observed one, and a link that 404s is worse from a
    /// phone than no link at all.</summary>
    [Fact]
    public async Task A_link_in_the_output_upgrades_a_bare_id_and_nothing_else_invents_one()
    {
        var withLink = new CloudVerb(
            new SpyCli { Answer = new CloudCliResult(0, $"done — https://claude.ai/code/{SessionId}", "", false) },
            _ => CloudPreflight.Judge(Snap(), Sha));
        var withoutLink = new CloudVerb(new SpyCli { Answer = new CloudCliResult(0, "done", "", false) },
            _ => CloudPreflight.Judge(Snap(), Sha));

        var linked = await withLink.RunAsync(_dir, "rig", $"{SessionId} hi", CancellationToken.None);
        var bare = await withoutLink.RunAsync(_dir, "rig", $"{SessionId} hi", CancellationToken.None);

        Assert.Equal($"https://claude.ai/code/{SessionId}", linked.Url);
        Assert.Null(bare.Url);
        Assert.Contains(CloudCliFacts.SessionHome, bare.Reply, StringComparison.Ordinal);
    }

    /// <summary>§2.4 item 2: there is no stall watchdog out there. A cloud session that is still
    /// thinking is not one that failed, and the owner is told which happened.</summary>
    [Fact]
    public async Task A_follow_up_that_runs_out_of_time_says_the_session_is_still_running()
    {
        var verb = new CloudVerb(new SpyCli { Answer = new CloudCliResult(0, "", "", TimedOut: true) },
            _ => CloudPreflight.Judge(Snap(), Sha), TimeSpan.FromMinutes(5));

        var r = await verb.RunAsync(_dir, "rig", $"{SessionId} hi", CancellationToken.None);

        Assert.Contains("did not answer within 5 minutes", r.Reply, StringComparison.Ordinal);
        Assert.Contains("still running", r.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_follow_up_reports_the_exit_code_and_what_the_cli_said()
    {
        var verb = new CloudVerb(new SpyCli { Answer = new CloudCliResult(1, "", "no such session", false) },
            _ => CloudPreflight.Judge(Snap(), Sha));

        var r = await verb.RunAsync(_dir, "rig", $"{SessionId} hi", CancellationToken.None);

        Assert.Contains("exit 1", r.Reply, StringComparison.Ordinal);
        Assert.Contains("no such session", r.Reply, StringComparison.Ordinal);
    }

    /// <summary>The id shape is the CLI's, not this engine's guess at it. The live probe that
    /// corrected it is quoted in <see cref="CloudCliFacts.RefusalNotASession"/>: cloud session ids
    /// are <c>session_…</c> or <c>cse_…</c>, and a bare UUID — which an earlier draft of this file
    /// accepted — is a LOCAL session id that the cloud surface refuses.</summary>
    [Theory]
    [InlineData("session_01k3q7wz9c", true)]
    [InlineData("cse_9f21aa77", true)]
    [InlineData("https://claude.ai/code/session_01k3q7wz9c", true)]
    [InlineData("0f9c2a41-77b5-4e2d-9a3c-1d2e3f4a5b6c", false)]
    [InlineData("sess_abc123def", false)]
    [InlineData("refactor", false)]
    [InlineData("sweep", false)]
    [InlineData("", false)]
    public void A_session_reference_is_a_uuid_a_sess_id_or_a_claude_ai_code_url_and_nothing_else(
        string token, bool isSession)
        => Assert.Equal(isSession, CloudSessionRef.TryParse(token) is not null);

    [Fact]
    public async Task A_session_named_with_nothing_to_say_is_refused_rather_than_sent_an_empty_message()
    {
        var spy = new SpyCli();
        var verb = new CloudVerb(spy, _ => CloudPreflight.Judge(Snap(), Sha));

        var r = await verb.RunAsync(_dir, "rig", SessionId, CancellationToken.None);

        Assert.Empty(spy.Calls);
        Assert.Contains("says nothing to it", r.Reply, StringComparison.Ordinal);
    }

    // ────────────────────────────── on the surface ──────────────────────────────

    [Fact]
    public async Task An_observer_is_refused_the_verb_by_name_and_nothing_is_spawned()
    {
        var spy = new SpyCli();
        var surface = Surface(spy, out _);

        await surface.HandleMessageAsync("77", ChatProfile.Observer, $"/cloud {SessionId} hi",
            CancellationToken.None);
        await surface.CloudInFlight;

        Assert.Contains("/cloud", Assert.Single(_channel.Sent).Text, StringComparison.Ordinal);
        Assert.Contains("this chat is an observer", _channel.Sent[0].Text, StringComparison.Ordinal);
        Assert.Empty(spy.Calls);
    }

    /// <summary>§2.4 item 3: a cloud session cannot reach the control plane and cannot claim anything,
    /// so this row is the run's whole record that the owner sent work somewhere conductor cannot
    /// watch. Its cost field is the word, not a number.</summary>
    [Fact]
    public async Task An_admin_follow_up_answers_the_chat_and_lands_as_an_owner_action_in_the_event_log()
    {
        var spy = new SpyCli { Answer = new CloudCliResult(0, "the sweep is done", "", false) };
        var surface = Surface(spy, out var store);

        await surface.HandleMessageAsync("77", ChatProfile.Admin, $"/cloud {SessionId} how is it going",
            CancellationToken.None);
        await surface.CloudInFlight;
        store.FlushEvents();

        Assert.Contains("the sweep is done", Assert.Single(_channel.Sent).Text, StringComparison.Ordinal);
        Assert.Equal((_dir, SessionId, "how is it going"), Assert.Single(spy.Calls));

        var row = Assert.Single(store.ReadAllEvents("dv5-run").OfType<OwnerCloudAction>());
        Assert.Equal("followUp", row.Action);
        Assert.Equal(SessionId, row.CloudSessionId);
        Assert.Equal("unknown", row.Cost);
        store.Dispose();
    }

    [Fact]
    public async Task A_refusal_is_recorded_too_because_it_is_the_half_an_owner_asks_about_later()
    {
        var spy = new SpyCli();
        var surface = Surface(spy, out var store,
            _ => CloudPreflight.Judge(Snap(dirty: true, dirtyCount: 1, summary: "M src/Foo.cs"), Sha));

        await surface.HandleMessageAsync("77", ChatProfile.Admin, "/cloud sweep the docs", CancellationToken.None);
        await surface.CloudInFlight;
        store.FlushEvents();

        Assert.Empty(spy.Calls);
        Assert.Equal("refusedGit", Assert.Single(store.ReadAllEvents("dv5-run").OfType<OwnerCloudAction>()).Action);
        store.Dispose();
    }

    // ────────────────────────────── the rig ──────────────────────────────

    private static GitSnapshot Snap(string branch = "feat/x", bool detached = false,
        string? upstream = "origin/feat/x", int? ahead = 0, int? behind = 0, bool dirty = false,
        int dirtyCount = 0, string summary = "clean")
        => new(branch, detached, upstream, ahead, behind, Sha, "a commit", dirty, dirtyCount, summary, []);

    private RemoteSurface Surface(SpyCli cli, out SqliteRunStore store,
        Func<string, CloudPreflightResult>? preflight = null)
    {
        var plan = new PlanConfig
        {
            Name = "rig",
            Repo = _dir,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "Z1", Title = "The seam", Sessions = 1 } },
        };
        var state = new RunState { RunId = "dv5-run", SessionCounter = 1, CurrentStage = "Z1" };
        store = new SqliteRunStore(Path.Combine(_dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        store.SetRunId("dv5-run");
        store.InitializeRun("dv5-run", "rig", _dir, "feat/x", EngineStamp.Parse("test"));

        var composer = new MessageComposer(plan, state, ProgressProviderFactory.Create(plan), store, _ => { });
        return new RemoteSurface(_channel, composer, new CommandRouter(composer, plan), state, store,
            (_, _, _) => Task.CompletedTask, (_, _) => { },
            cloud: new CloudVerb(cli, preflight ?? (_ => CloudPreflight.Judge(Snap(), Sha))));
    }

    private sealed class SpyCli : ICloudCli
    {
        public List<(string Repo, string SessionId, string Message)> Calls { get; } = [];

        public CloudCliResult Answer { get; init; } = new(0, "ok", "", false);

        public Task<CloudCliResult> FollowUpAsync(string repoDir, string sessionId, string message,
            TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((repoDir, sessionId, message));
            return Task.FromResult(Answer);
        }

        /// <summary>DV5.1 never reaches the review verb; a call here is a bug, not a fixture gap.</summary>
        public Task<CloudCliResult> ReviewAsync(string repoDir, string? target, TimeSpan timeout,
            CancellationToken ct)
            => throw new InvalidOperationException("the /cloud verb must never start a review lane");
    }

    private sealed class FakeChannel : IMessageChannel
    {
        public string Name => "fake";
        public bool IsLive => true;
        public bool AllowsControl => true;
        public IReadOnlyList<ChatTarget> Targets { get; } = [new ChatTarget("77", ChatProfile.Admin)];
        public List<OutboundMessage> Queued { get; } = [];
        public List<OutboundMessage> Sent { get; } = [];

        public Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
        {
            Queued.Add(message);
            return Task.CompletedTask;
        }

        public Task SendAsync(OutboundMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
