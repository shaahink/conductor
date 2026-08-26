using Conductor.Core.Courier;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>DV4.3 / findings §1.4-B — a run pushing THROUGH the courier instead of holding the token.
///
/// <para>This is the payoff KS11.1's seam was built for, and the first proof that the seam is one:
/// the composer, the profiles and the evidence browser do not change a line to send through a
/// daemon instead of a bot. Two verbs, because the seam has two — a queued push and an immediate
/// reply — and here the difference is only which side of the loopback hop the caller waits on.</para>
///
/// <para><b>It is a new single point of failure and it says so.</b> §1.4-B states the cost up front:
/// if the daemon is down the run goes quiet. So this channel keeps the reason it last failed
/// (<see cref="LastRefusal"/>), and DV1.1's channel health reads it — a run whose channel died must
/// say so in <c>REPORT.md</c>, in <c>/status</c> and in the owner queue, or B trades one silent
/// failure for another.</para></summary>
public sealed class CourierChannel : IMessageChannel
{
    /// <summary>The stable channel name — also what an owner-queue item keys on, so no spaces.</summary>
    public const string ChannelName = "courier";

    private readonly IReadOnlyList<ChatTarget> _targets;
    private readonly string? _stateHomeRoot;
    private readonly string? _origin;
    private readonly Func<string?, CourierClient?> _open;
    private readonly Func<int?, string?, string>? _stamp;
    private readonly Action<string>? _log;
    private string? _lastRefusal;

    /// <param name="targets">The chats a push fans out to — the RUN's chats, from its own plan. The
    /// courier's allowlist governs what it will FILE against, never who a run may talk to.</param>
    /// <param name="stateHomeRoot">The machine's state home, or null for the resolved one.</param>
    /// <param name="origin">The run's name, for the daemon's log. Never used for routing.</param>
    /// <param name="log">Where a refusal is written, or null.</param>
    /// <param name="stamp">The run's identity block, by session number and stage — normally
    /// <c>MessageComposer.Stamp</c>. It is rendered HERE and not by the daemon because only a run has
    /// the plan and the tracker the line is made of; see <see cref="CourierPush.Stamp"/>.</param>
    /// <param name="open">How to obtain a client, for a rig. Null uses the real presence record.</param>
    public CourierChannel(IReadOnlyList<ChatTarget> targets, string? stateHomeRoot = null,
        string? origin = null, Action<string>? log = null,
        Func<int?, string?, string>? stamp = null, Func<string?, CourierClient?>? open = null)
    {
        _targets = targets ?? [];
        _stateHomeRoot = stateHomeRoot;
        _origin = origin;
        _log = log;
        _stamp = stamp;
        _open = open ?? DefaultOpen;
    }

    /// <inheritdoc />
    public string Name => ChannelName;

    /// <summary>Whether a courier is running that this run would talk to. Derived, never stored —
    /// <see cref="ChannelHealth"/>'s rule: a stored answer outlives the daemon that justified it,
    /// and the whole failure this guards against is a surface that says "on" while nothing
    /// delivers.</summary>
    public bool IsLive => Refusal() is null;

    /// <summary>False, and deliberately. Two-way traffic belongs to the daemon: it owns the token,
    /// it is the one thing on this machine receiving updates, and a run that also claimed to accept
    /// control verbs would be the second consumer §6.9 exists to prevent.</summary>
    public bool AllowsControl => false;

    /// <inheritdoc />
    public IReadOnlyList<ChatTarget> Targets => _targets;

    /// <summary>Why the last push did not go out, or null. What DV1.1's probe prints.</summary>
    public string? LastRefusal => _lastRefusal;

    /// <summary>Why this channel cannot deliver right now, or null. The three courier refusals
    /// (<see cref="CourierClient.TryOpen"/>) with no bytes sent — cheap enough for a status page.</summary>
    public string? Refusal()
    {
        using var client = CourierClient.TryOpen(_stateHomeRoot, out var why);
        return why;
    }

    /// <summary>Fire-and-forget by contract: this must never throw and never block, so the hop runs
    /// on the pool and its outcome is recorded rather than raised.</summary>
    public Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
    {
        _ = Task.Run(() => SendAsync(message, ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>One message, now. Awaited — a command answer whose caller is holding an HTTP request
    /// open needs to know whether it arrived.</summary>
    public async Task SendAsync(OutboundMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var client = _open(_stateHomeRoot);
        if (client is null)
        {
            Record(_lastRefusal ?? "the courier is not reachable.");
            return;
        }

        var stamp = _stamp?.Invoke(message.SessionNumber, message.StageId);
        var ack = await client.PushAsync(CourierPush.From(message, stamp, _origin), ct).ConfigureAwait(false);
        if (ack.Accepted) _lastRefusal = null;
        else Record(ack.Detail);
    }

    private CourierClient? DefaultOpen(string? stateHomeRoot)
    {
        var client = CourierClient.TryOpen(stateHomeRoot, out var why);
        if (client is null) _lastRefusal = why;
        return client;
    }

    private void Record(string why)
    {
        _lastRefusal = why;
        _log?.Invoke("courier push refused: " + why);
    }
}
