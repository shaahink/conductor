using System.Diagnostics;

namespace Conductor.Core.Watch;

/// <summary>
/// SF5.1 — hand the brief to a supervisor command on stdin.
///
/// <para>Stdin, not an argument or a temp file, because the supervisor this exists for is a headless
/// model invocation (<c>claude -p "…"</c>), and every such CLI reads its input that way. It also
/// keeps a ~30-line JSON document out of a Windows command line, where quoting it correctly through
/// PowerShell's parser is a whole class of silent corruption this repo has already paid for.</para>
///
/// <para><see cref="ProcessRunner"/> is not reusable here: none of its overloads redirect stdin.</para>
/// </summary>
public static class WatchHook
{
    /// <summary>Run <paramref name="command"/> through the platform shell with
    /// <paramref name="brief"/> on its stdin. Returns exit code and captured output; a hook that
    /// cannot even be started reports -1 with the reason as its output, because a supervisor being
    /// unreachable must be visible, not swallowed.</summary>
    public static async Task<ProcResult> RunAsync(
        string command, string cwd, string brief, TimeSpan timeout, CancellationToken ct = default)
    {
        var shell = ProcessRunner.DefaultShell;
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
        }
        else
        {
            psi.ArgumentList.Add("-c");
        }
        psi.ArgumentList.Add(command);

        var sw = Stopwatch.StartNew();
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return new ProcResult(-1, "", $"hook could not start: {shell}", false, sw.Elapsed);

            await p.StandardInput.WriteAsync(brief.AsMemory(), ct).ConfigureAwait(false);
            p.StandardInput.Close();

            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var timedOut = false;
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            var o = await stdout.ConfigureAwait(false);
            var e = await stderr.ConfigureAwait(false);
            return new ProcResult(timedOut ? -1 : p.ExitCode, o, e, timedOut, sw.Elapsed);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new ProcResult(-1, "", $"hook failed to run: {ex.Message}", false, sw.Elapsed);
        }
    }
}
