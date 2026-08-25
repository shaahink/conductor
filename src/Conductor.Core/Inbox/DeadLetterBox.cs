using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Conductor.Core.Inbox;

/// <summary>DV3.4 / findings §6.10 — where a note goes when it cannot go where it belongs.
///
/// <para>The alternative was dropping it, and dropping a message the owner spoke is §1.2 gap 2
/// wearing yet another hat. A parked note keeps everything: the record, the audio, and the sentence
/// saying why it could not be filed — so when the checkout comes back, the note is still there to be
/// moved into it by hand.</para>
///
/// <para>Machine-level, under the state home, because the whole reason it is here is that no
/// project's directory could be found.</para></summary>
public sealed class DeadLetterBox
{
    /// <summary>The directory name under the state home.</summary>
    public const string DirName = "dead-letter";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public DeadLetterBox(string stateHomeRoot)
    {
        ArgumentNullException.ThrowIfNull(stateHomeRoot);
        Dir = Path.Combine(stateHomeRoot, DirName);
    }

    public string Dir { get; }

    /// <summary>Parks one note. Returns the file it was written to, or null if even that failed —
    /// in which case the caller still tells the sender, because the one unacceptable outcome is
    /// silence.</summary>
    /// <param name="why">The sentence that would have been said to the sender, stored beside the
    /// note so a person reading this directory next month knows what happened.</param>
    public string? Park(InboxNote note, string why, string? mediaSourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(note);
        try
        {
            Directory.CreateDirectory(Dir);
            var stamp = note.ReceivedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var id = note.Id.ToString(CultureInfo.InvariantCulture);
            var target = Path.Combine(Dir, stamp + "-" + id + ".json");

            string? mediaName = null;
            if (mediaSourcePath is { Length: > 0 } source && File.Exists(source))
            {
                mediaName = stamp + "-" + id + "-" + Path.GetFileName(source);
                File.Copy(source, Path.Combine(Dir, mediaName), overwrite: true);
            }

            InboxStore.WriteAtomic(target, JsonSerializer.Serialize(new
            {
                note,
                why,
                media = mediaName,
                parkedUtc = DateTime.UtcNow,
            }, Json));
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Everything parked here, newest last. Read by nothing in the engine — this is for a
    /// person, and for the test that proves a note nobody could file still exists.</summary>
    public IReadOnlyList<string> All() =>
        Directory.Exists(Dir) ? [.. Directory.GetFiles(Dir, "*.json").OrderBy(f => f, StringComparer.Ordinal)] : [];
}
