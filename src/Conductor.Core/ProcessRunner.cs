using System.Diagnostics;
using System.Text;

namespace Conductor.Core;

public sealed record ProcResult(int ExitCode, string Output, string StdErr, bool TimedOut, TimeSpan Duration);

public static class ProcessRunner
{
    /// <summary>The default shell for the current OS: <c>powershell</c> on Windows, <c>bash</c> everywhere else.</summary>
    public static string DefaultShell => OperatingSystem.IsWindows() ? "powershell" : "bash";

    private static Process CreateProcess(string fileName, IEnumerable<string> args, string cwd, StringBuilder stdout, StringBuilder stderr, Lock gate, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // SC6.2: per-call environment on top of the inherited one — git's identity variables
        // (GIT_AUTHOR_*/GIT_COMMITTER_*) are the only supported way to write a commit with an
        // authorship that is not the ambient config's.
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) stderr.AppendLine(e.Data); };
        return p;
    }

    /// <summary>F2.1: assign the started process to the supervisor's run-level JobObject if available,
    /// else a local JobObject scoped to this call — either way, killed processes never orphan.</summary>
    private static IDisposable Track(Process p, string fileName, ProcessSupervisor? supervisor)
    {
        if (supervisor != null) return supervisor.Track(p, fileName);
        var localJob = new JobObject();
        localJob.Assign(p);
        return localJob;
    }

    /// <summary>Run a process, capture stdout+stderr interleaved, kill the whole tree on timeout/cancel.
    /// When <paramref name="supervisor"/> is provided, the process is assigned to the run-level
    /// JobObject for crash safety and tracked in the PID registry (F2.1).</summary>
    public static ProcResult Run(string fileName, IEnumerable<string> args, string cwd, TimeSpan timeout, CancellationToken ct = default, ProcessSupervisor? supervisor = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var gate = new Lock();
        var sw = Stopwatch.StartNew();

        using var p = CreateProcess(fileName, args, cwd, stdout, stderr, gate, env);

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return new ProcResult(-1, $"failed to start '{fileName}': {ex.Message}", "", false, sw.Elapsed);
        }

        using var tracked = Track(p, fileName, supervisor);

        try
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            var deadline = DateTime.UtcNow + timeout;
            var timedOut = false;
            while (!p.WaitForExit(500))
            {
                if (ct.IsCancellationRequested || DateTime.UtcNow > deadline)
                {
                    timedOut = !ct.IsCancellationRequested;
                    try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    p.WaitForExit(5000);
                    break;
                }
            }
            try { p.WaitForExit(); } catch { /* flush async readers */ }

            int exit;
            try { exit = p.ExitCode; } catch { exit = -1; }
            lock (gate) return new ProcResult(exit, stdout.ToString(), stderr.ToString(), timedOut, sw.Elapsed);
        }
        finally
        {
            tracked.Dispose();
        }
    }

    /// <summary>True-async twin of <see cref="Run"/> — same tracking/timeout/cancel semantics, but
    /// awaits <see cref="Process.WaitForExitAsync(CancellationToken)"/> instead of polling
    /// <c>WaitForExit(500)</c>, so the calling async method's thread-pool thread is freed for the
    /// duration of the run instead of blocked (F-debt: gate battery / advisor spawns previously
    /// blocked the async orchestrator loop for their full multi-minute duration).</summary>
    public static async Task<ProcResult> RunAsync(string fileName, IEnumerable<string> args, string cwd, TimeSpan timeout, CancellationToken ct = default, ProcessSupervisor? supervisor = null)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var gate = new Lock();
        var sw = Stopwatch.StartNew();

        using var p = CreateProcess(fileName, args, cwd, stdout, stderr, gate);

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return new ProcResult(-1, $"failed to start '{fileName}': {ex.Message}", "", false, sw.Elapsed);
        }

        using var tracked = Track(p, fileName, supervisor);

        try
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var timedOut = false;
            try
            {
                await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Distinguish a real cancel from a timeout the same way the sync Run() does.
                timedOut = !ct.IsCancellationRequested;
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
            // Mirrors sync Run()'s trailing WaitForExit() — flush redirected-stream async readers.
            try { await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* best effort */ }

            int exit;
            try { exit = p.ExitCode; } catch { exit = -1; }
            lock (gate) return new ProcResult(exit, stdout.ToString(), stderr.ToString(), timedOut, sw.Elapsed);
        }
        finally
        {
            tracked.Dispose();
        }
    }

    /// <summary>Run a command line through a named shell (<c>powershell</c>, <c>bash</c>, <c>sh</c>)
    /// with real exit-code propagation. If the shell executable is unavailable (e.g. bash on
    /// Windows without WSL/msys), the result carries exit code -1 and the error message.</summary>
    public static ProcResult RunShell(string shell, string command, string cwd, TimeSpan timeout, CancellationToken ct = default, ProcessSupervisor? supervisor = null)
    {
        return shell.ToLowerInvariant() switch
        {
            "powershell" => OperatingSystem.IsWindows()
                ? Run("powershell.exe",
                      new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command + "; exit $LASTEXITCODE" },
                      cwd, timeout, ct, supervisor)
                : Run("pwsh",
                      new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command + "; exit $LASTEXITCODE" },
                      cwd, timeout, ct, supervisor),
            "bash" => Run("bash", new[] { "-c", command }, cwd, timeout, ct, supervisor),
            "sh" => Run("sh", new[] { "-c", command }, cwd, timeout, ct, supervisor),
            _ => new ProcResult(-1,
                $"unknown shell '{shell}': supported shells are powershell, bash, sh", "", false, TimeSpan.Zero),
        };
    }

    /// <summary>Run a command line through Windows PowerShell (legacy entry-point; delegates to
    /// <see cref="RunShell"/> for consistency).</summary>
    public static ProcResult RunPowerShell(string command, string cwd, TimeSpan timeout, CancellationToken ct = default)
        => RunShell("powershell", command, cwd, timeout, ct);

    /// <summary>True-async twin of <see cref="RunShell"/> — see <see cref="RunAsync"/>.</summary>
    public static Task<ProcResult> RunShellAsync(string shell, string command, string cwd, TimeSpan timeout, CancellationToken ct = default, ProcessSupervisor? supervisor = null)
    {
        return shell.ToLowerInvariant() switch
        {
            "powershell" => OperatingSystem.IsWindows()
                ? RunAsync("powershell.exe",
                      new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command + "; exit $LASTEXITCODE" },
                      cwd, timeout, ct, supervisor)
                : RunAsync("pwsh",
                      new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command + "; exit $LASTEXITCODE" },
                      cwd, timeout, ct, supervisor),
            "bash" => RunAsync("bash", new[] { "-c", command }, cwd, timeout, ct, supervisor),
            "sh" => RunAsync("sh", new[] { "-c", command }, cwd, timeout, ct, supervisor),
            _ => Task.FromResult(new ProcResult(-1,
                $"unknown shell '{shell}': supported shells are powershell, bash, sh", "", false, TimeSpan.Zero)),
        };
    }

    /// <summary>True-async twin of <see cref="RunPowerShell"/> — see <see cref="RunAsync"/>.</summary>
    public static Task<ProcResult> RunPowerShellAsync(string command, string cwd, TimeSpan timeout, CancellationToken ct = default)
        => RunShellAsync("powershell", command, cwd, timeout, ct);

    /// <summary>DV2.3, bug #66 — why a process failed, in words, and NEVER the empty string.
    ///
    /// <para>The report push logged <c>report push failed:</c> with nothing after the colon, over and
    /// over, on a run nobody could then diagnose. The cause is that it read <c>Output</c>: git writes
    /// its refusals — a rejected non-fast-forward, an auth failure, "Everything up-to-date" — to
    /// STDERR, so the one stream the message quoted was the one guaranteed to be empty. Preferring
    /// stderr and falling back to stdout is the shape <c>Git.cs</c> already uses; what is new is the
    /// last line: when a process fails with nothing on either stream, the exit code IS the reason and
    /// saying so beats a colon with nothing after it.</para></summary>
    public static string FailureReason(this ProcResult result, int lines = 3)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = string.IsNullOrWhiteSpace(result.StdErr) ? result.Output : result.StdErr;
        // The FIRST lines, not the last. GateRunner.TailOf takes the tail because a build log ends
        // with its error; a refused command is the other shape - it announces the refusal and then
        // explains how to fix it. Measured on the very failure this exists for, `git push` with no
        // remote: line 1 is "fatal: No configured push destination." and the lines after it are
        // instructions, so a tail of three read "git remote add <name> <url> | and then push using
        // the remote name | git push <name>" - every word of the advice and none of the reason.
        var kept = (text ?? "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Take(Math.Max(1, lines))
            .ToList();

        if (kept.Count > 0)
            return string.Join(" | ", kept) + (result.TimedOut ? " (timed out)" : "");

        return result.TimedOut
            ? FormattableString.Invariant($"timed out after {result.Duration.TotalSeconds:0.#}s, with no output on stdout or stderr")
            : FormattableString.Invariant($"exit code {result.ExitCode}, with no output on stdout or stderr");
    }
}
