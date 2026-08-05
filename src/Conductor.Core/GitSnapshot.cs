using System.Collections.Concurrent;
using System.Globalization;

namespace Conductor.Core;

/// <summary>SF3.3 — one commit as the wire and the Face carry it: the abbreviated sha and the
/// subject line. A count of commits answers "did anything happen"; the subject answers "what",
/// which is the question an operator reading a session actually has.</summary>
public sealed record GitCommit(string Sha, string Subject);

/// <summary>
/// SF3.3 — the repo's git state as one value: branch, upstream, ahead/behind, dirtiness, HEAD and
/// the last few subjects. Every surface that wanted this used to shell out for its own slice of it
/// (or, more often, went without), so "which branch is this run writing to, and is the tree clean?"
/// had no answer inside the tool at all.
/// <para><b>Two process spawns, not six.</b> <c>git status --porcelain --branch</c> answers branch,
/// upstream, ahead, behind AND the dirty set in one call; <c>git log</c> answers HEAD plus the
/// recent subjects in another. That matters because this is folded into <c>GET /state</c>, which the
/// Face polls once a second (<c>messages.go CmdTick</c>) — see <see cref="GitSnapshotCache"/> for
/// the other half of that bargain.</para>
/// </summary>
/// <param name="Branch">The checked-out branch, or "" when HEAD is detached or the repo has no
/// commits yet. Never the literal "HEAD": <see cref="Detached"/> carries that fact instead.</param>
/// <param name="Ahead">Commits on HEAD the upstream does not have, or null when there is NO
/// upstream. Null and 0 are different facts — "this branch was never pushed anywhere" must not
/// render as "in sync" — and every caller here is required to keep them apart.</param>
public sealed record GitSnapshot(
    string Branch,
    bool Detached,
    string? Upstream,
    int? Ahead,
    int? Behind,
    string HeadSha,
    string HeadSubject,
    bool Dirty,
    int DirtyCount,
    string DirtySummary,
    IReadOnlyList<GitCommit> RecentCommits)
{
    /// <summary>What a path that is not a git repo (or a git that would not answer) looks like.
    /// A named value rather than a null return: the Face renders "not a git repo" honestly, and
    /// the alternative — an absent block — reads identically to an older engine.</summary>
    public static readonly GitSnapshot None =
        new("", false, null, null, null, "", "", false, 0, "clean", []);

    /// <summary>How many subjects <see cref="Probe"/> reads. Enough to see a session's worth of
    /// work without turning the Home panel into a git log.</summary>
    public const int RecentCommitCount = 6;

    /// <summary>Reads <paramref name="repo"/> fresh — always shells out. Callers on a polled path
    /// want <see cref="GitSnapshotCache.Get"/>.</summary>
    public static GitSnapshot Probe(string repo)
    {
        if (string.IsNullOrWhiteSpace(repo) || !Directory.Exists(repo)) return None;

        var status = Git.Exec(repo, "status", "--porcelain", "--branch");
        if (status.ExitCode != 0) return None;

        var lines = status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToList();
        var head = lines.Count > 0 && lines[0].StartsWith("##", StringComparison.Ordinal)
            ? ParseBranchLine(lines[0]) : (Branch: "", Detached: false, Upstream: (string?)null, Ahead: (int?)null, Behind: (int?)null);
        var dirtyLines = lines.Where(l => !l.StartsWith("##", StringComparison.Ordinal)).ToList();

        var (headSha, headSubject, recent) = ReadLog(repo);

        return new GitSnapshot(
            Branch: head.Branch,
            Detached: head.Detached,
            Upstream: head.Upstream,
            Ahead: head.Ahead,
            Behind: head.Behind,
            HeadSha: headSha,
            HeadSubject: headSubject,
            Dirty: dirtyLines.Count > 0,
            DirtyCount: dirtyLines.Count,
            DirtySummary: SummariseDirty(dirtyLines),
            RecentCommits: recent);
    }

    /// <summary>Parses porcelain v1's branch header. The shapes it has to survive, all real:
    /// <c>## main</c> (no upstream), <c>## main...origin/main</c>, <c>## main...origin/main [ahead 2]</c>,
    /// <c>## main...origin/main [ahead 2, behind 1]</c>, <c>## HEAD (no branch)</c> (detached), and
    /// <c>## No commits yet on master</c> (a fresh repo, which is the state <c>conductor init</c>
    /// leaves behind and therefore the one most likely to be on screen first).</summary>
    internal static (string Branch, bool Detached, string? Upstream, int? Ahead, int? Behind) ParseBranchLine(string line)
    {
        var s = line.Length > 2 ? line[2..].Trim() : "";
        if (s.StartsWith("HEAD (no branch)", StringComparison.Ordinal)) return ("", true, null, null, null);

        const string noCommits = "No commits yet on ";
        if (s.StartsWith(noCommits, StringComparison.Ordinal))
            return (s[noCommits.Length..].Trim(), false, null, null, null);

        int? ahead = null, behind = null;
        var bracket = s.IndexOf('[');
        if (bracket >= 0 && s.Length > 0 && s[^1] == ']')
        {
            foreach (var part in s[(bracket + 1)..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length != 2 || !int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
                if (t[0].Equals("ahead", StringComparison.Ordinal)) ahead = n;
                else if (t[0].Equals("behind", StringComparison.Ordinal)) behind = n;
            }
            s = s[..bracket].Trim();
        }

        string? upstream = null;
        var sep = s.IndexOf("...", StringComparison.Ordinal);
        if (sep >= 0)
        {
            upstream = s[(sep + 3)..].Trim();
            s = s[..sep].Trim();
            // A tracked branch that is level reports neither counter. That is 0/0, NOT "no upstream":
            // leaving them null here would make an in-sync branch indistinguishable from one that was
            // never pushed, which is the exact confusion this record's doc comment forbids.
            ahead ??= 0;
            behind ??= 0;
        }
        return (s, false, upstream, ahead, behind);
    }

    private static (string Sha, string Subject, IReadOnlyList<GitCommit> Recent) ReadLog(string repo)
    {
        // %x1f = ASCII unit separator: a commit subject may contain anything else, tabs included.
        var r = Git.Exec(repo, "log", $"-{RecentCommitCount}", "--format=%H%x1f%h%x1f%s");
        if (r.ExitCode != 0) return ("", "", []);
        var commits = new List<GitCommit>();
        var fullSha = "";
        var subject = "";
        foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\u001f');
            if (f.Length < 3) continue;
            if (fullSha.Length == 0) { fullSha = f[0]; subject = f[2]; }
            commits.Add(new GitCommit(f[1], f[2]));
        }
        return (fullSha, subject, commits);
    }

    /// <summary>A one-line "what is dirty" — the first few porcelain rows, then a count of the rest.
    /// Bounded on purpose: this rides a polled endpoint, and a repo mid-build can carry thousands of
    /// untracked rows that no status strip could ever show.</summary>
    private static string SummariseDirty(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return "clean";
        var shown = lines.Take(6).Select(l => l.Trim());
        return string.Join(", ", shown) + (lines.Count > 6 ? $" (+{lines.Count - 6} more)" : "");
    }
}

