namespace Conductor.Core;

public static partial class Git
{
    public static ProcResult Exec(string repo, params string[] args)
        => ProcessRunner.Run("git", new[] { "-C", repo }.Concat(args), repo, TimeSpan.FromMinutes(10));

    public static string Head(string repo) => Exec(repo, "rev-parse", "HEAD").Output.Trim();

    public static string Branch(string repo) => Exec(repo, "rev-parse", "--abbrev-ref", "HEAD").Output.Trim();

    /// <summary>KS4.3 — every repo-relative path that differs between <paramref name="baseRev"/> and
    /// the WORKING TREE, untracked files included.</summary>
    /// <remarks>
    /// <para>Three commands, not one, and each covers a hole the others leave. <c>diff --name-only
    /// &lt;rev&gt;</c> compares the rev to the working tree, so with a branch name it already spans
    /// "committed on this branch" and "edited and not yet committed" together. It does not see a file
    /// git has never been told about, which is exactly the shape of a brand-new source file a session
    /// just wrote — so <c>ls-files --others</c> is unioned in. And the staged-only case
    /// (<c>git add</c> then nothing else) is covered by <c>diff --cached</c>, because the first
    /// command against a <em>rev</em> sees it but against <c>HEAD</c> the index is not the tree.</para>
    /// <para>An unresolvable base rev returns EMPTY rather than "everything". A caller that scopes a
    /// measurement by this set must treat empty as "I could not tell what changed" and fail closed;
    /// returning the whole repository instead would silently turn a diff-scoped gate into a
    /// whole-repository one and blow its budget rather than its verdict, which is the harder failure
    /// to notice.</para>
    /// </remarks>
    public static List<string> ChangedFiles(string repo, string baseRev)
    {
        if (string.IsNullOrWhiteSpace(baseRev)) return new List<string>();
        if (Exec(repo, "rev-parse", "--verify", "--quiet", baseRev + "^{commit}").ExitCode != 0)
            return new List<string>();
        var paths = new List<string>();
        foreach (var args in new[]
        {
            new[] { "diff", "--name-only", "--diff-filter=d", baseRev },
            new[] { "diff", "--name-only", "--diff-filter=d", "--cached", baseRev },
            new[] { "ls-files", "--others", "--exclude-standard" },
        })
        {
            var r = Exec(repo, args);
            if (r.ExitCode != 0) continue;
            paths.AddRange(r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().Replace('\\', '/')));
        }
        return paths.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> CommitsSince(string repo, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return new List<string>();
        var r = Exec(repo, "log", "--oneline", $"{sha}..HEAD");
        if (r.ExitCode != 0) return new List<string>();
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
    }

    // ---------------------------------------------------------------- SC4.2: conductor's own commits

    /// <summary>SC4.2: the subject prefix <c>Reporter.WriteAndPublish</c> stamps on conductor's own
    /// REPORT.md bookkeeping commits, and the string <see cref="SquashChoreCommits"/> already keys
    /// off. One constant so the writer, the squash and the verdict can never drift apart.</summary>
    public const string BookkeepingSubjectPrefix = "chore(conductor):";

