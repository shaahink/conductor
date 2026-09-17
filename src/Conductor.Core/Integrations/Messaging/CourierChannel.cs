using Conductor.Core.Courier;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>DV4.3 / findings §1.4-B — a run pushing THROUGH the courier instead of holding the token.
///
/// <para>This is the payoff KS11.1's seam was built for, and the first proof that the seam is one:
/// the composer, the profiles and the evidence browser do not change a line to send through a
/// daemon instead of a bot. Two verbs, because the seam has two — a queued push and an immediate
/// reply — and here the difference is only which side of the loopback hop the caller waits on.</para>
///
/// <para><b>It was a single point of failure, by choice, and PK3.2 (D3) un-chose it.</b> §1.4-B stated
/// the cost: if the daemon is down the run goes quiet (F-COUR-3). Only POLLING is limited to one
/// consumer per token, so a push no courier takes now goes out through the run's own transport, and
/// <see cref="LastDeliveryFor"/> says which path delivered. This channel still keeps the reason it last
/// failed (<see cref="LastRefusal"/>), and DV1.1's channel health still reports a dead courier loudly:
/// inbound notes are filed by nothing while it is down.</para></summary>
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
    private readonly Func<OutboundMessage, CancellationToken, Task>? _direct;
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
    /// <param name="direct">PK3.2 / D3 - the run's own transport, used when no courier takes a push.
    /// Null keeps the old behaviour: the refusal is recorded and nothing is sent.</param>
    public CourierChannel(IReadOnlyList<ChatTarget> targets, string? stateHomeRoot = null,
        string? origin = null, Action<string>? log = null,
        Func<int?, string?, string>? stamp = null, Func<string?, CourierClient?>? open = null,
        Func<OutboundMessage, CancellationToken, Task>? direct = null)
    {
        _targets = targets ?? [];
        _stateHomeRoot = stateHomeRoot;
        _origin = origin;
        _log = log;
        _stamp = stamp;
        _open = open ?? DefaultOpen;
        _direct = direct;
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
    /// open needs to know whether it arrived.
    ///
    /// <para>PK3.2 / D3: when no courier takes the push - none running, none this run may talk to, or
    /// a connection refused - the message goes out DIRECTLY through the run's own transport, and the
    /// log says <c>courier unreachable - sent directly</c>. The single point of failure this type used
    /// to document about itself was a choice: Telegram's one-consumer rule constrains polling, never
    /// sending. A courier that ANSWERED and refused is not unreachable, and is not gone around - it
    /// said why, and the same messenger would say the same thing to the run.</para></summary>
    public async Task SendAsync(OutboundMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var client = _open(_stateHomeRoot);
        if (client is null)
        {
            await DirectAsync(message, _lastRefusal ?? "the courier is not reachable.", ct).ConfigureAwait(false);
            return;
        }

        var stamp = _stamp?.Invoke(message.SessionNumber, message.StageId);
        var ack = await client.PushAsync(CourierPush.From(message, stamp, _origin), ct).ConfigureAwait(false);
        if (ack.Accepted)
        {
            _lastRefusal = null;
            LastDelivery = new ChannelDelivery(ChannelDelivery.ThroughCourier, DateTimeOffset.UtcNow, null);
        }
        else if (ack.Unanswered) await DirectAsync(message, ack.Detail, ct).ConfigureAwait(false);
        else Record(ack.Detail);
    }

    /// <summary>PK3.2 / D3 - which path the last message this PROCESS sent through a courier channel
    /// for <paramref name="stateHomeRoot"/> took, or null before the first. Process-wide on purpose:
    /// the channel health probe that prints it is static and is asked by the report, <c>/status</c>
    /// and the owner queue of the same engine process, none of which hold the channel. A process that
    /// never ran one (<c>doctor</c>) has nothing to claim, and does not. Keyed by state home because
    /// that is the probe's own key - and so two rigs in one process cannot read each other's.</summary>
    public static ChannelDelivery? LastDeliveryFor(string? stateHomeRoot) =>
        Deliveries.TryGetValue(stateHomeRoot ?? "", out var last) ? last : null;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ChannelDelivery> Deliveries =
        new(StringComparer.OrdinalIgnoreCase);

    private ChannelDelivery LastDelivery
    {
        set => Deliveries[_stateHomeRoot ?? ""] = value;
    }

    private async Task DirectAsync(OutboundMessage message, string why, CancellationToken ct)
    {
        if (_direct is null)
        {
            Record(why);
            return;
        }

        try
        {
            await _direct(message, ct).ConfigureAwait(false);
            _lastRefusal = null;
            LastDelivery = new ChannelDelivery(ChannelDelivery.Directly, DateTimeOffset.UtcNow, why);
            _log?.Invoke("courier unreachable - sent directly: " + why);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Never thrown across the seam: EnqueueAsync is fire-and-forget by contract.
            Record("courier unreachable (" + why + ") and the direct send failed too: " + ex.Message);
        }
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

/// <summary>PK3.2 / D3 - how the last push left this process.</summary>
/// <param name="Path"><see cref="ThroughCourier"/> or <see cref="Directly"/>.</param>
/// <param name="AtUtc">When it went.</param>
/// <param name="CourierRefusal">Why the courier was not used, for a direct send; null otherwise.</param>
public sealed record ChannelDelivery(string Path, DateTimeOffset AtUtc, string? CourierRefusal)
{
    /// <summary>Handed to the courier, which took it.</summary>
    public const string ThroughCourier = "through the courier";

    /// <summary>Sent by the run itself with its own token, because no courier took it.</summary>
    public const string Directly = "directly";

    /// <summary>The clause a health line ends with.</summary>
    public string Describe() =>
        $"last push went {Path} at {AtUtc.UtcDateTime.ToString("HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)}";
}
