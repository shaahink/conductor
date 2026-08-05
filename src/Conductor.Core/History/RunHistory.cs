using System.Globalization;
using Conductor.Core.Store;

namespace Conductor.Core.History;

/// <summary>
/// K3.2: what this machine remembers. Walks <see cref="StateCatalogue"/> — the index K3.1 built —
/// opens each catalogued database <b>read-only</b> through <see cref="RunArchive"/>, and returns every
/// run it finds, newest activity first.
/// <para>The catalogue is an index, not the truth: an entry whose database has been deleted, or which
/// this engine cannot read, is reported as an unreadable row rather than dropped. "That run is gone"
/// and "that run was never here" are different answers and a history that conflates them is worse
/// than no history.</para>
/// <para>Nothing in this file writes. Not to the databases (the connection forbids it), not to the
/// catalogue (browsing must not restamp <c>lastSeenUtc</c> and reorder the very list it is showing).</para>
/// </summary>
public static class RunHistory
{
    /// <summary>Every run in every catalogued database, newest activity first.</summary>
    /// <param name="root">The state home root; <see cref="StateHome.Root"/> is the caller's default.</param>
    /// <param name="filter">Null lists everything.</param>
    public static IReadOnlyList<RunHistoryRow> List(string root, RunHistoryFilter? filter = null)
    {
        var f = filter ?? RunHistoryFilter.All;
        var rows = new List<RunHistoryRow>();
        foreach (var entry in StateCatalogue.Read(root))
        {
            if (!MatchesEntry(entry, f)) continue;
            var archive = RunArchive.TryOpen(entry.RunDb);
            if (archive is null)
            {
                rows.Add(RunHistoryRow.Unreadable(entry));
                continue;
            }
            foreach (var run in archive.Runs().Where(run => MatchesRun(run, f)))
                rows.Add(new RunHistoryRow(entry.Key, entry.Slug, entry.RunDb, entry.Repo, entry.Plan,
                    run, entry.ImportedFrom, Readable: true));
        }
        return rows
            .OrderByDescending(r => SortKey(r))
            .ThenBy(r => r.Repo, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Resolves a human-typed selector to exactly one run. Accepted, in order: a full run id, a run
    /// id prefix of four characters or more, a catalogue slug, and a repo leaf name. Returns null
    /// when nothing matches; <paramref name="ambiguous"/> is the candidate set when more than one
    /// does — the caller prints them rather than guessing which run the operator meant.
    /// </summary>
    public static RunHistoryRow? Find(
        string root, string selector, out IReadOnlyList<RunHistoryRow> ambiguous, RunHistoryFilter? filter = null)
    {
        ambiguous = [];
        var all = List(root, filter).Where(r => r.Readable).ToList();
        if (string.IsNullOrWhiteSpace(selector)) return null;
        var s = selector.Trim();

        var exact = all.Where(r => string.Equals(r.Run?.RunId, s, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];

        var candidates = all.Where(r => Matches(r, s)).ToList();
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1) ambiguous = candidates;
        return null;
    }

    /// <summary>
    /// Done and total checkpoints for one row. Separate from <see cref="List"/> on purpose: this
    /// folds the run's whole event log, which is cheap for one run and wasteful for forty. The
    /// listing applies its limit first and only counts what it is about to print.
    /// </summary>
    public static (int Done, int Total) CheckpointCounts(RunHistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Run is null) return (0, 0);
        var archive = RunArchive.TryOpen(row.RunDbPath);
        if (archive is null) return (0, 0);
        var checkpoints = archive.Checkpoints(row.Run.RunId);
        return (checkpoints.Count(c => string.Equals(c.Status, "DONE", StringComparison.Ordinal)), checkpoints.Count);
    }

    /// <summary>
    /// Parses the <c>--since</c> forms an operator actually types: a relative window (<c>7d</c>,
    /// <c>2w</c>, <c>3mo</c>, <c>1y</c>) or any date the invariant culture understands. Null when the
    /// text means nothing — the caller reports that rather than silently listing everything.
    /// </summary>
    public static DateTimeOffset? ParseSince(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        var digits = s.TakeWhile(char.IsDigit).Count();
        if (digits > 0 && digits < s.Length
            && int.TryParse(s[..digits], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            // A relative window only when the suffix is a UNIT. "2026-07-01" starts with digits too,
            // and an early return here swallowed every absolute date the operator typed.
            var relative = s[digits..].Trim().ToLowerInvariant() switch
            {
                "d" or "day" or "days" => now.AddDays(-n),
                "w" or "week" or "weeks" => now.AddDays(-7 * n),
                "m" or "mo" or "month" or "months" => now.AddMonths(-n),
                "y" or "year" or "years" => now.AddYears(-n),
                "h" or "hour" or "hours" => now.AddHours(-n),
                _ => (DateTimeOffset?)null,
            };
            if (relative is not null) return relative;
        }
        return ParseUtc(s);
    }

    private static bool Matches(RunHistoryRow r, string s)
    {
        if (r.Run is null) return false;
        if (s.Length >= 4 && r.Run.RunId.StartsWith(s, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(r.Slug, s, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(RepoLabel(r.Repo), s, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Trailing directory name of the repo — the way a human names which run they mean.</summary>
    public static string RepoLabel(string repo)
    {
        if (string.IsNullOrWhiteSpace(repo)) return "";
        var trimmed = repo.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>Parses a stored timestamp. The schema keeps them as text and older engines wrote a
    /// different shape, so this is deliberately permissive and never throws.</summary>
    public static DateTimeOffset? ParseUtc(string? stamp)
        => DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;

    private static DateTimeOffset SortKey(RunHistoryRow r)
        => ParseUtc(r.Run?.LastActivityUtc) ?? ParseUtc(r.Run?.StartedUtc) ?? r.LastSeenFallback;

    private static bool MatchesEntry(StateCatalogueEntry e, RunHistoryFilter f)
    {
        if (f.Repo is { Length: > 0 } repo)
        {
            var wanted = StateHome.NormalizeRepo(repo);
            var label = RepoLabel(repo);
            if (!string.Equals(StateHome.NormalizeRepo(e.Repo), wanted, StringComparison.Ordinal)
                && !string.Equals(RepoLabel(e.Repo), label, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return f.Plan is not { Length: > 0 } plan
            || string.Equals(e.Plan, plan, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRun(ArchivedRun run, RunHistoryFilter f)
    {
        // The catalogue's plan name and the run row's plan name can differ — one database can hold
        // runs of a plan that was later renamed. Match either, so --plan finds both spellings.
        if (f.Plan is { Length: > 0 } plan
            && !string.Equals(run.PlanName, plan, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(run.PlanName))
            return false;
        if (f.Since is not { } since) return true;
        var when = ParseUtc(run.LastActivityUtc) ?? ParseUtc(run.StartedUtc);
        return when is null || when >= since;
    }
}

/// <summary>What to list. Every field is optional; <see cref="All"/> filters nothing.</summary>
public sealed record RunHistoryFilter(string? Repo = null, string? Plan = null, DateTimeOffset? Since = null)
{
    public static readonly RunHistoryFilter All = new();
}

/// <summary>One row of history: where it came from (catalogue) and what it was (archive).</summary>
/// <param name="Run">Null only when <paramref name="Readable"/> is false.</param>
/// <param name="Readable">False when the catalogued database is missing or unreadable. The row still
/// lists — a run whose file is gone is a fact worth showing, not a row to hide.</param>
public sealed record RunHistoryRow(
    string Key, string Slug, string RunDbPath, string Repo, string Plan,
    ArchivedRun? Run, string? ImportedFrom, bool Readable)
{
    /// <summary>Sort fallback for an unreadable row: the catalogue's own last-seen stamp.</summary>
    public DateTimeOffset LastSeenFallback { get; init; }

    public static RunHistoryRow Unreadable(StateCatalogueEntry e) => new(
        e.Key, e.Slug, e.RunDb, e.Repo, e.Plan, null, e.ImportedFrom, Readable: false)
    {
        LastSeenFallback = e.LastSeenUtc,
    };

    /// <summary>Trailing directory name of the repo.</summary>
    public string RepoLabel => RunHistory.RepoLabel(Repo);
}
