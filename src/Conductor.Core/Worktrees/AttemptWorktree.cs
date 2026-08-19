using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Core.Worktrees;

/// <summary>KS4.4 — the sidecar that makes an attempt tree identifiable after the run that made it is
/// gone. Written NEXT TO the worktree directory, never inside it: a marker inside would show up as an
/// untracked file in the attempt's own diff and in every gate that walks the tree.</summary>
public sealed record AttemptMarker
{
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("pidStartUtc")] public DateTime PidStartUtc { get; init; }
    [JsonPropertyName("runId")] public string RunId { get; init; } = "";
    [JsonPropertyName("repo")] public string Repo { get; init; } = "";
    [JsonPropertyName("stageId")] public string StageId { get; init; } = "";
    [JsonPropertyName("attempt")] public int Attempt { get; init; }
    [JsonPropertyName("branch")] public string Branch { get; init; } = "";
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; init; }
}

/// <summary>KS4.4 — one stage attempt, isolated in its own git worktree: created from the stage's base
/// commit, dropped whole when the attempt fails, fast-forwarded into the primary tree when it goes
/// green, and able to hand the verdict a clean diff of exactly what the attempt did.</summary>
/// <remarks>
/// <para>The value is rollback that is mechanical rather than remembered. Without this, a failed attempt
/// leaves its half-finished edits in the tree the NEXT attempt starts from, and every later gate result
/// is measured against a mixture of two sessions' work. Dropping a directory is a rollback that cannot
/// be got subtly wrong.</para>
/// <para>The second value is evidence. <see cref="AttemptDiff"/> is the attempt and nothing else —
/// diffed from the exact commit the tree was cut at, so bookkeeping commits the engine made on the
/// primary tree meanwhile cannot leak into it.</para>
/// <para>Merge is <b>fast-forward only</b>. A non-ff merge here would mean the primary tree moved under
/// the attempt, so the tree the gates went green on is not the tree that would result — precisely the
/// state where a merge commit launders an unverified combination into the branch.</para>
/// </remarks>
public sealed class AttemptWorktree
{
    /// <summary>Branch- and directory-name prefix that marks a tree as conductor's to reap. The sweep
    /// keys off this and nothing else — a worktree a human made is never touched.</summary>
    public const string Prefix = "conductor-attempt-";

    public required string Repo { get; init; }
    public required string Path { get; init; }
    public required string Branch { get; init; }
    /// <summary>The commit the tree was cut at — the left side of every diff this attempt produces.</summary>
    public required string BaseSha { get; init; }
    public required string StageId { get; init; }
    public required int Attempt { get; init; }

    private string MarkerPath => Path + ".attempt.json";

    private static readonly JsonSerializerOptions MarkerJson = new() { WriteIndented = true };

    /// <summary>Cut a fresh worktree for <paramref name="stageId"/> attempt <paramref name="attempt"/> at
    /// the primary tree's current HEAD. Returns null and logs when git refuses.</summary>
    /// <param name="root">Where attempt trees live. Defaults to the temp directory, matching the lane
    /// runner — deliberately OUTSIDE the repo so a build inside an attempt cannot be swept up by a gate
    /// or an ignore rule aimed at the primary tree.</param>
    public static AttemptWorktree? Create(
        string repo, string stageId, int attempt, string runId,
        string? root = null, Action<string>? log = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var slug = Slug(stageId);
        var name = $"{Prefix}{slug}-{attempt}-{suffix}";
        var path = System.IO.Path.Combine(root ?? System.IO.Path.GetTempPath(), name);
        var baseSha = Git.Head(repo);

        var created = Git.WorktreeAdd(repo, path, name);
        if (created.ExitCode != 0)
        {
            log?.Invoke($"attempt worktree: git refused to create {path} — {created.Output.Trim()}{created.StdErr.Trim()}");
            return null;
        }

        var wt = new AttemptWorktree
        {
            Repo = repo, Path = path, Branch = name, BaseSha = baseSha,
            StageId = stageId, Attempt = attempt,
        };
        wt.WriteMarker(runId);
        log?.Invoke($"attempt worktree: stage {stageId} attempt {attempt} isolated at {path} (branch {name}, base {baseSha[..Math.Min(8, baseSha.Length)]})");
        return wt;
    }

