using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Conductor.Core.Inbox;

namespace Conductor.Core.Courier;

/// <summary>DV4.1 / findings §6.2 — the poll offset, on disk.
///
/// <para>Measured before it was built: <c>TelegramService</c> keeps its offset in a field
/// (<c>private int _offset;</c>), and that was correct for as long as the offset lived exactly as
/// long as the run that owned the poll loop. The courier is designed to outlive everything, so the
/// same field would mean every restart replays whatever the Bot API still holds — up to a whole day
/// of it — and files every note again.</para>
///
/// <para><b>The ordering rule, which is the whole correctness argument.</b> The offset is written
/// AFTER an update has been handled, never before. Getting updates with <c>offset=N</c> is Telegram's
/// confirmation that everything below N is done with, so writing N+1 before doing the work would
/// discard a note on any crash in between. Writing it after means a kill between receive and
/// acknowledge replays exactly one update — the one in flight — and the replay is made harmless by
/// the second half of §6.2: <see cref="InboxStore.Append"/> refuses to overwrite a note file that
/// already exists, so the note files exactly once. Slow twice beats lost once.</para>
///
/// <para>Written through <see cref="InboxStore.WriteAtomic"/> — temp file plus rename — so a machine
/// that loses power mid-write has either the old offset or the new one, never half a number. An
/// unreadable or absent file reads as 0, which means "everything Telegram still has", which is the
/// safe direction: it replays rather than skips.</para></summary>
public sealed class CourierOffset
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    /// <param name="stateHomeRoot">The machine's state home, or null for the resolved one.</param>
    public CourierOffset(string? stateHomeRoot = null) => _path = CourierHome.OffsetPathFor(stateHomeRoot);

    public string Path_ => _path;

    /// <summary>The first update id not yet handled. 0 on a fresh machine, on an unreadable file, and
    /// on a file somebody truncated — all three mean the same thing to the Bot API, and all three are
    /// answered by replaying rather than skipping.</summary>
    public long Read()
    {
        try
        {
            if (!File.Exists(_path)) return 0;
            var record = JsonSerializer.Deserialize<OffsetRecord>(File.ReadAllText(_path), Json);
            var value = record?.Offset ?? 0;
            return value > 0 ? value : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
    }

    /// <summary>Confirms everything below <paramref name="offset"/>. Call it AFTER the update has
    /// been filed and answered, never before — see the type remarks.</summary>
    public void Write(long offset)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        InboxStore.WriteAtomic(_path, JsonSerializer.Serialize(
            new OffsetRecord(offset, DateTime.UtcNow), Json));
    }

    /// <summary>What a status line says about how far the courier has got.</summary>
    public string Describe()
    {
        var value = Read();
        return value == 0
            ? "offset 0 (nothing acknowledged yet — the next poll takes everything still undelivered)"
            : "offset " + value.ToString(CultureInfo.InvariantCulture);
    }

    /// <param name="Offset">The first update id not yet handled.</param>
    /// <param name="UpdatedUtc">When it last moved. For a person reading the file, not for the loop:
    /// a courier that has been up for a week with a two-day-old stamp has heard nothing for two days,
    /// which is a fact worth being able to see.</param>
    private sealed record OffsetRecord(long Offset, DateTime UpdatedUtc);
}
