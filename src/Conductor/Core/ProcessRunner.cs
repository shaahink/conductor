using System.Diagnostics;
using System.Text;

namespace Conductor.Core;

public sealed record ProcResult(int ExitCode, string Output, bool TimedOut, TimeSpan Duration);

public static class ProcessRunner
{
    /// <summary>The default shell for the current OS: <c>powershell</c> on Windows, <c>bash</c> everywhere else.</summary>
    public static string DefaultShell => OperatingSystem.IsWindows() ? "powershell" : "bash";

    /// <summary>Run a process, capture stdout+stderr interleaved, kill the whole tree on timeout/cancel.</summary>
    public static ProcResult Run(string fileName, IEnumerable<string> args, string cwd, TimeSpan timeout, CancellationToken ct = default)
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

        var sb = new StringBuilder();
        var gate = new Lock();
        var sw = Stopwatch.StartNew();
        using var job = new JobObject();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return new ProcResult(-1, $"failed to start '{fileName}': {ex.Message}", false, sw.Elapsed);
        }
        job.Assign(p);
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
        lock (gate) return new ProcResult(exit, sb.ToString(), timedOut, sw.Elapsed);
    }

    /// <summary>Run a command line through a named shell (<c>powershell</c>, <c>bash</c>, <c>sh</c>)
    /// with real exit-code propagation. If the shell executable is unavailable (e.g. bash on
    /// Windows without WSL/msys), the result carries exit code -1 and the error message.</summary>
    public static ProcResult RunShell(string shell, string command, string cwd, TimeSpan timeout, CancellationToken ct = default)
    {
        return shell.ToLowerInvariant() switch
        {
            "powershell" => OperatingSystem.IsWindows()
                ? Run("powershell.exe",
                      new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command + "; exit $LASTEXITCODE" },
                      cwd, timeout, ct)
                : Run("pwsh",
                      new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command + "; exit $LASTEXITCODE" },
                      cwd, timeout, ct),
            "bash" => Run("bash", new[] { "-c", command }, cwd, timeout, ct),
            "sh" => Run("sh", new[] { "-c", command }, cwd, timeout, ct),
            _ => new ProcResult(-1,
                $"unknown shell '{shell}': supported shells are powershell, bash, sh", false, TimeSpan.Zero),
        };
    }

    /// <summary>Run a command line through Windows PowerShell (legacy entry-point; delegates to
    /// <see cref="RunShell"/> for consistency).</summary>
    public static ProcResult RunPowerShell(string command, string cwd, TimeSpan timeout, CancellationToken ct = default)
        => RunShell("powershell", command, cwd, timeout, ct);
}
