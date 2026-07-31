namespace Conductor.Core;

public static class Git
{
    public static ProcResult Exec(string repo, params string[] args)
        => ProcessRunner.Run("git", new[] { "-C", repo }.Concat(args), repo, TimeSpan.FromMinutes(10));

    public static string Head(string repo) => Exec(repo, "rev-parse", "HEAD").Output.Trim();

    public static string Branch(string repo) => Exec(repo, "rev-parse", "--abbrev-ref", "HEAD").Output.Trim();

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

    // ---------------------------------------------------------------- B12.3: isolated worktrees

    /// <summary>Create a git worktree at <paramref name="path"/> on a new branch named
    /// <paramref name="branch"/> based on the current HEAD of <paramref name="repo"/>.</summary>
    public static ProcResult WorktreeAdd(string repo, string path, string branch)
        => Exec(repo, "worktree", "add", "-b", branch, path);

    /// <summary>P2: create a detached git worktree at <paramref name="path"/> pinned to
    /// <paramref name="sha"/> — read-only snapshot of the repo at that commit.</summary>
    public static ProcResult WorktreeAddDetached(string repo, string path, string sha)
        => Exec(repo, "worktree", "add", "--detach", path, sha);

    /// <summary>Remove a git worktree at <paramref name="path"/> (force cleanup even if dirty).</summary>
    public static ProcResult WorktreeRemove(string repo, string path)
        => Exec(repo, "worktree", "remove", path, "--force");

    /// <summary>Merge <paramref name="branch"/> into the current HEAD of <paramref name="repo"/> with
    /// a non-interactive merge commit. Returns the process result; exit 0 = success, non-zero = conflict.</summary>
    public static ProcResult MergeBranch(string repo, string branch)
        => Exec(repo, "merge", "--no-edit", branch);

    /// <summary>Force-delete a local branch.</summary>
    public static ProcResult DeleteBranch(string repo, string branch)
        => Exec(repo, "branch", "-D", branch);

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

    /// <summary>P4: squashes consecutive <c>chore(conductor):</c> commits between
    /// <paramref name="sinceSha"/> and HEAD into one per group using an interactive rebase.
    /// Non-chore commits (<c>feat:</c>, <c>fix:</c>, <c>docs:</c>, etc.) are preserved.
    /// Idempotent — if no consecutive chore commits exist, returns true without touching history.
    /// Returns true on success (including no-op).</summary>
    public static bool SquashChoreCommits(string repo, string sinceSha)
    {
        if (string.IsNullOrWhiteSpace(sinceSha)) return false;

        // 1. Probe: are there consecutive chore(conductor): commits to squash?
        var log = Exec(repo, "log", "--format=%H %s", "--reverse", $"{sinceSha}..HEAD");
        if (log.ExitCode != 0) return false;

        var lines = log.Output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return true;

        var squasheableCount = 0;
        var prevWasChore = false;
        foreach (var line in lines)
        {
            var spaceIdx = line.IndexOf(' ');
            if (spaceIdx < 0) continue;
            var subject = line[(spaceIdx + 1)..];
            var isChore = subject.StartsWith("chore(conductor):", StringComparison.OrdinalIgnoreCase);
            if (isChore && prevWasChore) squasheableCount++;
            prevWasChore = isChore;
        }
        if (squasheableCount == 0) return true;

        // 2. Write a sequence-editor PowerShell script to a temp file.
        var editorPath = Path.Combine(Path.GetTempPath(), $"conductor-sqedit-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(editorPath, string.Join("\r\n",
            "param($TodoFile)",
            "$lines = Get-Content -LiteralPath $TodoFile",
            "$prevWasChore = $false",
            "for ($i = 0; $i -lt $lines.Count; $i++) {",
            "    $line = $lines[$i]",
            "    if ($line -match '^pick \\S+ chore\\(conductor\\):') {",
            "        if ($prevWasChore) {",
            "            $lines[$i] = $line -replace '^pick ', 'fixup '",
            "        }",
            "        $prevWasChore = $true",
            "    } else {",
            "        $prevWasChore = $false",
            "    }",
            "}",
            "Set-Content -LiteralPath $TodoFile -Value $lines"));

        try
        {
            // 3. Set GIT_SEQUENCE_EDITOR to our script, run the interactive rebase.
            var psCmd = string.Join("; ",
                $"$env:GIT_SEQUENCE_EDITOR = 'powershell -NoProfile -File ''{editorPath}'''",
                $"git -C '{repo}' rebase -i '{sinceSha}^' --committer-date-is-author-date 2>&1",
                $"$exitCode = $LASTEXITCODE",
                $"Remove-Item -LiteralPath '{editorPath}' -ErrorAction SilentlyContinue",
                $"exit $exitCode");
            var result = ProcessRunner.RunPowerShell(psCmd, repo, TimeSpan.FromMinutes(5));
            return result.ExitCode == 0;
        }
        catch
        {
            try { File.Delete(editorPath); } catch { /* best-effort */ }
            return false;
        }
    }
}
