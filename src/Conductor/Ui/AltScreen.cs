using System.Runtime.InteropServices;

namespace Conductor.Ui;

/// <summary>
/// Guards the alternate-screen buffer so the live dashboard behaves like a real terminal app:
/// on <see cref="Enter"/> it switches into the alt-screen buffer (nothing scrolls into the user's
/// scrollback), and on <see cref="Dispose"/> it switches back and restores the cursor — leaving the
/// prompt exactly as it was found.
/// <para>
/// The restore is guaranteed on <b>every</b> exit path: normal (try/finally via <c>using</c>),
/// exception (finally), Ctrl+C / SIGTERM (a <see cref="PosixSignalRegistration"/> safety net), and
/// hard process exit (<see cref="AppDomain.ProcessExit"/>). A missed leave sequence wedges the user's
/// terminal, so restore is idempotent and fires from whichever path reaches it first.
/// </para>
/// <para>
/// When output is redirected (CI, piped, <c>| cat</c>) there is no screen buffer to switch, so the
/// guard degrades to a no-op and the caller falls back to inline rendering.
/// </para>
/// </summary>
public sealed class AltScreen : IDisposable
{
    // VT sequences. \e (U+001B) is the ESC introducer; DEC private modes 1049 (alt buffer, save/
    // restore cursor + clears) and 25 (cursor visibility).
    internal const string EnterAlt = "\e[?1049h";
    internal const string LeaveAlt = "\e[?1049l";
    internal const string HideCursor = "\e[?25l";
    internal const string ShowCursor = "\e[?25h";

    private readonly TextWriter _out;
    private readonly bool _enabled;
    private readonly Lock _gate = new();
    private readonly List<PosixSignalRegistration> _signals = new();
    private readonly EventHandler _onProcessExit;
    private bool _entered;
    private bool _left;

    private AltScreen(TextWriter output, bool enabled)
    {
        _out = output;
        _enabled = enabled;
        _onProcessExit = (_, _) => Leave();
    }

    /// <summary>True when the alt-screen buffer is actually active (an interactive TTY, not redirected).</summary>
    public bool IsActive => _enabled;

    /// <summary>
    /// Creates a guard for the current console. Alt-screen is used only when <paramref name="output"/>
    /// is a real terminal; when it is redirected the guard is inert and the caller renders inline.
    /// </summary>
    public static AltScreen Enter(TextWriter? output = null, bool? enabled = null)
    {
        var target = output ?? Console.Out;
        var active = enabled ?? !IsRedirected();
        var screen = new AltScreen(target, active);
        screen.EnterCore();
        return screen;
    }

    private static bool IsRedirected()
    {
        try { return Console.IsOutputRedirected; }
        catch (IOException) { return true; }
    }

    private void EnterCore()
    {
        if (!_enabled) return;
        lock (_gate)
        {
            if (_entered) return;
            _entered = true;
            _out.Write(EnterAlt);
            _out.Write(HideCursor);
            _out.Flush();
        }
        RegisterSafetyNets();
    }

    private void RegisterSafetyNets()
    {
        AppDomain.CurrentDomain.ProcessExit += _onProcessExit;
        // Restore the terminal even when a signal bypasses our try/finally. We do not cancel the
        // signal — the app's own CancelKeyPress/CT handling still runs; this only un-wedges the screen.
        TryRegisterSignal(PosixSignal.SIGINT);
        TryRegisterSignal(PosixSignal.SIGTERM);
        TryRegisterSignal(PosixSignal.SIGQUIT);
    }

    private void TryRegisterSignal(PosixSignal signal)
    {
        try { _signals.Add(PosixSignalRegistration.Create(signal, _ => Leave())); }
        catch (PlatformNotSupportedException) { /* e.g. SIGQUIT on Windows — the other nets cover it */ }
        catch (ArgumentException) { /* signal not available on this platform */ }
    }

    /// <summary>Leaves the alt-screen buffer and restores the cursor. Idempotent and thread-safe.</summary>
    public void Leave()
    {
        if (!_enabled) return;
        lock (_gate)
        {
            if (_left || !_entered) return;
            _left = true;
            _out.Write(ShowCursor);
            _out.Write(LeaveAlt);
            _out.Flush();
        }
    }

    public void Dispose()
    {
        Leave();
        AppDomain.CurrentDomain.ProcessExit -= _onProcessExit;
        foreach (var reg in _signals) reg.Dispose();
        _signals.Clear();
    }
}
