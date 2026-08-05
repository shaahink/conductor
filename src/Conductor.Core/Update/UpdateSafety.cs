using System.Diagnostics;

namespace Conductor.Core.Update;

/// <summary>
/// SC8.3 — the refusal. Swapping the engine binary while a run is live is the one way this verb can
/// destroy work, and it is not hypothetical: a run spawns fresh <c>conductor</c> processes of its own
/// throughout a session (every <c>task</c> claim, every <c>note</c>, every <c>bg start</c>), so a swap
/// mid-run means the second half of a session is driven by a different engine than the first — with a
/// different database schema, a different prompt battery, and no line anywhere saying so.
///
/// <para>Two independent detectors, because either alone has a blind spot:</para>
/// <list type="number">
///   <item><b>The engine lock</b> (<see cref="EngineLock"/>) names the run in a state directory we
///   were pointed at. It is precise, and it is blind to runs in every other repository.</item>
///   <item><b>The process image</b> — any other live process whose executable IS the file about to be
///   replaced. That catches the run in the other repository, and it catches an attached
///   <c>conductor face</c> or an open <c>conductor status</c> too. Conservative on purpose: the cost
///   of a false refusal is retyping one command, and the cost of a false green is a corrupted run.</item>
/// </list>
///
/// <para>There is deliberately no <c>--force</c>. A lock left behind by a dead engine already reads
/// as free (<see cref="PidLiveness"/> settles a recycled pid), so the honest escape hatch — stop the
/// run — is the only one, and it is always available.</para>
/// </summary>
public static class UpdateSafety
{
    /// <summary>Why the swap must not happen, one sentence each. Empty means go.</summary>
    public static IReadOnlyList<string> Blockers(string binaryPath, IEnumerable<string>? stateDirs = null)
    {
        var blockers = new List<string>();

        foreach (var dir in stateDirs ?? [])
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var holder = EngineLock.Read(dir);
            if (holder is null || !EngineLock.IsLive(holder)) continue;
            blockers.Add(
                $"a run is live in {dir} (engine pid {holder.Pid}) — stop it with `conductor pause` or `conductor kill`, then update");
        }

        foreach (var (pid, path) in OtherProcessesRunning(binaryPath))
            blockers.Add($"another conductor is running from {path} (pid {pid}) — exit it, then update");

        return blockers;
    }

    /// <summary>Live processes other than this one whose main module is <paramref name="binaryPath"/>.
    /// <para>Best effort by construction: <c>MainModule</c> is refused for a process owned by another
    /// user or running elevated, and an unreadable process is reported as NOT a match. That is the
    /// deliberate direction — this detector is one of two, and a hard failure here would make
    /// <c>update</c> unusable on any machine with a protected process lying around.</para></summary>
    public static IReadOnlyList<(int Pid, string Path)> OtherProcessesRunning(string binaryPath)
    {
        var found = new List<(int, string)>();
        if (string.IsNullOrWhiteSpace(binaryPath)) return found;

        var name = Path.GetFileNameWithoutExtension(binaryPath);
        var target = Normalize(binaryPath);
        Process[] candidates;
        try { candidates = Process.GetProcessesByName(name); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return found;
        }

        foreach (var p in candidates)
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                var path = p.MainModule?.FileName;
                if (path is { Length: > 0 } && string.Equals(Normalize(path), target, StringComparison.OrdinalIgnoreCase))
                    found.Add((p.Id, path));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // unreadable process: not a match, by design (see the summary)
            }
            finally { p.Dispose(); }
        }
        return found;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { return path; }
    }
}
