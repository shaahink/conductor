namespace Conductor.Core.Integrations.Github;

/// <summary>
/// DV6.2 — which COLUMN a checkpoint belongs in, decided from the fold's status alone and with no
/// HTTP in sight (the same split <see cref="GithubBoardPlan"/> makes, for the same reason).
///
/// <para><b>Why a preference list rather than a map.</b> A Projects v2 <c>Status</c> field is a
/// single-select whose options are whatever the board's owner typed. GitHub's default template
/// carries exactly three — Todo, In Progress, Done — and conductor has five statuses. A one-to-one
/// map would therefore leave <c>blocked</c> and <c>skipped</c> cards off the board on the board most
/// people have, which hides precisely the cards an operator opens a board to find. So each status
/// names the options it would LIKE, best first, and the first one this board actually offers wins.
/// </para>
///
/// <para><b>Every fallback is announced.</b> A card that lands somewhere other than its first choice
/// produces a note naming both — "no 'Blocked' option on this board, so blocked cards are placed in
/// 'In Progress'" — because a board that quietly shows a blocked checkpoint as in-progress is the
/// same class of lie as a mirror that claims a board it never wrote.</para>
///
/// <para><b>And a status with no home is UNPLACED, never guessed.</b> A board whose Status options
/// are "Now / Next / Later" matches nothing here; those items are still added to the board (they are
/// visible, in whatever the board's default column is) and reported as unplaced with their status
/// named, which is an answer an owner can act on.</para>
/// </summary>
public static class GithubProjectColumns
{
    /// <summary>The single-select field this integration writes. GitHub's own default board calls it
    /// Status and every template since has kept the name; a board that renamed it is reported as a
    /// missing field by name rather than being searched for by shape.</summary>
    public const string StatusField = "Status";

    /// <summary>The options a status would like, best first. Case and surrounding space are ignored
    /// when matching, so "In progress" and "In Progress" are one entry, not two.</summary>
    public static IReadOnlyList<string> Preferences(string? status) => Normalise(status) switch
    {
        "todo" => ["Todo", "To do", "Backlog", "New"],
        "in_progress" => ["In Progress", "Doing", "Started"],
        // Blocked falls back to In Progress rather than to nothing: the work HAS been started and is
        // not done, and a blocked card missing from the board is the one an operator needed to see.
        "blocked" => ["Blocked", "On hold", "Paused", "In Progress"],
        "done" => ["Done", "Complete", "Completed", "Shipped"],
        // Skipped is finished-and-not-done. On a board that has no word for that it lands in Done,
        // which matches what the ISSUE board already did to it (a skipped card is closed there).
        "skipped" => ["Skipped", "Won't do", "Wont do", "Cancelled", "Canceled", "Done"],
        "archived" => ["Retired", "Skipped", "Cancelled", "Done"],
        _ => [],
    };

    /// <summary>
    /// The option this card goes in, chosen from what the board OFFERS.
    /// </summary>
    /// <returns>
    /// <c>Name</c> is the board's own spelling of the winning option, or null when this board offers
    /// none of them. <c>Fallback</c> is true when the winner was not the status's first choice —
    /// the caller turns that into the note that names both.
    /// </returns>
    public static (string? Name, bool Fallback) Resolve(string? status, IEnumerable<string> optionNames)
    {
        ArgumentNullException.ThrowIfNull(optionNames);
        var offered = optionNames.ToList();
        var wanted = Preferences(status);
        for (var i = 0; i < wanted.Count; i++)
        {
            var match = offered.Find(o => string.Equals(Squash(o), Squash(wanted[i]), StringComparison.OrdinalIgnoreCase));
            if (match is not null) return (match, i > 0);
        }
        return (null, false);
    }

    /// <summary>The sentence a fallback produces. Built here so the bar "every fallback is announced"
    /// is asserted against the sentence rather than against a reading of the caller.</summary>
    public static string FallbackNote(string? status, string placedIn)
    {
        var first = Preferences(status);
        return first.Count == 0
            ? $"status '{Normalise(status)}' has no column preference at all, so cards are placed in '{placedIn}'."
            : $"no '{first[0]}' option on this board, so '{Normalise(status)}' cards are placed in '{placedIn}'.";
    }

    /// <summary>The sentence an unplaced card produces — the status, and what the board DID offer,
    /// so the answer is actionable without opening GitHub.</summary>
    public static string UnplacedNote(string? status, IEnumerable<string> optionNames) =>
        $"status '{Normalise(status)}' matches no option on this board " +
        $"(offered: {string.Join(", ", optionNames)}) — the card is on the board with no status set.";

    private static string Normalise(string? status) =>
        string.IsNullOrWhiteSpace(status) ? "(none)" : status.Trim().ToLowerInvariant();

    /// <summary>"In progress", "In Progress" and "in-progress" are one option name, not three.</summary>
    private static string Squash(string name) =>
        new(name.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_').ToArray());
}
