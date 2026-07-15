using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Face;

/// <summary>
/// Starts the Face TUI (Go / Bubble Tea — <c>face-go</c>) as a child of the engine so <c>conductor run</c>
/// is ONE command: engine + control plane + UI, one process tree, one terminal. The Face inherits this
/// console — the engine must therefore keep its own console sink off (file logging only) or the two fight
/// over stdout.
/// </summary>
/// <remarks>
/// The Face stays a <i>disposable client</i> (design doc AD-2): it is spawned, never awaited. If it fails
/// to launch or dies, the run continues headless — a UI is never a dependency of the orchestration loop.
/// It is tracked by the <see cref="ProcessSupervisor"/> job object, so it dies with the engine rather than
/// lingering as an orphan holding the terminal.
///
/// The original TypeScript + Ink face (<c>face/</c>, spawned as <c>node dist/cli.js</c>) was retired once
/// <c>face-go</c> reached day-to-day usability; this launcher now spawns the self-contained Go binary
/// directly — no node runtime on PATH required.
/// </remarks>
public static class FaceLauncher
{
    /// <summary>Overrides discovery with an explicit path to the built Face binary.</summary>
    public const string PathEnvVar = "CONDUCTOR_FACE";

    /// <summary>The face-go executable name for this platform.</summary>
    public static string BinaryName =>
        OperatingSystem.IsWindows() ? "conductor-face.exe" : "conductor-face";

    /// <summary>Spawns the Face against <paramref name="baseUrl"/>. Returns null when it could not be
    /// started, having logged why — the caller continues without a UI rather than failing the run.</summary>
    public static FaceHandle? Start(string baseUrl, ILogger logger, ProcessSupervisor? supervisor = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var entry = ResolveEntrypoint();
        if (entry is null)
        {
            logger.LogWarning(
                "face: no built TUI found — run `go build -o bin/{Bin} ./cmd/conductor-face/` in face-go/, or set {Env} to the built binary. Continuing headless.",
                BinaryName, PathEnvVar);
            return null;
        }

        var psi = new ProcessStartInfo(entry)
        {
            UseShellExecute = false,   // inherit this console: the Face needs the real TTY for raw mode + mouse
            CreateNoWindow = false,
        };
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
            // Not fatal: the engine runs fine without a face.
            proc?.Dispose();
            logger.LogWarning(ex, "face: could not start the TUI ({Entry}) — continuing headless", entry);
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

    /// <summary>Finds the built Face binary: explicit env var first, then <c>conductor-face(.exe)</c>
    /// walking up from the running binary — next to the engine (published layout) or under
    /// <c>face-go/bin/</c> (the repo's dev build).</summary>
    public static string? ResolveEntrypoint()
    {
        var fromEnv = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var bin = BinaryName;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir.FullName, bin),                         // published next to the exe
                         Path.Combine(dir.FullName, "face-go", "bin", bin),        // repo dev build
                     })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
