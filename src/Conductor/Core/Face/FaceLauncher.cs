using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Face;

/// <summary>
/// Starts the Face TUI (TypeScript/Ink) as a child of the engine so <c>conductor run</c> is ONE command:
/// engine + control plane + UI, one process tree, one terminal. The Face inherits this console — the
/// engine must therefore keep its own console sink off (file logging only) or the two fight over stdout.
/// </summary>
/// <remarks>
/// The Face stays a <i>disposable client</i> (design doc AD-2): it is spawned, never awaited. If it fails
/// to launch or dies, the run continues headless — a UI is never a dependency of the orchestration loop.
/// It is tracked by the <see cref="ProcessSupervisor"/> job object, so it dies with the engine rather than
/// lingering as an orphan holding the terminal.
/// </remarks>
public static class FaceLauncher
{
    /// <summary>Overrides discovery with an explicit path to the built Face entrypoint (<c>cli.js</c>).</summary>
    public const string PathEnvVar = "CONDUCTOR_FACE";

    /// <summary>Spawns the Face against <paramref name="baseUrl"/>. Returns null when it could not be
    /// started, having logged why — the caller continues without a UI rather than failing the run.</summary>
    public static FaceHandle? Start(string baseUrl, ILogger logger, ProcessSupervisor? supervisor = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var entry = ResolveEntrypoint();
        if (entry is null)
        {
            logger.LogWarning(
                "face: no built TUI found — run `npm install && npm run build` in face/, or set {Env} to its dist/cli.js. Continuing headless.",
                PathEnvVar);
            return null;
        }

        var psi = new ProcessStartInfo("node")
        {
            UseShellExecute = false,   // inherit this console: the Face needs the real TTY for raw mode + mouse
            CreateNoWindow = false,
        };
        psi.ArgumentList.Add(entry);
        psi.ArgumentList.Add("--url");
        psi.ArgumentList.Add(baseUrl);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null) return null;
            var handle = new FaceHandle(proc, supervisor?.Track(proc, "face:tui"));
            logger.LogInformation("face: TUI started (pid {Pid}) against {Url}", proc.Id, baseUrl);
            return handle;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Almost always "node is not on PATH". Not fatal: the engine runs fine without a face.
            proc?.Dispose();
            logger.LogWarning(ex, "face: could not start the TUI (is node on PATH?) — continuing headless");
            return null;
        }
    }

    /// <summary>Owns the spawned Face: killing it on dispose is what keeps a closed run from leaving a TUI
    /// holding the terminal. Disposal is always safe — the run's outcome is decided by the engine, never here.</summary>
    public sealed class FaceHandle(Process process, IDisposable? supervisorTrack) : IDisposable
    {
        public Process Process { get; } = process;

        public void Dispose()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
            catch (Exception) { /* already gone, or we lost the right to signal it — either way, nothing to do */ }
            supervisorTrack?.Dispose();
            Process.Dispose();
        }
    }

    /// <summary>Finds the built Face entrypoint: explicit env var first, then a <c>face/dist/cli.js</c>
    /// walking up from the running binary (covers both a published layout and an in-repo dev build).</summary>
    public static string? ResolveEntrypoint()
    {
        var fromEnv = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            // Published next to the exe (face/cli.js), or the repo's dev build (face/dist/cli.js).
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir.FullName, "face", "cli.js"),
                         Path.Combine(dir.FullName, "face", "dist", "cli.js"),
                     })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
