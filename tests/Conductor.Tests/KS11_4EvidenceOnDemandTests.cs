using System.Globalization;
using System.Text;

using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS11.4 / CHAPAR CH-6 — depth on demand, driven through the seam with no messenger in sight.
///
/// <para>The complaint behind this checkpoint is "it only sends part of the evidence": a push sends
/// four artifacts and announces the rest, and no reader could ever reach the rest. The exit is that
/// a reader can ASK — so these tests are about what arrives when they do, and about the two things
/// that must not happen when they do: an arbitrary file leaving the machine because a reader named a
/// path, and an upload loop nobody is paying for.</para>
///
/// <para>Every case here is a method call on <see cref="RemoteSurface"/> through the same fake
/// channel CH-1 built. The wire half — an observer's <c>/evidence</c> actually landing a
/// <c>sendDocument</c> in their chat — is <see cref="KS11_4OnWireTests"/>.</para>
/// </summary>
public sealed class KS11_4EvidenceOnDemandTests : IDisposable
{
    private const string Chat = "77";

    private readonly string _repo;
    private readonly FakeChannel _channel = new();

    public KS11_4EvidenceOnDemandTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-ks11e-{Guid.NewGuid():N}", "pull-rig");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(Path.Combine(_repo, "shots"));

        File.WriteAllText(Path.Combine(_repo, "gate-log.md"), new string('g', 512));
        File.WriteAllText(Path.Combine(_repo, "shots", "screen.png"), new string('p', 64));
        File.WriteAllText(Path.Combine(_repo, "notes.md"), "the second artifact of the same claim");

        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# Pull rig\n\n## Checkpoints\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + "| Z1.1 | the gate log | DONE | abc1234 | gate-log.md |\n"
            + "| Z1.2 | two artifacts, one claim | DONE | def5678 | shots/screen.png, notes.md |\n"
            + "| Z1.3 | still open | IN PROGRESS | - | - |\n"
            + "| Z1.4 | the artifact that moved | DONE | 9999999 | gone.md |\n");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(Directory.GetParent(_repo)!.FullName); } catch (Exception) { }
    }

    // ────────────────────────────── the list ──────────────────────────────

    /// <summary>CH-6's first half. The list is the map: every checkpoint that HAS something, and for
    /// each one what it is called and how big it is — including the one whose file has gone, because
    /// a list that quietly omitted it would send the reader to ask for something that cannot come.</summary>
    [Fact]
    public async Task The_list_names_every_checkpoint_that_has_an_artifact_and_says_which_one_has_gone()
    {
        await Surface().HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence", CancellationToken.None);

        var text = Assert.Single(_channel.Sent).Text;
        Assert.Contains("Z1.1", text, StringComparison.Ordinal);
        Assert.Contains("gate-log.md</code> (512 B)", text, StringComparison.Ordinal);
        // One claim, two artifacts: both are listed, neither is folded into the other.
        Assert.Contains("shots/screen.png</code> (64 B)", text, StringComparison.Ordinal);
        Assert.Contains("notes.md</code> (37 B)", text, StringComparison.Ordinal);
        Assert.Contains("gone.md</code> — not on this machine", text, StringComparison.Ordinal);
        // A checkpoint with no artifact is not on a list of artifacts.
        Assert.DoesNotContain("Z1.3", text, StringComparison.Ordinal);
    }

    /// <summary>The push side stops at <c>EvidenceLinesPerPush</c> = 8 because a push nobody asked
    /// for must not be forty lines. A reader who typed the verb DID ask, so the list is bounded by
    /// nothing: thirty claims are thirty entries, and the transport's chunker is what makes that
    /// deliverable.</summary>
    [Fact]
    public async Task The_list_is_not_bounded_by_the_push_side_line_budget()
    {
        var rows = new StringBuilder();
        for (var i = 1; i <= 30; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_repo, $"a{i}.md"), "x");
            rows.Append(CultureInfo.InvariantCulture, $"| Z2.{i} | claim {i} | DONE | c{i} | a{i}.md |\n");
        }

        await File.WriteAllTextAsync(Path.Combine(_repo, "TRACKER.md"),
            "# Pull rig\n\n## Checkpoints\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + rows);

        await Surface().HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence", CancellationToken.None);

        var text = Assert.Single(_channel.Sent).Text;
        for (var i = 1; i <= 30; i++)
            Assert.Contains($"a{i}.md", text, StringComparison.Ordinal);
    }

    // ────────────────────────────── the pull ──────────────────────────────

    /// <summary>CH-6's second half, and the whole point: the artifact ARRIVES. Both artifacts of the
    /// claim, the visual one as a photo and the other as a document — the same rule the push path
    /// uses, applied to a file the tracker named rather than to a registered artifact.</summary>
    [Fact]
    public async Task An_artifact_arrives_as_an_upload_and_a_photo_when_it_is_visual()
    {
        await Surface().HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence Z1.2", CancellationToken.None);

        Assert.Equal(2, _channel.Sent.Count);
        var photo = _channel.Sent[0].Attachment;
        Assert.NotNull(photo);
        Assert.True(photo.AsPhoto);
        Assert.EndsWith("screen.png", photo.Path, StringComparison.Ordinal);
        Assert.True(File.Exists(photo.Path));

        var doc = _channel.Sent[1].Attachment;
        Assert.NotNull(doc);
        Assert.False(doc.AsPhoto);
        Assert.EndsWith("notes.md", doc.Path, StringComparison.Ordinal);

        // The caption says which checkpoint this is proof OF — an artifact with no claim attached is
        // a file, not evidence.
        Assert.Contains("Z1.2", _channel.Sent[0].Text, StringComparison.Ordinal);
    }

    /// <summary>Case does not decide whether a reader gets their evidence — the tracker's own id
    /// lookup is case-insensitive and the surface must not be stricter than the board it reads.</summary>
    [Fact]
    public async Task An_id_is_matched_the_way_the_tracker_matches_it()
    {
        await Surface().HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence z1.1", CancellationToken.None);

        Assert.NotNull(Assert.Single(_channel.Sent).Attachment);
    }

    /// <summary>The security property of the whole verb, and the reason the argument is an id: a
    /// reader names a CHECKPOINT, and the path comes from the row the engine itself wrote. Neither a
    /// traversal nor the true relative path of a real artifact is a way in.</summary>
    [Theory]
    [InlineData("../../../secrets.txt")]
    [InlineData("gate-log.md")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task A_reader_cannot_ask_for_a_path_only_for_a_checkpoint(string argument)
    {
        await Surface().HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence " + argument,
            CancellationToken.None);

        var reply = Assert.Single(_channel.Sent);
        Assert.Null(reply.Attachment);
        Assert.Contains("No checkpoint", reply.Text, StringComparison.Ordinal);
    }

    /// <summary>"Nothing arrived" is the failure this checkpoint exists to end, so every way a pull
    /// can fail to produce a file says which way it was.</summary>
    [Theory]
    [InlineData("Z1.3", "has no artifact recorded yet")]
    [InlineData("Z1.4", "does not resolve to a file")]
    [InlineData("Z9.9", "No checkpoint")]
    public async Task A_pull_that_cannot_send_a_file_says_why(string id, string expected)
    {
        await Surface().HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence " + id, CancellationToken.None);

        var reply = Assert.Single(_channel.Sent);
        Assert.Null(reply.Attachment);
        Assert.Contains(expected, reply.Text, StringComparison.Ordinal);
    }

    /// <summary>The seam's own size cap, which is not the messenger's: a channel refuses what it
    /// cannot carry, but how much of the engine's disk a chat message may pull over the network is a
    /// decision of the surface. The file is named so the owner can still fetch it themselves.</summary>
    [Fact]
    public async Task An_artifact_over_the_pull_cap_is_named_rather_than_sent()
    {
        var big = Path.Combine(_repo, "huge.log");
        await using (var fs = new FileStream(big, FileMode.Create, FileAccess.Write))
            fs.SetLength(MessageComposer.EvidencePullMaxBytes + 1);
        await File.AppendAllTextAsync(Path.Combine(_repo, "TRACKER.md"),
            "| Z1.5 | the big one | DONE | 5555555 | huge.log |\n");

        await Surface().HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence Z1.5", CancellationToken.None);

        var reply = Assert.Single(_channel.Sent);
        Assert.Null(reply.Attachment);
        Assert.Contains("over the 10 MB limit", reply.Text, StringComparison.Ordinal);
        Assert.Contains("huge.log", reply.Text, StringComparison.Ordinal);
    }

    // ────────────────────────────── the budget ──────────────────────────────

    /// <summary>An upload is bytes off the engine's machine while the run is doing something else,
    /// and a chat that can ask for one can ask for a hundred. The refusal has to say WHEN, or it
    /// reads as the feature being broken.</summary>
    [Fact]
    public async Task A_chat_that_pulls_past_its_budget_is_refused_with_a_time_to_ask_again()
    {
        var surface = Surface();
        for (var i = 0; i < MessageComposer.EvidencePullsPerWindow; i++)
            await surface.HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence Z1.1", CancellationToken.None);

        Assert.Equal(MessageComposer.EvidencePullsPerWindow, _channel.Sent.Count(m => m.Attachment is not null));

        await surface.HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence Z1.1", CancellationToken.None);

        var refusal = _channel.Sent[^1];
        Assert.Null(refusal.Attachment);
        Assert.Contains("ask again in", refusal.Text, StringComparison.Ordinal);
        Assert.Contains("still lists them", refusal.Text, StringComparison.Ordinal);
    }

    /// <summary>The budget is per CHAT: the owner reading their run must not be locked out by a
    /// group chat's reader working through the artifact list.</summary>
    [Fact]
    public async Task One_chat_exhausting_its_budget_leaves_the_other_chat_alone()
    {
        var surface = Surface();
        for (var i = 0; i <= MessageComposer.EvidencePullsPerWindow; i++)
            await surface.HandleMessageAsync("-100999", ChatProfile.Observer, "/evidence Z1.1", CancellationToken.None);

        await surface.HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence Z1.1", CancellationToken.None);

        Assert.NotNull(_channel.Sent[^1].Attachment);
        Assert.Equal(Chat, _channel.Sent[^1].ChatId);
    }

    /// <summary>Only an answer that carries a file is charged for. A reader who mistypes an id has
    /// cost the engine a tracker read; charging for that would let three typos lock them out of the
    /// artifact they were trying to reach, which is the opposite of what the limit is for.</summary>
    [Fact]
    public async Task Listing_and_missing_ids_cost_nothing_only_uploads_do()
    {
        var surface = Surface();
        for (var i = 0; i < 20; i++)
        {
            await surface.HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence", CancellationToken.None);
            await surface.HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence Z9.9", CancellationToken.None);
        }

        await surface.HandleMessageAsync(Chat, ChatProfile.Admin, "/evidence Z1.1", CancellationToken.None);

        Assert.NotNull(_channel.Sent[^1].Attachment);
    }

    // ────────────────────────────── the profile ──────────────────────────────

    /// <summary>CH-3 lists <c>/evidence</c> in the closed observer set, and CH-6 is what makes that
    /// list worth having. The stakeholder in the group chat is the whole reason this checkpoint
    /// exists — so an observer pulls a real artifact, and is refused nothing.</summary>
    [Fact]
    public async Task An_observer_pulls_an_artifact_and_is_refused_nothing()
    {
        var surface = Surface();
        await surface.HandleMessageAsync(Chat, ChatProfile.Observer, "/evidence", CancellationToken.None);
        await surface.HandleMessageAsync(Chat, ChatProfile.Observer, "/evidence Z1.1", CancellationToken.None);

        Assert.Equal(2, _channel.Sent.Count);
        Assert.Contains("Z1.1", _channel.Sent[0].Text, StringComparison.Ordinal);
        Assert.NotNull(_channel.Sent[1].Attachment);
        Assert.DoesNotContain("observer", _channel.Sent[1].Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>KS11.3 left <c>/evidence</c> out of the onboarding ask-line because it had no handler
    /// — the list is derived from <see cref="SurfaceCommand.Implemented"/> precisely so that landing
    /// the handler is what adds it, rather than a second edit somebody forgets.</summary>
    [Fact]
    public void The_verb_joins_the_browse_list_by_being_implemented()
    {
        Assert.Contains("/evidence", SurfaceCommands.BrowseList, StringComparison.Ordinal);
        Assert.Contains("/evidence", SurfaceCommands.AskLine(ChatProfile.Observer, twoWay: false),
            StringComparison.Ordinal);
        // KS11.5 landed /progress, /money and /tokens, and the same flag is what put them in the
        // list: every verb the list promises has a handler, which is the invariant, not the count.
        foreach (var promised in SurfaceCommands.BrowseList.Split(", "))
            Assert.True(SurfaceCommands.Find(promised)?.Implemented == true,
                $"the ask-line promises {promised}, which has no handler");
    }

    // ────────────────────────────── the limiter itself ──────────────────────────────

    /// <summary>The window REOPENING is the half a test cannot observe by hammering the surface, and
    /// a test that sleeps for ten minutes is a test somebody shortens until it proves nothing — so
    /// the limiter takes its clock as an argument.</summary>
    [Fact]
    public void The_window_reopens_one_slot_at_a_time_as_the_oldest_take_falls_out()
    {
        var now = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new PullRateLimiter(2, TimeSpan.FromMinutes(10), () => now);

        Assert.True(limiter.TryTake("a", out _));
        now = now.AddMinutes(4);
        Assert.True(limiter.TryTake("a", out _));
        Assert.False(limiter.TryTake("a", out var retry));
        Assert.Equal(TimeSpan.FromMinutes(6), retry);

        // The first take falls out of the window and frees exactly one slot, not the whole budget.
        now = now.AddMinutes(6);
        Assert.True(limiter.TryTake("a", out _));
        Assert.False(limiter.TryTake("a", out _));
    }

    [Fact]
    public void A_key_that_has_never_asked_is_not_charged_for_another_keys_takes()
    {
        var limiter = new PullRateLimiter(1, TimeSpan.FromMinutes(10));

        Assert.True(limiter.TryTake("a", out _));
        Assert.False(limiter.TryTake("a", out _));
        Assert.True(limiter.TryTake("b", out _));
    }

    // ────────────────────────────── the rig ──────────────────────────────

    private RemoteSurface Surface()
    {
        var plan = new PlanConfig
        {
            Name = "Pull rig",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "Z1", Title = "Evidence on demand", Sessions = 1 } },
        };
        var state = new RunState { RunId = "pull-run", SessionCounter = 3, CurrentStage = "Z1" };
        var composer = new MessageComposer(plan, state, ProgressProviderFactory.Create(plan), null, _ => { });
        return new RemoteSurface(_channel, composer, new CommandRouter(composer, plan), state, null,
            (_, _, _) => Task.CompletedTask, (_, _) => { });
    }

    /// <summary>The same fake CH-1 built, kept local to this file for the same reason: a test that
    /// proves what a reader receives must not be able to receive it through a Telegram type.</summary>
    private sealed class FakeChannel : IMessageChannel
    {
        public string Name => "fake";
        public bool IsLive => true;
        public bool AllowsControl => true;
        public IReadOnlyList<ChatTarget> Targets => [new ChatTarget(Chat, ChatProfile.Admin)];

        public List<OutboundMessage> Sent { get; } = [];

        public Task EnqueueAsync(OutboundMessage message, CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(OutboundMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