/// <summary>
/// SF3.3 — the TTL in front of <see cref="GitSnapshot.Probe"/>. <c>GET /state</c> is polled once a
/// second by every attached Face and by <c>conductor status</c>; without this, adding git awareness
/// would have meant two process spawns per second per viewer, forever, for a fact that changes at
/// human speed.
/// <para>The clock is a parameter rather than <c>DateTime.UtcNow</c> so the policy itself is
/// testable — the point of a cache is what it does NOT do, and that is unobservable from outside a
/// method that reads the wall clock.</para>
/// </summary>
public static class GitSnapshotCache
{
    /// <summary>How long a probe is reused. Short enough that "I just committed" shows up before
    /// the operator reaches for the terminal to check, long enough that a wall of Faces cannot
    /// turn the run's repo into a git benchmark.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(4);

    private static readonly ConcurrentDictionary<string, (DateTime At, GitSnapshot Snap)> Entries =
        new(StringComparer.Ordinal);

    /// <summary>The cached snapshot for <paramref name="repo"/>, probing only when the last answer
    /// is older than <see cref="Ttl"/>.</summary>
    public static GitSnapshot Get(string repo, Func<string, GitSnapshot> probe, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var key = repo ?? "";
        if (Entries.TryGetValue(key, out var hit) && nowUtc - hit.At < Ttl && nowUtc >= hit.At)
            return hit.Snap;
        var fresh = probe(key);
        Entries[key] = (nowUtc, fresh);
        return fresh;
    }

    /// <summary>The production entry point: cached, real probe, real clock.</summary>
    public static GitSnapshot Get(string repo) => Get(repo, GitSnapshot.Probe, DateTime.UtcNow);

    /// <summary>Drops every cached answer. For tests, and for the case where the engine itself has
    /// just changed the repo and would otherwise serve its own stale reading back.</summary>
    public static void Clear() => Entries.Clear();
}
