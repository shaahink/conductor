using Conductor.Core;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Planning;
using Conductor.Models;
using CheckpointRow = Conductor.Core.CheckpointRow;

namespace Conductor.Tests;

/// <summary>
/// DV1.2 — the owner queue reaches the owner.
///
/// <para>SF4.1 has regenerated <c>.conductor/OWNER-QUEUE.md</c> at every session boundary since it
/// landed, and both of its readers — the file and <c>GET /owner/queue</c> — require somebody to be
/// LOOKING. The case the surface was written for is the one where nobody is: a run that parks at
/// 3am on an owner gate stands still until somebody opens a laptop.</para>
///
/// <para>Four properties, and three of them are about restraint. One message per obligation, so a
/// queue of three is three things to deal with rather than one; admin chats only, because every
/// entry carries a control command; and nothing at all when the queue has not changed, because a
/// channel that repeats itself at every boundary is one the owner mutes — at which point this whole
/// surface is worth less than nothing.</para>
/// </summary>
public sealed class DV1_2OwnerQueuePushTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"conductor-dv12-{Guid.NewGuid():N}");
    private readonly FakeChannel _channel = new();
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    public DV1_2OwnerQueuePushTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_repo); } catch (IOException) { /* best effort */ }
    }

    // ---- one message per obligation --------------------------------------------------------------

    /// <summary>The batching this checkpoint replaced: <c>RunContext</c> built ONE message listing
    /// every new item. Three obligations arriving as one notification read as one thing to deal with
    /// later, and the two under the first one are the ones that get lost.</summary>
    [Fact]
    public async Task ThreeObligations_ArriveAsThreeMessages_NotOneDigest()
    {
        await Surface().PushOwnerQueueAsync(Items(3), Now, CancellationToken.None);

        Assert.Equal(3, _channel.Queued.Count);
        Assert.Equal(["approve S1", "approve S2", "approve S3"],
            _channel.Queued.Select(m => Between(m.Text, "<b>", "</b>")).ToList());
    }

    /// <summary>The command is the line a phone reader taps, so it is in monospace and it is the
    /// item's OWN command — not a generic "go and look at the queue", which is what a digest link
    /// would have been.</summary>
    [Fact]
    public async Task EachMessageCarriesTheExactClearingCommand()
    {
        await Surface().PushOwnerQueueAsync(Items(1), Now, CancellationToken.None);

        var text = Assert.Single(_channel.Queued).Text;
        Assert.Contains("clears with: <code>conductor approve</code>", text, StringComparison.Ordinal);
        Assert.Contains("unblocks: stage S1 and everything after it", text, StringComparison.Ordinal);
    }

    /// <summary>A blocked-until wait clears itself. Inventing a command for it would send the owner
    /// to a keyboard for nothing, so the empty command is rendered as the fact it is.</summary>
    [Fact]
    public async Task AnObligationWithNoCommand_SaysSo_RatherThanInventingOne()
    {
        var wait = new OwnerQueueItem("wait", "wait", "waiting until 15:12Z", "nothing you can hurry",
            "", Now.AddMinutes(-5), 3);
        await Surface().PushOwnerQueueAsync([wait], Now, CancellationToken.None);

        Assert.Contains("clears with: nothing to type — it clears itself",
            Assert.Single(_channel.Queued).Text, StringComparison.Ordinal);
        // The telemetry line is monospace too, so the bar is the CLEARS line specifically.
        Assert.DoesNotContain("clears with: <code>", _channel.Queued[0].Text, StringComparison.Ordinal);
    }

    /// <summary>The owner must ACT on this, which is the whole test for severity in this engine.</summary>
    [Fact]
    public async Task AnObligationBuzzes()
    {
        await Surface().PushOwnerQueueAsync(Items(1), Now, CancellationToken.None);
        Assert.Equal(PushSeverity.Alert, Assert.Single(_channel.Queued).Severity);
    }

    // ---- admin chats only -------------------------------------------------------------------------

    /// <summary>CH-3's rule for keyboards, applied to the to-do list: a chat that may not run
    /// <c>conductor approve</c> is not helped by being told to. The observer's copy of the run's
    /// STORY stays unfiltered — that bar lives in the grammar goldens — but the owner's obligations
    /// are not part of that story.</summary>
    [Fact]
    public async Task AnObserverChatIsNotToldWhatOnlyTheOwnerCanDo()
    {
        _channel.SetTargets(new ChatTarget("77", ChatProfile.Admin), new ChatTarget("99", ChatProfile.Observer));

        await Surface().PushOwnerQueueAsync(Items(2), Now, CancellationToken.None);

        Assert.Equal(2, _channel.Queued.Count);
        Assert.All(_channel.Queued, m => Assert.Equal("77", m.ChatId));
    }

    /// <summary>A push-only roster of observers is not an error and not a silent drop of the file:
    /// the queue is still written, and this surface simply has nobody to tell.</summary>
    [Fact]
    public async Task AnAllObserverRoster_SendsNothingAndDoesNotThrow()
    {
        _channel.SetTargets(new ChatTarget("99", ChatProfile.Observer));
        await Surface().PushOwnerQueueAsync(Items(2), Now, CancellationToken.None);
        Assert.Empty(_channel.Queued);
    }

    /// <summary>A dead channel is not a reason to lose the queue — the file is written either way,
    /// and this is the guard every other push in the surface has.</summary>
    [Fact]
    public async Task ADeadChannelSendsNothing()
    {
        _channel.IsLive = false;
        await Surface().PushOwnerQueueAsync(Items(2), Now, CancellationToken.None);
        Assert.Empty(_channel.Queued);
    }

    // ---- no change, no push -----------------------------------------------------------------------

    /// <summary>The restraint half, end to end through <see cref="OwnerQueue.Write"/> — the path the
    /// engine actually takes, four call sites across three classes, several times per session.
    ///
    /// <para>A report written twenty times in a session must announce each obligation ONCE. The
    /// memory is the key marker inside the rendered file, so this also proves the marker survives a
    /// round trip through the markdown it is written into.</para></summary>
    [Fact]
    public async Task AnUnchangedQueueSendsNothingOnEveryLaterBoundary()
    {
        var plan = Plan();
        var state = Parked();
        var surface = Surface(plan, state);

        await WriteAsync(surface, plan, state);
        var afterFirst = _channel.Queued.Count;

        await WriteAsync(surface, plan, state);
        await WriteAsync(surface, plan, state);

        Assert.Equal(1, afterFirst);
        Assert.Single(_channel.Queued);
    }

    /// <summary>Only what is NEW. A second obligation appearing beside one already announced pushes
    /// once, not twice — the diff is per item, which is precisely what one-message-per-item buys.
    /// </summary>
    [Fact]
    public async Task ANewObligationBesideAnAnnouncedOne_PushesOnlyTheNewOne()
    {
        var plan = Plan();
        var state = Parked();
        var surface = Surface(plan, state);

        await WriteAsync(surface, plan, state);
        Assert.Single(_channel.Queued);

        state.SkippedStages.Add("S1");
        await WriteAsync(surface, plan, state);

        Assert.Equal(2, _channel.Queued.Count);
        Assert.Contains("S1 was skipped", _channel.Queued[1].Text, StringComparison.Ordinal);
    }

    /// <summary>An obligation that CLEARS is good news the run makes on its own; announcing it here
    /// would turn the channel into a running commentary, which is the noise this checkpoint exists to
    /// avoid.</summary>
    [Fact]
    public async Task AClearedObligationIsNotAnnounced()
    {
        var plan = Plan();
        var state = Parked();
        var surface = Surface(plan, state);

        await WriteAsync(surface, plan, state);
        Assert.Single(_channel.Queued);

        state.Status = RunStatus.Running;
        state.AttentionReason = null;
        await WriteAsync(surface, plan, state);

        Assert.Single(_channel.Queued);
    }

    // ---- the rig ----------------------------------------------------------------------------------

    /// <summary>The engine's own path: <see cref="OwnerQueue.Write"/> decides what is new, and the
    /// callback is exactly what <c>RunContext.NotifyNewOwnerQueueItems</c> hands the surface.</summary>
    private async Task WriteAsync(RemoteSurface surface, PlanConfig plan, RunState state)
    {
        IReadOnlyList<OwnerQueueItem> fresh = [];
        OwnerQueue.Write(plan, state, new TrackerSnapshot(), _ => { }, Now, items => fresh = items);
        if (fresh.Count > 0) await surface.PushOwnerQueueAsync(fresh, Now, CancellationToken.None);
    }

    private PlanConfig Plan() => new()
    {
        Name = "dv12-rig",
        Repo = _repo.Replace("\\", "/", StringComparison.Ordinal),
        Tracker = "TRACKER.md",
        Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
        Stages = [new StageConfig { Id = "S1", Title = "One stage", Sessions = 1 }],
    };

    private static RunState Parked() => new()
    {
        RunId = "dv12",
        Status = RunStatus.Paused,
        CurrentStage = "S1",
        AttentionReason = "paused by the operator",
        AttentionSinceUtc = Now.AddHours(-2),
    };

    private RemoteSurface Surface(PlanConfig? plan = null, RunState? state = null)
    {
        plan ??= Plan();
        state ??= new RunState { RunId = "dv12", CurrentStage = "S1" };
        var composer = new MessageComposer(plan, state, ProgressProviderFactory.Create(plan), null, _ => { });
        return new RemoteSurface(_channel, composer, new CommandRouter(composer, plan), state, null,
            (_, _, _) => Task.CompletedTask, (_, _) => { });
    }

    private static IReadOnlyList<OwnerQueueItem> Items(int n) =>
        [.. Enumerable.Range(1, n).Select(i => new OwnerQueueItem(
            Id: $"gate-S{i}",
            Kind: "ownerGate",
            Title: $"approve S{i}",
            Unblocks: $"stage S{i} and everything after it",
            Command: "conductor approve",
            SinceUtc: Now.AddMinutes(-30),
            Rank: 2,
            Detail: "green gates are not enough for this stage"))];

    private static string Between(string text, string open, string close)
    {
        var a = text.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var b = text.IndexOf(close, a, StringComparison.Ordinal);
        return text[a..b];
    }

    /// <summary>KS11.1's fake channel, reused: CH-1 says the seam is proved by a fake rather than by
    /// a second real messenger, and this checkpoint is the first push that needed to know WHICH chat
    /// it reached, which is exactly what the fake records.</summary>
    private sealed class FakeChannel : IMessageChannel
    {
        public string Name => "fake";
        public bool IsLive { get; set; } = true;
        public bool AllowsControl { get; set; } = true;
        public IReadOnlyList<ChatTarget> Targets { get; private set; } = [new ChatTarget("77", ChatProfile.Admin)];

        public List<OutboundMessage> Queued { get; } = [];

        public void SetTargets(params ChatTarget[] targets) => Targets = targets;

        public Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
        {
            if (IsLive) Queued.Add(message);
            return Task.CompletedTask;
        }

        public Task SendAsync(OutboundMessage message, CancellationToken ct) => Task.CompletedTask;
    }
}
