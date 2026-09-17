using System.Text;
using System.Text.Json;

namespace Conductor.Core.Courier;

/// <summary>PK3.1 / D5 - one line of the message-id ledger.</summary>
/// <param name="Id">The id the messenger assigned.</param>
/// <param name="Chat">The chat id it lives in. An id, never a profile name: a profile can be pointed
/// at another chat tomorrow, and the ledger has to keep naming the one the message is in.</param>
/// <param name="Origin">Which process asked for it - a run, <c>say</c>, a watcher.</param>
/// <param name="Stamp">Who composed it, as the sender stamped it, or null.</param>
/// <param name="When">When the courier recorded it, UTC.</param>
/// <param name="Verb">What happened to the id: <c>send</c>, <c>push</c>, or <c>delete</c>.</param>
public sealed record CourierMessage(long Id, string Chat, string? Origin, string? Stamp,
    DateTimeOffset When, string Verb);

/// <summary>PK3.1 / D5 - <c>messages.jsonl</c> in the courier's home: the ids of everything this
/// courier put in a chat or took out of one.
///
/// <para>It exists because the ids did not. Before protocol 3 the courier answered a push with
/// accepted-or-not, so nothing a run said could be replied to, reacted to or taken back, and a
/// watcher who needed an id forwarded messages into the admin DM one at a time to read it off the
/// copy. The courier is the process that SEES each id come back; it is the one that writes it down.</para>
///
/// <para>Append-only JSON lines, so a reader never needs a lock and a crash costs at most the line
/// being written. Writing it never fails a send: the message is already in the chat by the time
/// the ledger hears of it, and a refusal then would tell the sender something false.</para></summary>
public static class CourierMessageLedger
{
    /// <summary>One writer at a time. The listener serves requests concurrently, and two appends
    /// interleaving their bytes is a torn line nobody can parse.</summary>
    private static readonly Lock Gate = new();

    /// <summary>Appends <paramref name="messages"/>, and returns why it could not, or null.</summary>
    public static string? Append(IEnumerable<CourierMessage> messages, string? stateHomeRoot = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var sb = new StringBuilder();
        foreach (var m in messages)
            sb.Append(JsonSerializer.Serialize(m, CourierJson.Options)).Append('\n');
        if (sb.Length == 0) return null;

        var path = CourierHome.MessagesPathFor(stateHomeRoot);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the message ledger at {path} could not be written ({ex.Message}).";
        }
    }

    /// <summary>Every readable line, oldest first. A torn or foreign line is skipped, not fatal:
    /// the ledger is evidence, and one bad line must not hide the rest.</summary>
    public static IReadOnlyList<CourierMessage> Read(string? stateHomeRoot = null)
    {
        var path = CourierHome.MessagesPathFor(stateHomeRoot);
        if (!File.Exists(path)) return [];

        var read = new List<CourierMessage>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<CourierMessage>(line, CourierJson.Options) is { } m) read.Add(m);
            }
            catch (JsonException)
            {
                // A line torn by a kill mid-append. The rest still count.
            }
        }
        return read;
    }
}