    private void WriteMarker(string runId)
    {
        try
        {
            var self = System.Diagnostics.Process.GetCurrentProcess();
            var marker = new AttemptMarker
            {
                Pid = Environment.ProcessId,
                PidStartUtc = self.StartTime.ToUniversalTime(),
                RunId = runId, Repo = Repo, StageId = StageId, Attempt = Attempt,
                Branch = Branch, CreatedUtc = DateTime.UtcNow,
            };
#pragma warning disable MA0045 // one small sidecar written at attempt creation; async buys nothing and the caller is sync
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker, MarkerJson));
#pragma warning restore MA0045
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing marker only costs the sweep its owner check; it must never fail the attempt.
        }
    }

    /// <summary>Read the sidecar next to <paramref name="worktreePath"/>, or null when there is none —
    /// which is what a tree made before this landed, or by hand, looks like.</summary>
    public static AttemptMarker? ReadMarker(string worktreePath)
    {
        var path = worktreePath + ".attempt.json";
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<AttemptMarker>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    /// <summary>The unified diff of everything this attempt did — committed or not, new files included —
    /// measured from <see cref="BaseSha"/>. This is what the verdict's evidence set carries.</summary>
    /// <remarks>Two commands, because one does not cover it: <c>diff &lt;base&gt;</c> spans committed and
    /// uncommitted work but is blind to a file git has never been told about, and a brand-new source file
    /// is the commonest single artifact of a delivery session. Untracked files are added with
    /// <c>--no-index</c> against the null device so they appear as full additions.</remarks>
    public string AttemptDiff(int maxChars = 200_000)
    {
        var parts = new List<string>();
        var tracked = Git.Exec(Path, "diff", BaseSha);
        if (tracked.ExitCode == 0 && tracked.Output.Trim().Length > 0) parts.Add(tracked.Output.TrimEnd());

        foreach (var rel in UntrackedFiles())
        {
            var d = Git.Exec(Path, "diff", "--no-index", "--", NullDevice, rel);
            // --no-index exits 1 when the files differ, which is the normal case here.
            if (d.Output.Trim().Length > 0) parts.Add(d.Output.TrimEnd());
        }

        var all = string.Join("\n", parts);
        return all.Length <= maxChars ? all : all[..maxChars] + $"\n... [attempt diff truncated at {maxChars} chars]";
    }

    private static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    private List<string> UntrackedFiles()
    {
        var r = Git.Exec(Path, "ls-files", "--others", "--exclude-standard");
        if (r.ExitCode != 0) return new List<string>();
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>Repo-relative paths the attempt touched, for the diff-scoped gate classes.</summary>
    public List<string> ChangedFiles() => Git.ChangedFiles(Path, BaseSha);

    /// <summary>True when the attempt produced at least one commit of its own.</summary>
    public bool HasCommits() => Git.Exec(Path, "rev-list", "--count", $"{BaseSha}..HEAD") is { ExitCode: 0 } r
                                && int.TryParse(r.Output.Trim(), out var n) && n > 0;

    /// <summary>Fast-forward the primary tree onto this attempt's branch. Non-zero exit means the base
    /// moved and the caller must NOT force it — the gates went green on a tree that no longer exists.</summary>
    public ProcResult MergeIntoPrimary() => Git.MergeFastForwardOnly(Repo, Branch);

    /// <summary>Drop the tree and, only if it costs nothing, the branch. See
    /// <see cref="WorktreeDrop.DropAttempt"/> — an unmerged branch is KEPT and named.</summary>
    public WorktreeDropResult Drop(Action<string>? log = null)
    {
        var result = WorktreeDrop.DropAttempt(Repo, Path, Branch, log);
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { /* sidecar; harmless */ }
        return result;
    }

    private static string Slug(string stageId)
    {
        var cleaned = new string(stageId.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
        cleaned = cleaned.Trim('-');
        return cleaned.Length == 0 ? "stage" : cleaned[..Math.Min(24, cleaned.Length)];
    }
}
