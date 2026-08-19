namespace Conductor.Core.Worktrees;

/// <summary>KS4.4 — the clean attempt diff the verdict receives: exactly what one stage attempt did to
/// the tree, written to a file and registered in the evidence taxonomy.</summary>
/// <remarks>
/// <para>The verdict already counts commits and reads gate results, but until now it had no view of the
/// SHAPE of an attempt — and neither did anyone reading the run afterwards. A commit count cannot tell a
/// 400-line delivery from a one-line tracker edit, and the fix brief a red attempt produces has to
/// describe work it cannot see. This artifact is the missing input, and it is the same artifact whether
/// the attempt ran isolated in its own worktree or in the primary tree.</para>
/// <para>Measured from the head the ATTEMPT started at, not the stage's — two attempts on one stage are
/// two diffs, and a second attempt whose diff included the first's work would credit it twice.</para>
/// <para>Written under the state dir rather than <c>.conductor/evidence/</c>: that directory is the
/// AGENT's, force-added into git by hand, and an engine artifact appearing in it would show up in every
/// human evidence sweep as something a session claimed.</para>
/// </remarks>
public static class AttemptDiff
{
    /// <summary>Where attempt diffs live under the state dir.</summary>
    public const string DirName = "attempts";

    /// <summary>Write the diff of everything <paramref name="tree"/> gained since
    /// <paramref name="baseSha"/>. Returns the full path, or null when the attempt changed nothing (an
    /// empty file would register as evidence of work that does not exist).</summary>
    /// <param name="tree">The working tree the attempt ran in — the attempt worktree when isolation is
    /// on, the primary repo otherwise. The distinction is the caller's; the artifact is identical.</param>
    public static string? Write(
        string tree, string stateDir, string stageId, int attempt, int sessionNumber, string baseSha,
        Action<string>? log = null, int maxChars = 200_000)
    {
        if (string.IsNullOrWhiteSpace(baseSha)) return null;
        try
        {
            var body = Render(tree, baseSha, maxChars);
            if (body.Trim().Length == 0) return null;

            var dir = Path.Combine(stateDir, DirName);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, FileName(stageId, attempt, sessionNumber));
            File.WriteAllText(path, body);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An artifact is never allowed to fail a session — see RegisterEvidenceAsync's own rule.
            log?.Invoke($"attempt diff: could not write — {ex.Message}");
            return null;
        }
    }

    /// <summary><c>&lt;stage&gt;-a&lt;attempt&gt;-s&lt;session&gt;.diff</c> — sortable, and it says which
    /// attempt of which stage produced it without opening the file.</summary>
    public static string FileName(string stageId, int attempt, int sessionNumber)
    {
        var slug = new string(stageId.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "stage";
        return $"{slug}-a{attempt}-s{sessionNumber:000}.diff";
    }

    /// <summary>The diff body: tracked changes since <paramref name="baseSha"/>, then every untracked
    /// file as a full addition. Untracked files need the second pass because a brand-new source file is
    /// the commonest artifact of a delivery session and <c>git diff</c> alone is blind to it.</summary>
    public static string Render(string tree, string baseSha, int maxChars = 200_000)
    {
        var parts = new List<string>();
        // The state dir is excluded on BOTH sides. Measured against a live demo run: a repo that TRACKS
        // .conductor/ (the scaffolded default — this repo gitignores it, which is what hid this) put 132
        // lines of the engine's own REPORT.md bookkeeping into the artifact, because that commit lands
        // inside the session's window. An attempt diff carrying the engine's edits is worse than none:
        // it reads as work the agent did.
        var tracked = Git.Exec(tree, "diff", baseSha, "--", ".", ":(exclude)" + Store.StateHome.ScratchDirName);
        if (tracked.ExitCode == 0 && tracked.Output.Trim().Length > 0) parts.Add(tracked.Output.TrimEnd());

        var untracked = Git.Exec(tree, "ls-files", "--others", "--exclude-standard");
        if (untracked.ExitCode == 0)
        {
            var nul = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            foreach (var rel in untracked.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()))
            {
                if (rel.Length == 0 || IsRunState(rel)) continue;
                // --no-index exits 1 when the files differ, which is every case here.
                var d = Git.Exec(tree, "diff", "--no-index", "--", nul, rel);
                if (d.Output.Trim().Length > 0) parts.Add(d.Output.TrimEnd());
            }
        }

        var all = string.Join("\n", parts);
        return all.Length <= maxChars ? all : all[..maxChars] + $"\n... [attempt diff truncated at {maxChars} chars]";
    }

    /// <summary>The run's own state directory, which is never part of what an attempt DID.</summary>
    /// <remarks>Caught by a test, not by reasoning: attempt 2's diff carried attempt 1's diff file,
    /// because the artifact this class writes lands under <c>.conductor/attempts/</c> and is untracked.
    /// In a live repo the state dir is gitignored so <c>--exclude-standard</c> hides it, which is
    /// exactly the kind of accident that holds until someone runs against a tree without the ignore —
    /// a fresh scratch rig, a worktree cut before the ignore existed. Excluded explicitly instead.</remarks>
    private static bool IsRunState(string relPath)
    {
        var p = relPath.Replace('\\', '/');
        return p.StartsWith(Store.StateHome.ScratchDirName + "/", StringComparison.OrdinalIgnoreCase);
    }
}
