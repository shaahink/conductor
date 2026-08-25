using Conductor.Core;
using Conductor.Core.Planning;
using Conductor.Core.Inbox;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Store;
using Conductor.Models;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV3.4 through the SURFACE — the journeys, end to end, with the store, the router, the ack and the
/// dead-letter box all real and only the messenger faked.
///
/// <para>Three of them: a voice note replying to another project's push lands in THAT project's
/// inbox with its audio and the ack says so; <c>/project</c> sets a selection that the next note
/// obeys; and a note for a project whose checkout has gone is parked rather than dropped, with the
/// sender told by name.</para>
///
/// <para>The state home here is a temp directory passed in, never resolved from the machine: bug #73
/// in this repo's ledger is a rig that wrote scratch runs into the operator's REAL state home, and
/// nothing in this file may repeat it.</para>
/// </summary>
public sealed class DV3_4RoutingSurfaceTests : IDisposable
{
    private readonly string _box;
    private readonly string _root;
    private readonly string _repo;
    private readonly FakeChannel _channel = new();
    private readonly ITestOutputHelper _out;

    public DV3_4RoutingSurfaceTests(ITestOutputHelper output)
    {
        _out = output;
        _box = Path.Combine(Path.GetTempPath(), $"conductor-dv34s-{Guid.NewGuid():N}");
        _root = Path.Combine(_box, "state-home");
        _repo = Path.Combine(_box, "repos", "divan");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), "# rig\n");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_box); } catch (Exception) { }
    }

    /// <summary>§1.5 (1) — the headline interaction, with nothing typed: a voice note sent as a
    /// REPLY to another project's push files against that project, takes its audio with it, and the
    /// acknowledgement says where it went and why.</summary>
    [Fact]
    public async Task A_voice_note_replying_to_another_projects_push_lands_in_that_projects_inbox()
    {
        var payesh = Project("payesh");
        var surface = Surface();

        await surface.HandleNoteAsync(Voice(601, "payesh · s4\npayesh@main · P2 — the harvest"),
            ChatProfile.Admin, CancellationToken.None);

        // it is in payesh's inbox, with its audio beside it
        var filed = Assert.Single(new InboxStore(Path.Combine(payesh.Repo, ".conductor")).All());
        Assert.Equal(601, filed.Id);
        Assert.Equal("media/601-voice.oga", filed.MediaPath);
        Assert.True(File.Exists(Path.Combine(payesh.Repo, ".conductor", "inbox", "media", "601-voice.oga")));

        // and NOT in the run that received it
        Assert.Empty(new InboxStore(Path.Combine(_repo, ".conductor")).All());

        var reply = Assert.Single(_channel.Sent).Text!;
        Assert.Contains("Filed against", reply, StringComparison.Ordinal);
        Assert.Contains("payesh", reply, StringComparison.Ordinal);
        Assert.Contains("the run you replied to", reply, StringComparison.Ordinal);
        _out.WriteLine(reply);
    }

    /// <summary>§1.5 (2) — typed once, obeyed afterwards, and the confirmation names the checkout so
    /// two clones of one plan are distinguishable.</summary>
    [Fact]
    public async Task Project_sets_a_selection_the_next_note_obeys()
    {
        var payesh = Project("payesh");
        var surface = Surface();

        await surface.HandleMessageAsync("77", ChatProfile.Admin, "/project payesh", CancellationToken.None);
        var confirmation = Assert.Single(_channel.Sent).Text!;
        Assert.Contains("now file against", confirmation, StringComparison.Ordinal);
        Assert.Contains("payesh", confirmation, StringComparison.Ordinal);

        await surface.HandleNoteAsync(Voice(602, replyTo: null), ChatProfile.Admin, CancellationToken.None);

        Assert.Single(new InboxStore(Path.Combine(payesh.Repo, ".conductor")).All());
        Assert.Contains("this chat's project", _channel.Sent[^1].Text!, StringComparison.Ordinal);

        // an unknown name is refused BY NAME and changes nothing
        await surface.HandleMessageAsync("77", ChatProfile.Admin, "/project karvan", CancellationToken.None);
        var refusal = _channel.Sent[^1].Text!;
        Assert.Contains("karvan", refusal, StringComparison.Ordinal);
        Assert.Contains("payesh", refusal, StringComparison.Ordinal);
        Assert.Equal(payesh.Slug, new ChatRoutes(_root).Current("77", null));
        _out.WriteLine(refusal);
    }

    /// <summary>Findings §6.10 — the checkout is gone. The note cannot be filed, so it is PARKED
    /// with its audio in the machine-level box and the sender is told by name. Nothing is dropped and
    /// nothing lands in the wrong project.</summary>
    [Fact]
    public async Task A_note_for_a_vanished_checkout_is_parked_and_the_sender_is_told()
    {
        var gone = Project("moved-away");
        new ChatRoutes(_root).Set("77", null, gone.Slug);
        Directory.Delete(gone.Repo, recursive: true);
        var surface = Surface();

        await surface.HandleNoteAsync(Voice(603, replyTo: null), ChatProfile.Admin, CancellationToken.None);

        var reply = Assert.Single(_channel.Sent).Text!;
        Assert.Contains("Kept, not filed", reply, StringComparison.Ordinal);
        Assert.Contains("moved-away", reply, StringComparison.Ordinal);
        Assert.Contains("nothing deletes it", reply, StringComparison.Ordinal);

        var box = new DeadLetterBox(_root);
        var parked = Assert.Single(box.All());
        Assert.Contains("\"Id\": 603", await File.ReadAllTextAsync(parked, CancellationToken.None),
            StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(box.Dir, "*.oga"));       // the audio was parked too
        Assert.Empty(new InboxStore(Path.Combine(_repo, ".conductor")).All());   // and not misfiled
        _out.WriteLine(reply);
    }

    // ── the rig ──

    /// <summary>A voice note as the adapter hands it over: bytes already downloaded into the
    /// RECEIVING run's inbox, which is exactly the state that makes routing have to move them.</summary>
    private InboundNote Voice(long id, string? replyTo)
    {
        var media = Path.Combine(_repo, ".conductor", "inbox", "media");
        Directory.CreateDirectory(media);
        var path = Path.Combine(media, id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-voice.oga");
        File.WriteAllBytes(path, [0x4F, 0x67, 0x67, 0x53]);

        return new InboundNote("77", id, "", new InboundMedia(InboundMediaKind.Voice, "f" + id,
            "voice.oga", "audio/ogg", 4, 3, path, null),
            replyTo is null ? null : 500, replyTo, null, id);
    }

    private ProjectRef Project(string plan)
    {
        var repo = Path.Combine(_box, "repos", plan);
        Directory.CreateDirectory(repo);
        StateCatalogue.Upsert(_root, repo, plan, Path.Combine(_root, "runs", plan, "run.db"));
        return new ProjectRef(plan, repo, StateHome.SlugFor(repo, plan),
            Path.Combine(repo, StateHome.ScratchDirName), true);
    }

    private RemoteSurface Surface()
    {
        var plan = new PlanConfig
        {
            Name = "divan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "DV3", Title = "The inbox", Sessions = 1 } },
        };
        var state = new RunState { RunId = "dv34", SessionCounter = 7, CurrentStage = "DV3" };
        var composer = new MessageComposer(plan, state, ProgressProviderFactory.Create(plan), null, _ => { });
        var local = new ProjectRef("divan", _repo, StateHome.SlugFor(_repo, "divan"),
            Path.Combine(_repo, StateHome.ScratchDirName), true);

        return new RemoteSurface(_channel, composer, new CommandRouter(composer, plan), state, null,
            (_, _, _) => Task.CompletedTask, (_, _) => { },
            inbox: new InboxStore(Path.Combine(_repo, ".conductor")),
            notes: new NoteRouter(new ProjectDirectory(_root, local), new ChatRoutes(_root)));
    }

    /// <summary>A channel that delivers to a list — the same fake KS11.1 established, kept local so
    /// this file has no dependency on another test class's internals.</summary>
    private sealed class FakeChannel : IMessageChannel
    {
        public string Name => "fake";
        public bool IsLive => true;
        public bool AllowsControl => true;
        public IReadOnlyList<ChatTarget> Targets => [new ChatTarget("77", ChatProfile.Admin)];

        public List<OutboundMessage> Sent { get; } = [];

        public Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task SendAsync(OutboundMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
