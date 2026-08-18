using Conductor.Core;
using Conductor.Core.Evidence;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS11.1, the seam half — the whole remote surface, driven by a channel that is not Telegram.
///
/// <para>This is CH-1's falsifiable exit and the reason the seam was worth extracting. Before it,
/// asking "what would this run say about that session?" or "what does it do when an unknown command
/// arrives?" meant standing up an HTTP listener impersonating api.telegram.org, starting a hosted
/// service, and waiting for a long-poll. Every question below is now a method call.</para>
///
/// <para>There is no Telegram type in this file, and that is load-bearing rather than tidy:
/// <see cref="KS11_1SeamBoundaryTests"/> is what stops one coming back.</para>
/// </summary>
public sealed class KS11_1FakeChannelTests : IDisposable
{
    private readonly string _repo;
    private readonly FakeChannel _channel = new();

    public KS11_1FakeChannelTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-ks11f-{Guid.NewGuid():N}", "fake-rig");
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# Fake rig\n\n## Checkpoints\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + "| Z1.1 | first | DONE | abc1234 | e.md |\n| Z1.2 | second | IN PROGRESS | | |\n");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(Directory.GetParent(_repo)!.FullName); } catch (Exception) { }
    }

    // ────────────────────────────── outbound ──────────────────────────────

    [Fact]
    public async Task Every_push_type_reaches_the_channel_without_a_messenger_in_sight()
    {
        var surface = Surface();

        await surface.PushAsync("a plain engine push", PushSeverity.Quiet, CancellationToken.None);
        await surface.PushSessionEndAsync(
            new SessionEndPush(3, "Z1", "Advanced", "gates:2/2", null, 0.5m, null, 0, ["Z1.1"], false),
            CancellationToken.None);
        await surface.PushRunCompleteAsync(new RunCompletePush(3, 2, 2, null, []), CancellationToken.None);

        Assert.Equal(3, _channel.Queued.Count);
        Assert.Contains("a plain engine push", _channel.Queued[0].Text, StringComparison.Ordinal);
        // K5.2's progress line rides every engine push, and the fake channel sees it too.
        Assert.Contains("progress: 1/2 checkpoints", _channel.Queued[0].Text, StringComparison.Ordinal);
        Assert.Contains("Advanced", _channel.Queued[1].Text, StringComparison.Ordinal);
        Assert.Equal(3, _channel.Queued[1].SessionNumber);
        Assert.Contains("run complete", _channel.Queued[2].Text, StringComparison.Ordinal);
        // A finished run buzzes; a progress line does not.
        Assert.Equal(PushSeverity.Quiet, _channel.Queued[0].Severity);
        Assert.Equal(PushSeverity.Alert, _channel.Queued[2].Severity);
    }

    /// <summary>The fan-out is the seam's, so a second chat is a second copy of the same body — the
    /// property that used to be provable only by counting HTTP posts.</summary>
    [Fact]
    public async Task A_push_is_copied_once_per_configured_chat()
    {
        _channel.SetTargets(new ChatTarget("1", ChatProfile.Admin), new ChatTarget("2", ChatProfile.Observer));

        await Surface().PushAsync("one body, two chats", PushSeverity.Quiet, CancellationToken.None);

        Assert.Equal(2, _channel.Queued.Count);
        Assert.Equal(_channel.Queued[0].Text, _channel.Queued[1].Text);
        Assert.Equal(["1", "2"], _channel.Queued.Select(m => m.ChatId));
    }

    [Fact]
    public async Task A_dead_channel_is_told_nothing()
    {
        _channel.IsLive = false;

        var surface = Surface();
        await surface.PushAsync("dropped", PushSeverity.Quiet, CancellationToken.None);
        await surface.PushRunCompleteAsync(new RunCompletePush(1, 1, 1, null, []), CancellationToken.None);

        Assert.Empty(_channel.Queued);
    }

    [Fact]
    public async Task Evidence_rides_as_attachments_up_to_the_budget_and_as_text_after_it()
    {
        var artifacts = new List<EvidenceArtifact>();
        for (var i = 1; i <= 6; i++)
        {
            var rel = $"e{i}.md";
            await File.WriteAllTextAsync(Path.Combine(_repo, rel), "evidence");
            artifacts.Add(new EvidenceArtifact(rel, i == 1 ? EvidenceKinds.Image : "text", $"Z1.{i}", "Z1",
                4, "sha", 8, DateTimeOffset.UnixEpoch, "session"));
        }

        await Surface().PushEvidenceAsync(artifacts, CancellationToken.None);

        // Four uploads, then one text message naming the two that did not fit.
        Assert.Equal(5, _channel.Queued.Count);
        Assert.Equal(4, _channel.Queued.Count(m => m.Attachment is not null));
        Assert.True(_channel.Queued[0].Attachment!.AsPhoto);
        Assert.False(_channel.Queued[1].Attachment!.AsPhoto);
        Assert.Null(_channel.Queued[4].Attachment);
        Assert.Contains("e5.md", _channel.Queued[4].Text, StringComparison.Ordinal);
        Assert.Contains("e6.md", _channel.Queued[4].Text, StringComparison.Ordinal);
    }

    /// <summary>An artifact whose path no longer resolves must degrade to a line that SAYS so —
    /// never a throw inside a fire-and-forget push.</summary>
    [Fact]
    public async Task An_artifact_that_has_gone_missing_is_announced_rather_than_thrown()
    {
        await Surface().PushEvidenceAsync(
            [new EvidenceArtifact("gone.md", "text", "Z1.1", "Z1", 4, "sha", 8, DateTimeOffset.UnixEpoch, "session")],
            CancellationToken.None);

        var only = Assert.Single(_channel.Queued);
        Assert.Null(only.Attachment);
        Assert.Contains("not attached", only.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_keyboard_is_only_offered_where_control_is_wired()
    {
        _channel.AllowsControl = false;
        await Surface().PushWithKeyboardAsync("approve?", [new MessageButton("Yes", "approve:1")],
            CancellationToken.None);
        Assert.Empty(_channel.Queued);

        _channel.AllowsControl = true;
        await Surface().PushWithKeyboardAsync("approve?", [new MessageButton("Yes", "approve:1")],
            CancellationToken.None);
        var only = Assert.Single(_channel.Queued);
        Assert.Equal("Yes", Assert.Single(only.Buttons!).Text);
        Assert.Equal(PushSeverity.Alert, only.Severity);
    }

    // ────────────────────────────── inbound ──────────────────────────────

    [Theory]
    [InlineData("/status", "Conductor —")]
    [InlineData("/tasks", "Task Graph")]
    // KS11.3 / CH-4: /start answers with the chat's onboarding message now, not one static
    // sentence. The admin version opens by saying what this chat IS.
    [InlineData("/start", "the control surface for a conductor run")]
    // KS11.5 / CH-5: the digest reads in the push grammar now — same answer, same chat, new header.
    [InlineData("/daily", "daily digest")]
    [InlineData("/chat", "conductor chat")]
    public async Task Every_read_command_answers_the_chat_that_asked(string command, string expected)
    {
        await Surface().HandleMessageAsync("77", ChatProfile.Admin, command, CancellationToken.None);

        var reply = Assert.Single(_channel.Sent);
        Assert.Equal("77", reply.ChatId);
        Assert.Contains(expected, reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_command_and_an_empty_message_are_both_silence()
    {
        var surface = Surface();
        await surface.HandleMessageAsync("77", ChatProfile.Admin, "/nonsense", CancellationToken.None);
        await surface.HandleMessageAsync("77", ChatProfile.Admin, "   ", CancellationToken.None);
        await surface.HandleMessageAsync("77", ChatProfile.Admin, "just chatting", CancellationToken.None);

        Assert.Empty(_channel.Sent);
    }

    [Fact]
    public async Task A_control_verb_writes_the_control_file_before_it_acknowledges()
    {
        var written = new List<string>();
        var surface = Surface(writeControl: (action, _, _) => { written.Add(action); return Task.CompletedTask; });

        await surface.HandleMessageAsync("77", ChatProfile.Admin, "/pause", CancellationToken.None);

        Assert.Equal("pause", Assert.Single(written));
        Assert.Contains("pause command sent", Assert.Single(_channel.Sent).Text, StringComparison.Ordinal);
    }

    /// <summary>A destructive verb asks first, and asking must not have already done it.</summary>
    [Fact]
    public async Task A_destructive_verb_asks_and_writes_nothing_until_the_button_comes_back()
    {
        var written = new List<string>();
        var surface = Surface(writeControl: (action, _, _) => { written.Add(action); return Task.CompletedTask; });

        await surface.HandleMessageAsync("77", ChatProfile.Admin, "/abort", CancellationToken.None);

        Assert.Empty(written);
        var ask = Assert.Single(_channel.Sent);
        Assert.Contains("Confirm abort?", ask.Text, StringComparison.Ordinal);
        var yes = ask.Buttons![0];
        Assert.EndsWith(":confirmed", yes.CallbackData, StringComparison.Ordinal);

        await surface.HandleCallbackAsync("77", ChatProfile.Admin, yes.CallbackData, CancellationToken.None);
        Assert.Equal("abort", Assert.Single(written));
    }

    [Fact]
    public async Task A_cancelled_confirmation_writes_nothing()
    {
        var written = new List<string>();
        var surface = Surface(writeControl: (action, _, _) => { written.Add(action); return Task.CompletedTask; });

        await surface.HandleCallbackAsync("77", ChatProfile.Admin, "cancel:deadbeef", CancellationToken.None);
        // An unconfirmed action is not an action.
        await surface.HandleCallbackAsync("77", ChatProfile.Admin, "abort:deadbeef", CancellationToken.None);

        Assert.Empty(written);
        Assert.Equal("Cancelled.", Assert.Single(_channel.Sent).Text);
    }

    /// <summary>The inject button arms the chat, and the NEXT plain message is the instruction —
    /// two exchanges that used to need a live long-poll to observe.</summary>
    [Fact]
    public async Task The_inject_button_arms_the_chat_and_the_next_plain_message_is_the_instruction()
    {
        var surface = Surface();

        await surface.HandleCallbackAsync("77", ChatProfile.Admin, "inject:1", CancellationToken.None);
        Assert.Contains("Reply to this message", _channel.Sent[0].Text, StringComparison.Ordinal);

        await surface.HandleMessageAsync("77", ChatProfile.Admin, "re-run the ratchet gate", CancellationToken.None);
        Assert.Contains("Cannot inject: store is not available", _channel.Sent[1].Text, StringComparison.Ordinal);

        // Armed once, spent once: the message after it is ordinary traffic again.
        await surface.HandleMessageAsync("77", ChatProfile.Admin, "thanks", CancellationToken.None);
        Assert.Equal(2, _channel.Sent.Count);
    }

    [Fact]
    public async Task A_command_typed_while_armed_is_still_a_command()
    {
        var surface = Surface();
        await surface.HandleCallbackAsync("77", ChatProfile.Admin, "inject:1", CancellationToken.None);

        await surface.HandleMessageAsync("77", ChatProfile.Admin, "/start", CancellationToken.None);

        Assert.Contains("the control surface for a conductor run", _channel.Sent[1].Text,
            StringComparison.Ordinal);
    }

    // ────────────────────────────── the rig ──────────────────────────────

    private RemoteSurface Surface(Func<string, bool, string?, Task>? writeControl = null)
    {
        var plan = new PlanConfig
        {
            Name = "Fake rig",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "Z1", Title = "The seam", Sessions = 1 } },
        };
        var state = new RunState { RunId = "fake-run", SessionCounter = 4, CurrentStage = "Z1" };
        var composer = new MessageComposer(plan, state, ProgressProviderFactory.Create(plan), null, _ => { });
        return new RemoteSurface(_channel, composer, new CommandRouter(composer, plan), state, null,
            writeControl ?? ((_, _, _) => Task.CompletedTask), (_, _) => { });
    }

    /// <summary>A channel that delivers to a list. CH-1 says no second channel is built this era and
    /// that a fake proves the same thing for free — this is that fake.</summary>
    private sealed class FakeChannel : IMessageChannel
    {
        public string Name => "fake";
        public bool IsLive { get; set; } = true;
        public bool AllowsControl { get; set; } = true;
        public IReadOnlyList<ChatTarget> Targets { get; private set; } = [new ChatTarget("77", ChatProfile.Admin)];

        public List<OutboundMessage> Queued { get; } = [];
        public List<OutboundMessage> Sent { get; } = [];

        public void SetTargets(params ChatTarget[] targets) => Targets = targets;

        public Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
        {
            if (IsLive) Queued.Add(message);
            return Task.CompletedTask;
        }

        public Task SendAsync(OutboundMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
