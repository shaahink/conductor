namespace Conductor.Core.Fleet;

/// <summary>
/// SF5.4 — the engine says which run it is, in the one place an operating system shows a process to a
/// human: its title.
///
/// <para>Two engines on one machine are two identical <c>conductor</c> entries in a task manager and two
/// identical tabs in a terminal. The owner runs several websites at once, and the moment that matters is
/// the one where a run has to be found and stopped — the moment when picking the wrong one costs a
/// morning. <see cref="FleetScan"/> answers "who is running" from the outside; this answers it from the
/// process itself, for anyone who never types <c>conductor ps</c>.</para>
///
/// <para>Best effort, always: a title is decoration, and a service host, a redirected pipe or a platform
/// that has no such concept must never take a run down over one. On non-Windows the setter writes an
/// escape sequence to stdout, so a redirected stdout is left alone rather than fed control bytes that
/// would end up in a log file.</para>
/// </summary>
public static class ProcessTitle
{
    /// <summary>Longest plan name carried into the title before it is elided — a terminal tab shows
    /// perhaps thirty characters, and the identity (repo, stage, run) must survive the truncation the
    /// window does, so it goes first and the prose goes last.</summary>
    private const int PlanBudget = 40;

    private static string? _original;
    private static bool _captured;

    /// <summary><c>conductor conductor SF5 8cefa5de - Sarban face - the watcher and the surfaces</c>.
    /// Repo, stage and run id lead because they are the identity; the plan name is the part a truncated
    /// title can afford to lose.</summary>
    public static string Compose(string? repo, string? planName, string? runId, string? stageId)
    {
        var parts = new List<string>(5) { "conductor" };

        var leaf = RepoLeaf(repo);
        if (leaf.Length > 0) parts.Add(leaf);
        if (!string.IsNullOrWhiteSpace(stageId)) parts.Add(stageId.Trim());
        if (!string.IsNullOrWhiteSpace(runId)) parts.Add(runId.Length >= 8 ? runId[..8] : runId);

        var head = string.Join(' ', parts);
        var plan = (planName ?? "").Trim();
        if (plan.Length == 0) return head;
        if (plan.Length > PlanBudget) plan = plan[..(PlanBudget - 1)] + "...";
        return head + " - " + plan;
    }

    /// <summary>Sets the title, remembering the one that was there so the shell gets its window back.</summary>
    public static void Set(string? repo, string? planName, string? runId, string? stageId)
        => Apply(Compose(repo, planName, runId, stageId));

    /// <summary>Puts back whatever the title was before the run — otherwise a finished run leaves its
    /// stage id sitting in a terminal tab for the rest of the day, which is worse than no title at all
    /// because it reads as live.</summary>
    public static void Restore()
    {
        if (!_captured) return;
        Apply(_original ?? "");
        _captured = false;
        _original = null;
    }

    private static void Apply(string title)
    {
        try
        {
            // Unix: the setter emits an OSC escape on stdout. Into a pipe or a log file that is noise,
            // so a redirected run keeps its bytes clean. Windows sets it through the console API, which
            // works with redirected output and is exactly where a task manager reads from.
            if (!OperatingSystem.IsWindows() && Console.IsOutputRedirected) return;

            if (!_captured)
            {
                // Only Windows can be ASKED what the title is; elsewhere "" is the honest restore.
                _original = "";
                if (OperatingSystem.IsWindows()) _original = SafeRead();
                _captured = true;
            }
            Console.Title = title;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException
            or ArgumentOutOfRangeException or ArgumentException or UnauthorizedAccessException)
        {
            // No console, no title, no problem.
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? SafeRead()
    {
        try { return Console.Title; }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Trailing directory name of a repo path, in either separator, with a drive root
    /// (<c>C:\</c>) degrading to the drive letter rather than the empty string.</summary>
    internal static string RepoLeaf(string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo)) return "";
        var trimmed = repo.Replace('\\', '/').TrimEnd('/');
        if (trimmed.Length == 0) return "";
        var slash = trimmed.LastIndexOf('/');
        var leaf = slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
        return leaf.Replace(":", "", StringComparison.Ordinal);   // a bare drive root reads as "C", not "C:"
    }
}
