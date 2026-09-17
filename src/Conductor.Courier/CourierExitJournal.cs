using System.Globalization;
using System.Runtime.InteropServices;

using Conductor.Core.Courier;

namespace Conductor.Courier;

/// <summary>PK2.2 / D2(c) - the process's own last words, for the exits <c>Main</c> never returns from.
///
/// <para>F-COUR-1: 22 starts in the log and no stop, exit or exception line, because every path
/// <c>Main</c> journals is a path through <c>Main</c>. These handlers are the ones the runtime calls on
/// the way out regardless: <see cref="AppDomain.ProcessExit"/> (a return, <c>Environment.Exit</c> from
/// anywhere, a graceful runtime shutdown), <see cref="AppDomain.UnhandledException"/> (a throw on a
/// thread nothing awaits, which ends the process without passing any catch), and the console close,
/// logoff and shutdown signals. A <c>TerminateProcess</c> - Task Manager, <c>Stop-Process</c>, the
/// scheduler's End - calls none of them; that is what the death record at the next start is for.</para></summary>
internal sealed class CourierExitJournal
{
    private readonly CourierLog _journal;
    private readonly List<PosixSignalRegistration> _signals = [];
    private int? _returned;

    public CourierExitJournal(CourierLog journal) => _journal = journal;

    /// <summary>The signals the runtime can deliver on this platform; on Windows SIGHUP is the console
    /// closing and SIGTERM a logoff or shutdown.</summary>
    internal static readonly PosixSignal[] Signals = [PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT];

    /// <summary>Registers every handler. Called first thing in <c>Main</c>, so nothing after it can end
    /// the process unseen by a handler that exists.</summary>
    public void Arm()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => OnProcessExit(Environment.ExitCode);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnUnhandled(e.ExceptionObject, e.IsTerminating);
        foreach (var signal in Signals)
        {
            try
            {
                // Not cancelled: the journal line is the point, and the runtime's default handling
                // (ending the process) is what should happen next.
                _signals.Add(PosixSignalRegistration.Create(signal, c => OnSignal(c.Signal)));
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
            {
                _journal.Append("courier exit journal: " + signal + " cannot be watched here - " + ex.Message);
            }
        }
    }

    /// <summary><c>Main</c> is about to return this code - the one exit it journals itself.</summary>
    public void Returned(int exit) => _returned = exit;

    internal string OnProcessExit(int exitCode)
    {
        var line = _returned is { } code
            ? "courier process exit: Main returned " + code.ToString(CultureInfo.InvariantCulture)
            : "courier process exit WITHOUT Main returning: exit code " + exitCode.ToString(CultureInfo.InvariantCulture)
              + " (Environment.Exit or a runtime shutdown)";
        _journal.Append(line);
        return line;
    }

    internal string OnUnhandled(object? exception, bool terminating)
    {
        var line = "courier run DIED (unhandled" + (terminating ? ", terminating" : "") + "): "
                 + (exception is Exception ex ? ex.GetType().Name + ": " + ex.Message : exception?.ToString() ?? "no exception object");
        _journal.Append(line);
        if (exception is Exception full) _journal.Append(full.ToString());
        return line;
    }

    internal string OnSignal(PosixSignal signal)
    {
        var line = "courier received " + signal + " - the process is being asked to end";
        _journal.Append(line);
        return line;
    }
}