    /// <summary>SC4.2: true when a <c>git log --oneline</c> line is one of conductor's OWN
    /// bookkeeping commits. These carry a status transition and nothing else, so counting them as
    /// the agent's work let a session that delivered nothing score green — devcontext #14 caught
    /// session #2 reading <c>commits 3</c> of which only one was the agent's.</summary>
    public static bool IsBookkeepingCommit(string onelineOrSubject)
        => SubjectOf(onelineOrSubject).StartsWith(BookkeepingSubjectPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="commits"/> with conductor's own bookkeeping commits removed.</summary>
    public static List<string> ExcludeBookkeeping(IEnumerable<string> commits)
        => commits.Where(c => !IsBookkeepingCommit(c)).ToList();

    /// <summary>Strips the leading abbreviated sha from a <c>--oneline</c> row. A bare subject with
    /// no sha is passed through: the leading token only counts as a sha when it is all hex AND at
    /// least git's 7-character minimum, so an English first word is never mistaken for one.</summary>
    private static string SubjectOf(string oneline)
    {
        var s = oneline.Trim();
        var sp = s.IndexOf(' ');
        if (sp >= 7 && s[..sp].All(Uri.IsHexDigit)) return s[(sp + 1)..].TrimStart();
        return s;
    }

    public static bool IsDirty(string repo)
        => Exec(repo, "status", "--porcelain").Output.Trim().Length > 0;

    public static string DirtySummary(string repo)
    {
        var lines = Exec(repo, "status", "--porcelain").Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return "clean";
        var shown = lines.Take(8).Select(l => l.Trim());
        return string.Join(", ", shown) + (lines.Length > 8 ? $" (+{lines.Length - 8} more)" : "");
    }

    /// <summary>Returns (ahead, behind) counts vs the upstream tracking branch, or null if the branch has
    /// no upstream configured (e.g. a detached HEAD, a local-only branch, or no remote).</summary>
    public static (int Ahead, int Behind)? AheadBehind(string repo)
    {
        var r = Exec(repo, "rev-list", "--left-right", "--count", "@{upstream}...HEAD");
        if (r.ExitCode != 0) return null;
        var parts = r.Output.Trim().Split('\t');
        if (parts.Length == 2 && int.TryParse(parts[0], out var behind) && int.TryParse(parts[1], out var ahead))
            return (ahead, behind);
        return null;
    }

    // ---------------------------------------------------------------- P4: squash bookkeeping

    /// <summary>Returns the UTC timestamp of the most recent git commit touching any tracked file
    /// in the repo. Returns null if git fails (e.g. no commits yet). Used by skipIfFresh gate caching.</summary>
    public static DateTime? MostRecentCommitTime(string repo)
    {
        var r = Exec(repo, "log", "-1", "--format=%at", ".");
        if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Output)) return null;
        if (long.TryParse(r.Output.Trim(), out var unixSeconds))
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        return null;
    }

    /// <summary>SC4.3: the newest moment this repo's SOURCE changed — the most recent commit, or a
    /// newer UNCOMMITTED edit. <see cref="MostRecentCommitTime"/> alone is the wrong clock for a
    /// freshness cache: mid-session the agent's work is uncommitted by definition, so a build output
    /// left over from before the edits still dated "newer than the last commit" and every skipIfFresh
    /// gate skipped over exactly the changes it existed to check.</summary>
    /// <param name="excludeRelPath">Repo-relative path to ignore while scanning the dirty set — the
    /// freshness artifact itself, which is frequently untracked and would otherwise date itself.</param>
    public static DateTime? MostRecentChangeTime(string repo, string? excludeRelPath = null)
    {
        var commit = MostRecentCommitTime(repo);
        var dirty = NewestDirtyChangeTime(repo, excludeRelPath);
        if (commit is null) return dirty;
        if (dirty is null) return commit;
        return dirty > commit ? dirty : commit;
    }

    /// <summary>Newest last-write time across the paths <c>git status --porcelain</c> reports as
    /// changed (staged, unstaged or untracked), or null when the tree is clean. Best-effort and
    /// bounded: a huge dirty set is sampled rather than fully stat'ed.</summary>
    public static DateTime? NewestDirtyChangeTime(string repo, string? excludeRelPath = null, int maxPaths = 500)
    {
        var r = Exec(repo, "status", "--porcelain");
        if (r.ExitCode != 0) return null;
        var exclude = string.IsNullOrWhiteSpace(excludeRelPath)
            ? null
            : excludeRelPath.Replace('\\', '/').Trim('/');
        DateTime? newest = null;
        var seen = 0;
        foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen >= maxPaths) break;
            var rel = PorcelainPath(line);
            if (rel.Length == 0) continue;
            if (exclude != null &&
                (rel.Equals(exclude, StringComparison.OrdinalIgnoreCase) ||
                 rel.StartsWith(exclude + "/", StringComparison.OrdinalIgnoreCase)))
                continue;
            seen++;
            var full = Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar));
            DateTime? t = File.Exists(full) ? File.GetLastWriteTimeUtc(full)
                : Directory.Exists(full) ? Directory.GetLastWriteTimeUtc(full)
                : null; // deleted — the deletion itself is not timestamped; the commit clock covers it
            if (t is { } when_ && (newest is null || when_ > newest)) newest = when_;
        }
        return newest;
    }

    /// <summary>The path out of one <c>git status --porcelain</c> row: two status columns, a space,
    /// then the path. A rename row carries <c>old -&gt; new</c> (the new side is the live file), and a
    /// path with unusual characters arrives C-quoted.</summary>
    private static string PorcelainPath(string line)
    {
        var s = line.TrimEnd('\r');
        if (s.Length < 4) return "";
        var rest = s[3..].Trim();
        var arrow = rest.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow >= 0) rest = rest[(arrow + 4)..];
        rest = rest.Trim();
        if (rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"')
            rest = rest[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return rest.Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>SC6.2: is a rebase half-finished in <paramref name="repo"/>? Returns the state
    /// directory git left behind (<c>rebase-merge</c> or <c>rebase-apply</c>), or null. While one
    /// exists HEAD is detached at a partially replayed commit, so no history rewrite may proceed.</summary>
    public static string? RebaseStateDir(string repo)
    {
        foreach (var name in new[] { "rebase-merge", "rebase-apply" })
        {
            var r = Exec(repo, "rev-parse", "--git-path", name);
            if (r.ExitCode != 0) continue;
            var p = r.Output.Trim();
            if (p.Length == 0) continue;
            var full = Path.IsPathRooted(p) ? p : Path.Combine(repo, p);
            if (Directory.Exists(full)) return full;
        }
        return null;
    }

    // ---------------------------------------------------------------- SC6.2: the honest squash

    public enum SquashStatus
    {
        /// <summary>History was rewritten: at least one group of consecutive chore commits collapsed.</summary>
        Squashed,
        /// <summary>Nothing to do — no two consecutive chore commits in the range. History untouched.</summary>
        NothingToSquash,
        /// <summary>Declined on purpose (no start head, a merge in the range, HEAD not where expected).
        /// Not an error, but not a success either: the caller must leave the stage retryable.</summary>
        Refused,
        /// <summary>A git command failed. <see cref="SquashResult.ExitCode"/> and
        /// <see cref="SquashResult.StdErr"/> carry git's own words.</summary>
        Failed,
    }

    /// <summary>SC6.2: what the squash actually did, in numbers a log line can quote. devcontext #20
    /// caught the old <c>bool</c>: it reported success for a no-op, and threw away git's reason on
    /// failure, so four of six stage closes failed silently.</summary>
    /// <param name="Trace">Every process this squash launched, as <c>program args -&gt; exit</c>.
    /// Recorded so the portability claim is measurable rather than asserted: the squash launches
    /// nothing but <c>git</c>, on any OS.</param>
    public sealed record SquashResult(
        SquashStatus Status,
        string Message,
        int CommitsBefore = 0,
        int CommitsAfter = 0,
        int Collapsed = 0,
        int Groups = 0,
        int ExitCode = 0,
        string StdErr = "",
        bool AbortedRebase = false,
        IReadOnlyList<string>? Trace = null)
    {
        /// <summary>True when history is in the state the caller asked for — rewritten, or already
        /// clean. Only then may a stage be marked squashed.</summary>
        public bool Ok => Status is SquashStatus.Squashed or SquashStatus.NothingToSquash;

        public IReadOnlyList<string> Commands => Trace ?? Array.Empty<string>();
    }

    /// <summary>SC6.2: collapse each run of consecutive <c>chore(conductor):</c> commits between
    /// <paramref name="sinceSha"/> and HEAD into one, keeping the first commit's message and the
    /// last one's tree — exactly what an interactive rebase's <c>fixup</c> produces.
    ///
    /// <para>It does NOT rebase. The chain is rebuilt with <c>commit-tree</c> from the trees that
    /// already exist and the branch is moved with a compare-and-swap <c>update-ref</c>, so nothing is
    /// ever checked out. Three defects fall out of that: it works on a dirty tree (the engine rewrites
    /// the tracker after the agent commits it, so the tree is never clean at a stage close and git
    /// refused the rebase outright — devcontext #20); it cannot leave a half-finished rebase behind;
    /// and it launches nothing but <c>git</c>, where the rebase it replaces was a Windows-only
    /// PowerShell sequence-editor script with unescaped path interpolation.</para>
    ///
    /// <para>Diffs are never replayed, only trees reused, so a conflict is not reachable.</para></summary>
    public static SquashResult SquashChoreCommits(string repo, string sinceSha)
    {
        var trace = new List<string>();
        var msgFiles = new List<string>();

        ProcResult Run(params string[] args)
        {
            var r = Exec(repo, args);
            trace.Add($"git {string.Join(' ', args)} -> {r.ExitCode}");
            return r;
        }
        ProcResult RunEnv(IReadOnlyDictionary<string, string> env, params string[] args)
        {
            var r = ProcessRunner.Run("git", new[] { "-C", repo }.Concat(args), repo,
                TimeSpan.FromMinutes(10), env: env);
            trace.Add($"git {string.Join(' ', args)} -> {r.ExitCode}");
            return r;
        }
        SquashResult Refuse(string why) => new(SquashStatus.Refused, why, Trace: trace);
        SquashResult Fail(string why, ProcResult r) => new(SquashStatus.Failed, why,
            ExitCode: r.ExitCode, StdErr: FirstLines(string.IsNullOrWhiteSpace(r.StdErr) ? r.Output : r.StdErr, 4),
            Trace: trace);

        try
        {
            if (string.IsNullOrWhiteSpace(sinceSha)) return Refuse("no start-head recorded for the stage");

            // A half-started rebase (a crashed predecessor, or the PowerShell rebase this replaced)
            // detaches HEAD onto a partially replayed commit. Rewriting from there would move the
            // wrong ref, so it is aborted first — the recovery devcontext #20 never had.
            var aborted = false;
            if (RebaseStateDir(repo) is { } stuck)
            {
                // DV2.4, bug #67: this abort is a destructive command aimed at state THIS process did
                // not create. Ask what it would do before running it — see Git.Rebase.cs for the run
                // where it silently rewound a branch by 28 commits.
                if (StaleRebaseReason(repo, stuck) is { } stale)
                    return Refuse($"a stale rebase state was found and left untouched: {stale}. Clear it by " +
                                  "hand in a checkout you have inspected before this stage can squash");
                var abort = Run("rebase", "--abort");
                if (abort.ExitCode != 0 || RebaseStateDir(repo) != null)
                    return Fail($"a half-finished rebase ({Path.GetFileName(stuck)}) could not be aborted", abort);
                // And what the abort DID: the stage's own start head must still be reachable, or the
                // range this method is about no longer exists. Without this, a rewind reads downstream
                // as the innocuous "nothing to squash" and the stage advances over lost commits.
                if (Exec(repo, "merge-base", "--is-ancestor", sinceSha, "HEAD").ExitCode != 0)
                    return Fail($"aborting the half-finished rebase rewound HEAD past the stage's start head " +
                                $"({Short(sinceSha)}) — refusing to rewrite history on a branch that just lost commits", abort);
                aborted = true;
            }

            var headResult = Run("rev-parse", "HEAD");
            if (headResult.ExitCode != 0) return Fail("git could not resolve HEAD", headResult);
            var oldHead = headResult.Output.Trim();

            const char FS = '', RS = '';   // ASCII unit/record separators: git bodies never contain them
            var log = Run("log", "--reverse",
                $"--format=%H{FS}%T{FS}%P{FS}%an{FS}%ae{FS}%aI{FS}%cn{FS}%ce{FS}%cI{FS}%B{RS}",
                $"{sinceSha}..HEAD");
            if (log.ExitCode != 0) return Fail($"git could not read the range {Short(sinceSha)}..HEAD", log);

            var commits = ParseCommits(log.Output, FS, RS);
            if (commits.Count == 0)
                return new SquashResult(SquashStatus.NothingToSquash, "nothing to squash — no commits in the range", Trace: trace);
            if (commits.Any(c => c.Parents.Length > 1))
                return Refuse("the range contains a merge commit — refusing to linearise history");
            if (!string.Equals(commits[^1].Sha, oldHead, StringComparison.Ordinal))
                return Refuse($"the range does not end at HEAD ({Short(oldHead)}) — refusing to move it");

            // Group runs of consecutive chore commits. A group of one is carried through untouched.
            var groups = new List<List<CommitRow>>();
            foreach (var c in commits)
            {
                if (c.IsChore && groups.Count > 0 && groups[^1][^1].IsChore) groups[^1].Add(c);
                else groups.Add(new List<CommitRow> { c });
            }

            var collapsed = commits.Count - groups.Count;
            var foldedGroups = groups.Count(g => g.Count > 1);
            var foldedInputs = groups.Where(g => g.Count > 1).Sum(g => g.Count);
            if (collapsed == 0)
                return new SquashResult(SquashStatus.NothingToSquash,
                    $"nothing to squash — no consecutive {BookkeepingSubjectPrefix} commits among {commits.Count} commit(s)",
                    CommitsBefore: commits.Count, CommitsAfter: commits.Count, AbortedRebase: aborted, Trace: trace);

            // Everything BELOW the first fold keeps its original sha: only the tail is rebuilt, so a
            // squash rewrites the least history it possibly can.
            var firstFold = groups.FindIndex(g => g.Count > 1);
            var parent = groups[firstFold][0].Parents.FirstOrDefault();

            for (var i = firstFold; i < groups.Count; i++)
            {
                var g = groups[i];
                var first = g[0];   // the surviving message and authorship — fixup semantics
                var last = g[^1];   // the surviving tree, and when the collapsed work landed
                var msgFile = Path.Combine(Path.GetTempPath(), $"conductor-squash-{Guid.NewGuid():N}.msg");
                File.WriteAllText(msgFile, first.Body);
                msgFiles.Add(msgFile);

                var args = new List<string> { "-c", "commit.gpgsign=false", "commit-tree", last.Tree };
                if (!string.IsNullOrEmpty(parent)) { args.Add("-p"); args.Add(parent!); }
                args.Add("-F"); args.Add(msgFile);

                var env = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GIT_AUTHOR_NAME"] = first.AuthorName,
                    ["GIT_AUTHOR_EMAIL"] = first.AuthorEmail,
                    ["GIT_AUTHOR_DATE"] = first.AuthorDate,
                    ["GIT_COMMITTER_NAME"] = last.CommitterName,
                    ["GIT_COMMITTER_EMAIL"] = last.CommitterEmail,
                    ["GIT_COMMITTER_DATE"] = last.CommitterDate,
                };
                var built = RunEnv(env, args.ToArray());
                if (built.ExitCode != 0 || built.Output.Trim().Length == 0)
                    return Fail($"git commit-tree failed rebuilding commit {i - firstFold + 1} of {groups.Count - firstFold}", built);
                parent = built.Output.Trim();
            }

            // ORIG_HEAD first: a human who dislikes the result has the pre-squash tip by name, the
            // same way a real rebase would have left it.
            Run("update-ref", "-m", "conductor: before chore(conductor) squash", "ORIG_HEAD", oldHead);
            // Compare-and-swap: an agent session commits concurrently with the engine, and a squash
            // that moved the branch past a commit that landed in the window would delete it.
            var moved = Run("update-ref",
                "-m", $"conductor: squashed {foldedInputs} {BookkeepingSubjectPrefix} commits into {foldedGroups}",
                "HEAD", parent!, oldHead);
            if (moved.ExitCode != 0)
                return Fail("git update-ref refused to move the branch (HEAD moved under the squash?)", moved);

            return new SquashResult(SquashStatus.Squashed,
                $"squashed {foldedInputs} {BookkeepingSubjectPrefix} commits into {foldedGroups} " +
                $"({commits.Count} commits -> {groups.Count})",
                CommitsBefore: commits.Count, CommitsAfter: groups.Count, Collapsed: collapsed,
                Groups: foldedGroups, AbortedRebase: aborted, Trace: trace);
        }
        catch (Exception ex)
        {
            return new SquashResult(SquashStatus.Failed, $"squash threw: {ex.Message}", ExitCode: -1,
                StdErr: ex.GetType().Name, Trace: trace);
        }
        finally
        {
            foreach (var f in msgFiles) { try { File.Delete(f); } catch { /* best-effort */ } }
        }
    }

    /// <summary>One commit as the squash needs it: identity, both trees' worth of metadata, and the
    /// raw body (never the subject alone — trailers are the repo's convention and must survive).</summary>
    private sealed record CommitRow(
        string Sha, string Tree, string[] Parents,
        string AuthorName, string AuthorEmail, string AuthorDate,
        string CommitterName, string CommitterEmail, string CommitterDate,
        string Body)
    {
        public string Subject => Body.Split('\n')[0].Trim();
        public bool IsChore => IsBookkeepingCommit(Subject);
    }

    private static List<CommitRow> ParseCommits(string output, char fs, char rs)
    {
        var rows = new List<CommitRow>();
        foreach (var raw in output.Split(rs, StringSplitOptions.RemoveEmptyEntries))
        {
            // git separates records with a newline of its own; the body inside a record keeps every
            // newline it had, so only the leading one is stripped.
            var entry = raw.TrimStart('\n', '\r');
            if (entry.Length == 0) continue;
            var f = entry.Split(fs);
            if (f.Length < 10) continue;
            rows.Add(new CommitRow(f[0], f[1],
                f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                f[3], f[4], f[5], f[6], f[7], f[8], f[9]));
        }
        return rows;
    }

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : sha.Length >= 7 ? sha[..7] : sha;

    /// <summary>git's reason, trimmed to something a log line can carry whole.</summary>
    private static string FirstLines(string text, int max)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).Take(max).ToList();
        return string.Join(" | ", lines);
    }
}
