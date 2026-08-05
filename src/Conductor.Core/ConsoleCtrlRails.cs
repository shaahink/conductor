using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Conductor.Core;

/// <summary>
/// W3.3: closing the console window must stop a run, not sever it.
///
/// `conductor run` hooked <c>Console.CancelKeyPress</c>, which fires for Ctrl+C and nothing else.
/// Clicking the window's ✕, logging off, or shutting down delivers CTRL_CLOSE/LOGOFF/SHUTDOWN
/// instead — none of which reach that handler — so the process was terminated mid-session with
/// state unsaved and no resume queued (§7.5 of OPERATING-CONDUCTOR, the accidental-✕ data-loss
/// risk). This wires those events to the same graceful stop, and — critically — BLOCKS inside the
/// OS handler until the run has finished saving. Windows kills the process as soon as the handler
/// returns, so returning early is the same as not handling it at all.
///
/// No-op off Windows, where the equivalent signals (SIGHUP/SIGTERM) already reach .NET's
/// <c>ProcessExit</c>/POSIX signal handling.
/// </summary>
public static class ConsoleCtrlRails
{
    public const int CtrlCEvent = 0;
    public const int CtrlBreakEvent = 1;
    public const int CtrlCloseEvent = 2;
    public const int CtrlLogoffEvent = 5;
    public const int CtrlShutdownEvent = 6;

    /// <summary>Windows allows roughly 5s for a close handler before killing the process anyway
    /// (and less on shutdown). Save inside that, or the save is theatre.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(4);

    private delegate bool HandlerRoutine(int ctrlType);

    // Must outlive the P/Invoke: the OS holds a raw function pointer, and a collected delegate is a
    // hard crash at the worst possible moment.
    private static HandlerRoutine? _pinned;
    private static Action? _gracefulStop;
    private static Func<TimeSpan, bool>? _waitForStop;
    private static Action<string>? _log;
    private static TimeSpan _grace = DefaultGrace;

    /// <summary>
    /// Install the rail. <paramref name="gracefulStop"/> asks the run to stop (the same cancellation
    /// Ctrl+C triggers); <paramref name="waitForStop"/> blocks until it has finished saving, and
    /// returns false if it did not finish inside the grace window.
    /// </summary>
    public static IDisposable Install(
        Action gracefulStop,
        Func<TimeSpan, bool> waitForStop,
        Action<string>? log = null,
        TimeSpan? grace = null)
    {
        _gracefulStop = gracefulStop;
        _waitForStop = waitForStop;
        _log = log;
        _grace = grace ?? DefaultGrace;

        if (OperatingSystem.IsWindows())
        {
            _pinned = Handle;
            try { SetConsoleCtrlHandler(_pinned, add: true); }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                _pinned = null; // no console (service/detached) — nothing to hook
            }
        }
        return new Uninstaller();
    }

    /// <summary>
    /// The handler body, separated from the P/Invoke so it can be exercised directly.
    /// Returns true when this rail owns the event (and has already stopped the run).
    /// </summary>
    public static bool Handle(int ctrlType)
    {
        // Ctrl+C and Ctrl+Break stay with Console.CancelKeyPress — it already cancels cleanly and
        // keeps the process alive to finish its own epilogue. Returning false passes them on.
        if (ctrlType is CtrlCEvent or CtrlBreakEvent) return false;
        if (ctrlType is not (CtrlCloseEvent or CtrlLogoffEvent or CtrlShutdownEvent)) return false;

        var what = ctrlType switch
        {
            CtrlCloseEvent => "console window closed",
            CtrlLogoffEvent => "user logging off",
            _ => "system shutting down",
        };
        _log?.Invoke($"{what} — stopping the run and saving state (up to {_grace.TotalSeconds:0}s)");
        try { _gracefulStop?.Invoke(); } catch (Exception ex) { _log?.Invoke($"graceful stop failed: {ex.Message}"); }

        var saved = true;
        try { saved = _waitForStop?.Invoke(_grace) ?? true; }
        catch (Exception ex) { _log?.Invoke($"waiting for the run to stop failed: {ex.Message}"); }
        _log?.Invoke(saved
            ? "state saved — run `conductor run` again to resume"
            : $"the run did not finish stopping within {_grace.TotalSeconds:0}s — resuming may replay the last session");
        return true;
    }

    private sealed class Uninstaller : IDisposable
    {
        public void Dispose()
        {
            if (OperatingSystem.IsWindows() && _pinned != null)
            {
                try { SetConsoleCtrlHandler(_pinned, add: false); } catch (EntryPointNotFoundException) { }
            }
            _pinned = null;
            _gracefulStop = null;
            _waitForStop = null;
            _log = null;
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
