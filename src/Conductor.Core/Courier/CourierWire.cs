using System.Text.Json;
using System.Text.Json.Serialization;

using Conductor.Core.Integrations.Messaging;

namespace Conductor.Core.Courier;

/// <summary>DV4.3 — one push, on the wire between a run and the daemon.
///
/// <para><see cref="OutboundMessage"/> is deliberately NOT serialised directly: it carries a
/// <c>TaskCompletionSource</c> (the send loop's receipt) which is a promise inside one process and
/// meaningless in another, and a record that silently drops a field on the wire is how a caller
/// comes to believe a push was acknowledged when nothing acknowledged it. The receipt stays local;
/// what crosses is what a chat can actually receive.</para>
///
/// <para>The attachment travels as a PATH, not as bytes, and that is a property of loopback rather
/// than a shortcut: the daemon is on the same machine as the run by construction, so handing it the
/// filename is strictly better than copying the file through a socket to a process that could have
/// opened it.</para></summary>
/// <param name="ChatId">The chat this copy is addressed to.</param>
/// <param name="Text">The body, without the identity stamp — the sender stamps on the wire.</param>
/// <param name="Buttons">Offered actions, as text and callback token. Never a wire keyboard: only
/// the adapter knows what one looks like (KS11.1).</param>
/// <param name="SessionNumber">The number the identity line carries.</param>
/// <param name="Severity">Notify or silent, as the enum's name so a version skew reads.</param>
/// <param name="StageId">The stage this message is ABOUT.</param>
/// <param name="AttachmentPath">A file on this machine to send instead of text, or null.</param>
/// <param name="AttachmentAsPhoto">Whether that file should render inline.</param>
/// <param name="AttachmentCaption">The caption, already escaped by the composer.</param>
/// <param name="Protocol">What the RUN speaks. The courier refuses a newer one by name rather than
/// guessing at a field it has never heard of.</param>
/// <param name="Stamp">The identity block the RUN rendered — "repo@branch - stage - checkpoint",
/// plus the line every conductor push is recognised by. It crosses pre-rendered because only the run
/// has a plan and a tracker to render it from; a daemon that tried would either need a plan it does
/// not have or would drop the line, and §1.5 makes that line the routing mechanism: a reply to a
/// push files against the project that push came from.</param>
/// <param name="Origin">Which run sent it, for the daemon's log. Never used for routing — the chat
/// id is the address, and a daemon that routed on a caller-supplied name would let any local process
/// choose a chat by claiming to be a run.</param>
public sealed record CourierPush(
    string ChatId,
    string Text,
    IReadOnlyList<CourierButton>? Buttons = null,
    int? SessionNumber = null,
    string Severity = nameof(PushSeverity.Quiet),
    string? StageId = null,
    string? AttachmentPath = null,
    bool AttachmentAsPhoto = false,
    string? AttachmentCaption = null,
    int Protocol = CourierProtocol.Version,
    string? Stamp = null,
    string? Origin = null)
{
    /// <summary>The severity as the engine's enum, defaulting to the quiet one. An unknown word from
    /// a newer run does NOT buzz the owner's phone: the safe reading of "I do not know how loud this
    /// is" is quiet, and the protocol number is what catches the skew properly.</summary>
    public PushSeverity ParsedSeverity() =>
        Enum.TryParse<PushSeverity>(Severity, ignoreCase: true, out var s) ? s : PushSeverity.Quiet;

    /// <summary>The push as the send path already understands it. The receipt is left null on
    /// purpose — see the type remarks.</summary>
    public OutboundMessage ToMessage() => new(
        ChatId, Text,
        Buttons is { Count: > 0 } b ? [.. b.Select(x => new MessageButton(x.Text, x.CallbackData))] : null,
        Ack: null,
        SessionNumber: SessionNumber,
        Severity: ParsedSeverity(),
        Attachment: AttachmentPath is { Length: > 0 } p
            ? new OutboundAttachment(p, AttachmentAsPhoto, AttachmentCaption ?? "")
            : null,
        StageId: StageId);

    /// <summary>The body as it goes on the wire: the run's stamp, then the text. One place, so the
    /// daemon and a test cannot disagree about where the identity line sits.</summary>
    public string Stamped() =>
        string.IsNullOrWhiteSpace(Stamp) ? Text : Stamp + "\n" + Text;

    /// <summary>The wire form of a message the engine already composed.</summary>
    /// <param name="message">What the composer produced.</param>
    /// <param name="stamp">The rendered identity block — see <see cref="Stamp"/>.</param>
    /// <param name="origin">The run's name, for the daemon's log.</param>
    public static CourierPush From(OutboundMessage message, string? stamp = null, string? origin = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new CourierPush(
            message.ChatId, message.Text,
            message.Buttons is { Count: > 0 } b ? [.. b.Select(x => new CourierButton(x.Text, x.CallbackData))] : null,
            message.SessionNumber,
            message.Severity.ToString(),
            message.StageId,
            message.Attachment?.Path,
            message.Attachment?.AsPhoto ?? false,
            message.Attachment?.Caption,
            CourierProtocol.Version,
            stamp,
            origin);
    }
}

/// <summary>One offered action on the wire — <see cref="MessageButton"/> without the struct.</summary>
public sealed record CourierButton(string Text, string CallbackData);

/// <summary>What the daemon says back. Never a bare boolean: a push that did not go out has a reason
/// and the run has to be able to print it, which is the whole of DV1.1's argument applied to a new
/// hop.</summary>
/// <param name="Accepted">Whether the daemon took responsibility for delivering it.</param>
/// <param name="Detail">Why not, in one sentence, or the empty string.</param>
public sealed record CourierAck(bool Accepted, string Detail = "");

/// <summary>The shared JSON shape. One options object for both ends: camelCase, case-insensitive on
/// read, nulls omitted — the same settings every other courier file uses, so a person reading
/// <c>courier.run.json</c> and a person reading a captured request see the same names.</summary>
public static class CourierJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
