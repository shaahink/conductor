using System.Globalization;

namespace Conductor.Core.Store;

/// <summary>
/// KS9.2 — the live mirror's high-water mark. Two methods, because a reconciler needs exactly two
/// things from the store: how far GitHub has been told, and a way to record that it has been told
/// further. Everything else it needs is the event log it already reads.
/// </summary>
public sealed partial class SqliteRunStore
{
    public GithubCursorRow ReadGithubCursor(string runId, string repo)
    {
        var rows = Query(
            "SELECT seq, passes, last_error FROM github_cursor WHERE run_id = @runId AND repo = @repo",
            ("@runId", runId), ("@repo", repo));
        if (rows.Count == 0) return GithubCursorRow.Start;
        var row = rows[0];
        return new GithubCursorRow(
            Seq: Convert.ToInt64(row["seq"] ?? 0L, CultureInfo.InvariantCulture),
            Passes: (int)Convert.ToInt64(row["passes"] ?? 0L, CultureInfo.InvariantCulture),
            LastError: row["last_error"] as string);
    }

    /// <summary>Move the mark. Called ONLY after a batch has been pushed without errors — the write
    /// order is the whole guarantee, so there is deliberately no overload that takes a seq and an
    /// error together.</summary>
    public bool WriteGithubCursor(string runId, string repo, long seq, string? lastError) =>
        TryExecute(
            """
            INSERT INTO github_cursor (run_id, repo, seq, updated_utc, passes, last_error)
            VALUES (@runId, @repo, @seq, @now, 1, @err)
            ON CONFLICT(run_id, repo) DO UPDATE SET
                seq = @seq,
                updated_utc = @now,
                passes = passes + 1,
                last_error = @err
            """,
            ("@runId", runId), ("@repo", repo), ("@seq", seq),
            ("@now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
            ("@err", lastError));

    /// <summary>Everything this run has already created on that destination. The authority on "have I
    /// already made this" — GitHub's own issue list is a read replica and answers "no" for seconds
    /// after a create, which is how one process put two complete copies of a board on a repository.</summary>
    public IReadOnlyList<GithubMapRow> ReadGithubMap(string runId, string repo)
    {
        var rows = Query(
            "SELECT key, kind, issue_number FROM github_map WHERE run_id = @runId AND repo = @repo",
            ("@runId", runId), ("@repo", repo));
        return [.. rows.Select(r => new GithubMapRow(
            (string)r["key"]!, (string)r["kind"]!,
            (int)Convert.ToInt64(r["issue_number"] ?? 0L, CultureInfo.InvariantCulture)))];
    }

    /// <summary>Record one. Written the moment GitHub answers a create, BEFORE anything else in the
    /// pass — a crash between the create and this row is the one gap the marker in the body exists to
    /// close, and it costs a rebuild rather than a duplicate.</summary>
    public bool WriteGithubMapEntry(string runId, string repo, string key, string kind, int issueNumber) =>
        TryExecute(
            """
            INSERT INTO github_map (run_id, repo, key, kind, issue_number, created_utc)
            VALUES (@runId, @repo, @key, @kind, @number, @now)
            ON CONFLICT(run_id, repo, kind, key) DO UPDATE SET issue_number = @number
            """,
            ("@runId", runId), ("@repo", repo), ("@key", key), ("@kind", kind), ("@number", issueNumber),
            ("@now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

    /// <summary>Record a FAILED pass without moving the mark. The mark and the error are written by
    /// different methods because they mean opposite things: one says "GitHub knows this much", the
    /// other says "the last attempt to tell it did not land".</summary>
    public bool RecordGithubSyncError(string runId, string repo, string error)
    {
        var current = ReadGithubCursor(runId, repo);
        return WriteGithubCursor(runId, repo, current.Seq, error);
    }
}

/// <summary>One <c>github_map</c> row: a thing this run has already put on that destination.</summary>
public readonly record struct GithubMapRow(string Key, string Kind, int IssueNumber);

/// <summary>One <c>github_cursor</c> row. <see cref="Seq"/> is the event seq GitHub has been told
/// through; zero means "nothing yet", which is also what a replay asks for.</summary>
public readonly record struct GithubCursorRow(long Seq, int Passes, string? LastError)
{
    public static GithubCursorRow Start => new(0L, 0, null);
}
