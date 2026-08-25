using Conductor.Core.Inbox;
using Conductor.Core.Store;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV3.4 — which project is this note about?
///
/// <para>Findings §1.5 puts three mechanisms in order of how much typing they cost, and the whole
/// point is that the cheapest one is the good one: <b>reply to a push and the note lands in that
/// push's project with no command at all</b>. Every message conductor sends already opens with an
/// identity line naming the plan, so the answer is on the wire before the owner does anything.</para>
///
/// <para>Under that: this topic's project, then this chat's project, then the run that received it.
/// Each rung is a weaker claim about intent, so each one is NAMED in the acknowledgement. And under
/// all of them the invariant that outranks routing entirely — a note is never dropped. A project
/// that cannot be filed against parks the note in a machine-level dead-letter box (§6.10) with its
/// audio, and the sender is told by name.</para>
/// </summary>
public sealed class DV3_4RoutingTests : IDisposable
{
    private readonly string _root;      // a scratch STATE HOME, not this machine's
    private readonly string _repos;
    private readonly ITestOutputHelper _out;

    public DV3_4RoutingTests(ITestOutputHelper output)
    {
        _out = output;
        var box = Path.Combine(Path.GetTempPath(), $"conductor-dv34-{Guid.NewGuid():N}");
        _root = Path.Combine(box, "state-home");
        _repos = Path.Combine(box, "repos");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_repos);
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(Directory.GetParent(_root)!.FullName); } catch (Exception) { }
    }

    // ────────────────────────── the push identity line ──────────────────────────

    /// <summary>The parse the headline interaction rests on. <c>MessageComposer.IdentityFor</c> opens
    /// every push with <c>&lt;plan&gt; · s&lt;n&gt;</c>; the wire hands it back as plain text.</summary>
    [Fact]
    public void The_plan_name_is_read_out_of_a_pushs_identity_line()
    {
        Assert.Equal("Divan", NoteRouter.PlanNameIn("Divan · s7\nconductor@feat/divan · DV3 — The inbox"));
        Assert.Equal("Karvansara edge", NoteRouter.PlanNameIn("Karvansara edge · s12"));
    }

    /// <summary>And what it must NOT match. A person's own message that happens to contain a middle
    /// dot is not a push, and a router that thought it was would file notes against a project nobody
    /// named.</summary>
    [Fact]
    public void A_message_that_is_not_a_push_yields_no_plan()
    {
        Assert.Null(NoteRouter.PlanNameIn("lunch · tomorrow"));           // no session marker
        Assert.Null(NoteRouter.PlanNameIn("Divan · session 7"));          // not the marker's shape
        Assert.Null(NoteRouter.PlanNameIn("just a sentence"));
        Assert.Null(NoteRouter.PlanNameIn(""));
        Assert.Null(NoteRouter.PlanNameIn(null));
        Assert.Null(NoteRouter.PlanNameIn(" · s3"));                      // no name in front of it
    }

    // ────────────────────────── the ladder ──────────────────────────

    /// <summary>§1.5 (1), the headline: two projects on this machine, a reply to the SECOND one's
    /// push, and no command typed anywhere.</summary>
    [Fact]
    public void A_reply_to_a_push_files_against_that_pushs_project_with_no_command()
    {
        var router = Router(local: "divan");
        Project("payesh");

        var route = router.Route("99", null, "payesh · s4\npayesh@main · P2 — the harvest");

        Assert.Equal(RouteReason.ReplyToPush, route.Reason);
        Assert.Equal("payesh", route.Project!.Plan);
        Assert.Contains("the run you replied to", route.Describe(), StringComparison.Ordinal);
        _out.WriteLine(route.Describe());
    }

    /// <summary>§1.5 (2): typed once, and it stays — including across the restart that a selection
    /// held in memory would not survive. The second <see cref="ChatRoutes"/> here is a different
    /// object reading the same disk, which is what a restarted engine is.</summary>
    [Fact]
    public void A_sticky_selection_outlives_the_process_that_set_it()
    {
        var router = Router(local: "divan");
        var payesh = Project("payesh");

        new ChatRoutes(_root).Set("99", null, payesh.Slug);

        var route = Router(local: "divan").Route("99", null, replyToText: null);
        Assert.Equal(RouteReason.Sticky, route.Reason);
        Assert.Equal("payesh", route.Project!.Plan);
        Assert.Contains("/project to change it", route.Describe(), StringComparison.Ordinal);

        // and the reply still outranks it: a reply to a push is about THAT push.
        var replied = router.Route("99", null, "divan · s7");
        Assert.Equal(RouteReason.ReplyToPush, replied.Reason);
        Assert.Equal("divan", replied.Project!.Plan);
    }

    /// <summary>§1.5 (3): one topic per project in a supergroup. The topic's selection is its own —
    /// setting it must not become the chat's, or a note in another topic files against the wrong
    /// project.</summary>
    [Fact]
    public void A_topics_selection_is_its_own_and_does_not_become_the_chats()
    {
        Router(local: "divan");
        var payesh = Project("payesh");
        var routes = new ChatRoutes(_root);
        routes.Set("-100777", threadId: 42, payesh.Slug);

        var inTopic = Router(local: "divan").Route("-100777", 42, null);
        Assert.Equal(RouteReason.Topic, inTopic.Reason);
        Assert.Equal("payesh", inTopic.Project!.Plan);
        Assert.Contains("this topic's project", inTopic.Describe(), StringComparison.Ordinal);

        var inChat = Router(local: "divan").Route("-100777", threadId: null, null);
        Assert.Equal(RouteReason.LocalRun, inChat.Reason);
        Assert.Equal("divan", inChat.Project!.Plan);

        var otherTopic = Router(local: "divan").Route("-100777", 43, null);
        Assert.Equal(RouteReason.LocalRun, otherTopic.Reason);
    }

    /// <summary>Nothing said otherwise: the run that received it. This is the whole behaviour of
    /// DV3.2 and it has to survive the ladder being added on top of it.</summary>
    [Fact]
    public void With_nothing_selected_a_note_belongs_to_the_run_that_received_it()
    {
        var route = Router(local: "divan").Route("99", null, null);

        Assert.Equal(RouteReason.LocalRun, route.Reason);
        Assert.Equal("divan", route.Project!.Plan);
        Assert.Contains("the run on this machine", route.Describe(), StringComparison.Ordinal);
    }

    // ────────────────────────── refusals, by name ──────────────────────────

    /// <summary>The <c>GithubConfig.Board</c> rule a third time (findings §1.5): a name this machine
    /// does not have is refused WITH the name and with what it does have. "Unknown project" alone is
    /// unanswerable.</summary>
    [Fact]
    public void An_unknown_project_is_refused_by_name_and_says_what_this_machine_has()
    {
        var dir = Directory_(local: "divan");
        Project("payesh");

        var match = dir.Resolve("karvan");

        Assert.Null(match.Project);
        Assert.Contains("\"karvan\"", match.Refusal!, StringComparison.Ordinal);
        Assert.Contains("divan", match.Refusal!, StringComparison.Ordinal);
        Assert.Contains("payesh", match.Refusal!, StringComparison.Ordinal);
        _out.WriteLine(match.Refusal);
    }

    /// <summary>Two clones of one plan is the ordinary way to be ambiguous, and guessing between
    /// them would file the owner's words in the checkout they were not talking about.</summary>
    [Fact]
    public void An_ambiguous_name_is_refused_with_both_candidates_named()
    {
        var dir = Directory_(local: null);
        var a = Project("divan", folder: "divan-main");
        var b = Project("divan", folder: "divan-spike");

        var match = dir.Resolve("divan");

        Assert.Null(match.Project);
        Assert.Contains(a.Slug, match.Refusal!, StringComparison.Ordinal);
        Assert.Contains(b.Slug, match.Refusal!, StringComparison.Ordinal);
        Assert.Contains("divan-main", match.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>A push about a project this machine no longer lists still gets a named answer rather
    /// than a silent fall back to the local run — which would file it in the wrong place and say
    /// nothing.</summary>
    [Fact]
    public void A_reply_to_a_push_from_an_unknown_project_is_refused_not_guessed()
    {
        var route = Router(local: "divan").Route("99", null, "booktocourse · s3");

        Assert.Null(route.Project);
        Assert.Equal(RouteReason.Unknown, route.Reason);
        Assert.Contains("booktocourse", route.Refusal!, StringComparison.Ordinal);
    }

    // ────────────────────────── never dropped ──────────────────────────

    /// <summary>Findings §6.10 — a catalogue entry whose repo has vanished. Refused by name, and the
    /// note is PARKED with its audio in a machine-level box rather than dropped.</summary>
    [Fact]
    public void A_project_whose_checkout_is_gone_is_unroutable_and_the_note_is_parked_with_its_audio()
    {
        var gone = Project("moved-away");
        Directory.Delete(gone.Repo, recursive: true);
        new ChatRoutes(_root).Set("99", null, gone.Slug);

        var route = Router(local: null).Route("99", null, null);

        Assert.Equal(RouteReason.Unroutable, route.Reason);
        Assert.Null(route.Project);
        Assert.Contains("moved-away", route.Refusal!, StringComparison.Ordinal);
        Assert.Contains("checkout is gone", route.Refusal!, StringComparison.Ordinal);

        // and the note survives the failure to route it
        var audio = Path.Combine(_root, "orphan.oga");
        File.WriteAllBytes(audio, [1, 2, 3, 4]);
        var box = new DeadLetterBox(_root);
        var parked = box.Park(new InboxNote(77, DateTime.UtcNow, "99", "voice", "", MediaPath: audio),
            route.Refusal!, audio);

        Assert.NotNull(parked);
        Assert.True(File.Exists(parked!), parked);
        Assert.Single(box.All());
        var written = File.ReadAllText(parked);
        Assert.Contains("moved-away", written, StringComparison.Ordinal);
        Assert.Contains("\"Id\": 77", written, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(box.Dir, "*.oga"));   // the audio came too
        _out.WriteLine(written);
    }

    /// <summary>A note routed to another project takes its audio with it. Without this, "the audio
    /// is kept beside the transcript" is false across a route, and a prune of one project would
    /// orphan a file another project's note points at.</summary>
    [Fact]
    public void Routing_a_note_elsewhere_moves_its_media_into_that_projects_inbox()
    {
        var receiving = new InboxStore(Path.Combine(_repos, "receiving", ".conductor"));
        var target = new InboxStore(Path.Combine(_repos, "target", ".conductor"));
        Directory.CreateDirectory(Path.Combine(receiving.Dir, "media"));
        var arrived = Path.Combine(receiving.Dir, "media", "501-voice.oga");
        File.WriteAllBytes(arrived, [9, 9, 9]);

        var recorded = target.AdoptMedia(arrived);

        Assert.Equal("media/501-voice.oga", recorded);
        Assert.True(File.Exists(Path.Combine(target.Dir, "media", "501-voice.oga")));
        Assert.False(File.Exists(arrived), "the audio was left behind in the receiving project");

        // Adopting something already inside the store is a no-op that still answers relatively.
        Assert.Equal("media/501-voice.oga",
            target.AdoptMedia(Path.Combine(target.Dir, "media", "501-voice.oga")));
    }

    /// <summary>Two projects, two notes, one file name off the wire. A silent overwrite here would
    /// be one owner's voice note replacing another's.</summary>
    [Fact]
    public void Two_files_with_the_same_name_do_not_overwrite_each_other()
    {
        var store = new InboxStore(Path.Combine(_repos, "collide", ".conductor"));
        var first = Path.Combine(_root, "a", "voice.oga");
        var second = Path.Combine(_root, "b", "voice.oga");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2, 2]);

        Assert.Equal("media/voice.oga", store.AdoptMedia(first));
        Assert.Equal("media/voice-2.oga", store.AdoptMedia(second));
        Assert.Equal(1, new FileInfo(Path.Combine(store.Dir, "media", "voice.oga")).Length);
        Assert.Equal(2, new FileInfo(Path.Combine(store.Dir, "media", "voice-2.oga")).Length);
    }

    /// <summary>The verb is on the closed surface list with a scope. KS11.2's matrix test walks that
    /// list; a verb the router answers but the list does not name would default open.</summary>
    [Fact]
    public void The_project_verb_is_on_the_command_surface_as_steer()
    {
        var verb = Assert.Single(Conductor.Core.Integrations.Messaging.SurfaceCommands.All,
            c => c.Verb == "/project");

        Assert.Equal(Conductor.Core.Integrations.Messaging.SurfaceScope.Steer, verb.Scope);
        Assert.False(verb.AllowedFor(Conductor.Core.Integrations.Messaging.ChatProfile.Observer));
    }

    // ────────────────────────── the rig ──────────────────────────

    /// <summary>A project in the scratch catalogue, with a checkout on disk.</summary>
    private ProjectRef Project(string plan, string? folder = null)
    {
        var repo = Path.Combine(_repos, folder ?? plan);
        Directory.CreateDirectory(repo);
        StateCatalogue.Upsert(_root, repo, plan, Path.Combine(_root, "runs", plan, "run.db"));
        return new ProjectRef(plan, repo, StateHome.SlugFor(repo, plan),
            Path.Combine(repo, StateHome.ScratchDirName), true);
    }

    private ProjectDirectory Directory_(string? local)
    {
        var localRef = local is { Length: > 0 } ? Project(local) : null;
        return new ProjectDirectory(_root, localRef);
    }

    private NoteRouter Router(string? local) =>
        new(Directory_(local), new ChatRoutes(_root));
}
