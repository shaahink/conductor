namespace Conductor.Core.Courier;

/// <summary>PK3.1 / D5 - what the courier's loopback does with each verb, once the listener has
/// authenticated the caller and read the body. The listener owns HTTP; this owns meaning.</summary>
public interface ICourierDesk
{
    /// <summary>A run's protocol-2 push, still accepted this era.</summary>
    Task<CourierAck> PushAsync(CourierPush push, CancellationToken ct);

    /// <summary>A protocol-3 send: the chat resolved, the ceilings checked, the ids answered.</summary>
    Task<CourierAck> SendAsync(CourierSend send, CancellationToken ct);

    /// <summary>A reaction on a message by id.</summary>
    Task<CourierAck> ReactAsync(CourierReact react, CancellationToken ct);

    /// <summary>A message taken back by id.</summary>
    Task<CourierAck> DeleteAsync(CourierDelete delete, CancellationToken ct);

    /// <summary>The chats this courier lists, each with its profile.</summary>
    IReadOnlyList<CourierChat> Chats();
}

/// <summary>PK3.1 / D5 - the courier's desk: resolves the chat a sender named, hands the send to the
/// source, and writes every id that came back into <c>messages.jsonl</c>.
///
/// <para>Messenger-neutral on purpose. The ceilings are the messenger's and are refused inside the
/// source (<see cref="ICourierSource.SendAsync(CourierSend, string, CancellationToken)"/>); chat
/// resolution and the ledger are the courier's, and they are the same whichever wire is behind it.</para>
///
/// <para>The ledger is written AFTER the send and never turns a delivered message into a refusal:
/// by then the message is in the chat, and the sender has to be told so. A ledger that could not be
/// written is said in the courier's log.</para></summary>
/// <param name="source">The wire.</param>
/// <param name="settings">The courier's settings, for resolving a profile name to its chat.</param>
/// <param name="stateHomeRoot">The state home the ledger lives under, or null for the resolved one.</param>
/// <param name="log">Where a ledger that could not be written is said.</param>
/// <param name="clock">The time a ledger line is stamped with, or null for now.</param>
public sealed class CourierDesk(ICourierSource source, CourierSettings settings, string? stateHomeRoot,
    Action<string>? log = null, Func<DateTimeOffset>? clock = null) : ICourierDesk
{
    /// <summary>The ledger verb for a protocol-2 push.</summary>
    public const string PushVerb = "push";

    /// <summary>The ledger verb for a protocol-3 send.</summary>
    public const string SendVerb = "send";

    /// <summary>The ledger verb for a message taken back.</summary>
    public const string DeleteVerb = "delete";

    /// <inheritdoc />
    public async Task<CourierAck> PushAsync(CourierPush push, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(push);
        var ack = await source.SendAsync(push, ct).ConfigureAwait(false);
        if (ack.Accepted) Record(ack.MessageIds, ack.ChatId ?? push.ChatId, push.Origin, push.Stamp, PushVerb);
        return ack;
    }

    /// <inheritdoc />
    public async Task<CourierAck> SendAsync(CourierSend send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (settings.ChatFor(send.Chat, out var refusal) is not { } chatId) return new CourierAck(false, refusal!);

        var ack = await source.SendAsync(send, chatId, ct).ConfigureAwait(false);
        if (ack.Accepted) Record(ack.MessageIds, chatId, send.Origin, send.Stamp, SendVerb);
        return ack with { ChatId = chatId };
    }

    /// <inheritdoc />
    public async Task<CourierAck> ReactAsync(CourierReact react, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(react);
        if (settings.ChatFor(react.Chat, out var refusal) is not { } chatId) return new CourierAck(false, refusal!);

        var why = await source.ReactAsync(chatId, react.MessageId, react.Emoji, ct).ConfigureAwait(false);
        return new CourierAck(why is null, why ?? "", [react.MessageId], chatId);
    }

    /// <inheritdoc />
    public async Task<CourierAck> DeleteAsync(CourierDelete delete, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delete);
        if (settings.ChatFor(delete.Chat, out var refusal) is not { } chatId) return new CourierAck(false, refusal!);

        var why = await source.DeleteAsync(chatId, delete.MessageId, ct).ConfigureAwait(false);
        if (why is null) Record([delete.MessageId], chatId, delete.Origin, null, DeleteVerb);
        return new CourierAck(why is null, why ?? "", [delete.MessageId], chatId);
    }

    /// <inheritdoc />
    public IReadOnlyList<CourierChat> Chats() => settings.ChatList();

    private void Record(IReadOnlyList<long>? ids, string chatId, string? origin, string? stamp, string verb)
    {
        if (ids is not { Count: > 0 }) return;
        var when = clock?.Invoke() ?? DateTimeOffset.UtcNow;
        var why = CourierMessageLedger.Append(
            ids.Select(id => new CourierMessage(id, chatId, origin, stamp, when, verb)), stateHomeRoot);
        if (why is not null) log?.Invoke("courier message ledger: " + why);
    }
}
