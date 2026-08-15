namespace Conductor.Core.Integrations.Github;

/// <summary>
/// KS9.2 — what this run has already put on that destination, remembered LOCALLY.
///
/// <para><b>Why this exists, measured rather than assumed.</b> KS9.1 made identity a marker in the
/// issue body, matched against a full list of the repository's issues — deliberately not GitHub's
/// search index, which is eventually consistent. On the live rig the LIST turned out to be eventually
/// consistent too: one pass created four issues, a second pass two seconds later listed the
/// repository, saw none of them, and created four more. Two complete copies of one board, from one
/// process, with correct code on both ends. KS9.1's two live passes only looked idempotent because
/// they happened to be minutes apart.</para>
///
/// <para><b>So the authority moves local.</b> The marker stays — it is what a human reads, and it is
/// how a lost map is rebuilt from a repository this run has never seen. But "have I already created
/// this" is answered from a row this process wrote when GitHub answered the create, and a destination
/// whose list is stale can no longer talk the mirror into a duplicate. Which is what D-7 / A16 / ADR
/// 0005 asked for from the start — decide from the fold and the local map, never from what GitHub
/// says — for an entirely different reason.</para>
/// </summary>
public sealed class GithubMap
{
    public const string IssueKind = "issue";
    public const string CommentKind = "comment";

    private readonly Dictionary<string, int> _issues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _comments = new(StringComparer.Ordinal);
    private readonly Action<string, string, int>? _persist;

    /// <summary>A map that remembers nothing across passes — the shape a dry run and the read-only
    /// backfill get, because neither may write to the database it is reading.</summary>
    public static GithubMap Transient() => new(null);

    public GithubMap(Action<string, string, int>? persist) => _persist = persist;

    public void Seed(string key, string kind, int issueNumber)
    {
        if (string.Equals(kind, CommentKind, StringComparison.Ordinal)) _comments.Add(key);
        else _issues[key] = issueNumber;
    }

    /// <summary>The issue this run created for that key, or null when it has created none. Null does
    /// NOT mean "no issue exists" — a repository can carry issues from an earlier run, and those are
    /// found by their marker in the listing.</summary>
    public int? IssueFor(string key) => _issues.TryGetValue(key, out var n) ? n : null;

    public bool CommentPosted(string key) => _comments.Contains(key);

    public void RecordIssue(string key, int issueNumber)
    {
        _issues[key] = issueNumber;
        _persist?.Invoke(key, IssueKind, issueNumber);
    }

    public void RecordComment(string key, int issueNumber)
    {
        _comments.Add(key);
        _persist?.Invoke(key, CommentKind, issueNumber);
    }
}
