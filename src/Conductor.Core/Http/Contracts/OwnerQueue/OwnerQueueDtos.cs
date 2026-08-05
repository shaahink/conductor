using System.Text.Json.Serialization;
using Conductor.Core;

namespace Conductor.Core.Http;

// SF4.1: the owner queue on the wire (GET /owner/queue) — the same entries `.conductor/OWNER-QUEUE.md`
// renders, so a face and a file can never disagree about what the owner owes.

/// <param name="AgeSeconds">Null when the source carries no timestamp (tracker rows do not). A face
/// must render that as "age unknown" — never as zero, which would read as "just now".</param>
/// <param name="Command">Empty when nothing the owner types clears the entry (a blocked-until wait).</param>
public sealed record OwnerQueueItemDto(
    string Id, string Kind, string Title, string Unblocks, string Command,
    // The context ignores nulls wire-wide, which here would be a trap: a client whose age field is a
    // plain number reads an ABSENT key as 0 — "just now" — for an obligation with no timestamp at
    // all. These two are written explicitly as null so unknown is unmistakable.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SinceUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? AgeSeconds,
    string? Detail);

/// <param name="Count">Zero is a real answer, not an absence: the queue was computed and nothing is
/// owed. The face says so out loud rather than hiding the section.</param>
public sealed record OwnerQueueDto(int Count, string GeneratedUtc, IReadOnlyList<OwnerQueueItemDto> Items)
{
    public static OwnerQueueDto From(IReadOnlyList<OwnerQueueItem> items, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new OwnerQueueDto(
            Count: items.Count,
            GeneratedUtc: nowUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Items: [.. items.Select(i => new OwnerQueueItemDto(
                i.Id, i.Kind, i.Title, i.Unblocks, i.Command,
                i.SinceUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                i.AgeSeconds(nowUtc),
                i.Detail))]);
    }
}
