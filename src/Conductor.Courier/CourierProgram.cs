using System.Globalization;

using Conductor.Core;
using Conductor.Core.Courier;
using Conductor.Core.Integrations;

using Microsoft.Extensions.Logging;

namespace Conductor.Courier;

/// <summary>PK1.1 / D1 - <c>conductor-courier</c>: the daemon's composition root, moved out of the
/// engine's <c>courier run</c> verb so the process that outlives every run no longer holds the engine's
/// binary open.
///
/// <para>Bug #93 travels with it: every exit - a refusal to start, a clean stop, an exception nothing
/// below caught - is one line in the courier's own log before the process ends, because the scheduled
/// task that runs this captures nothing and the machine's Task Scheduler log is off.</para></summary>
public static class CourierProgram
{
    /// <summary>What the command line asked for. Two flags, the ones <c>conductor courier run</c> has
    /// always passed through.</summary>
    internal sealed record Options(bool Once, string? TaskName);

    internal const string Usage =
        "usage: conductor-courier [--once] [--task-name <NAME>]\n"
      + "  --once              poll once and exit; opens no loopback listener\n"
      + "  --task-name <NAME>  the scheduled task this courier runs under (for its presence record)";

    public static async Task<int> Main(string[] args)
    {
        var journal = CourierLog.At();

        // PK2.2 / D2(c): armed before anything else can end the process.
        var exits = new CourierExitJournal(journal);
        exits.Arm();
        var exit = await StartAsync(args, journal).ConfigureAwait(false);
        exits.Returned(exit);
        return exit;
    }

    private static async Task<int> StartAsync(string[] args, CourierLog journal)
    {
        if (Parse(args, out var error) is not { } options)
        {
            journal.Append("courier run refused its command line: " + error);
            await Console.Error.WriteLineAsync("error: " + error + "\n" + Usage).ConfigureAwait(false);
            return 2;
        }

        journal.Append($"courier run starting: pid {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}, " +
                       $"engine {EngineStamp.Current.Full}, protocol {CourierProtocol.Version.ToString(CultureInfo.InvariantCulture)}" +
                       (options.Once ? ", --once" : ""));
        try
        {
            var exit = await RunAsync(options, journal).ConfigureAwait(false);
            journal.Append($"courier run stopped: exit {exit.ToString(CultureInfo.InvariantCulture)}");
            return exit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            journal.Append($"courier run DIED: {ex.GetType().Name}: {ex.Message}");
            journal.Append(ex.ToString());
            await Console.Error.WriteLineAsync("error: the courier died - " + ex.Message
                + " (recorded in " + journal.Path + ")").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>The flags, or null with the reason. Anything unrecognised is refused rather than
    /// ignored: a task XML that passes a flag this build does not know is a stale registration, and
    /// saying so beats running as if it had not asked.</summary>
    internal static Options? Parse(IReadOnlyList<string> args, out string? error)
    {
        var once = false;
        string? taskName = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--once":
                    once = true;
                    break;
                case "--task-name" when i + 1 < args.Count:
                    taskName = args[++i];
                    break;
                case "--task-name":
                    error = "--task-name needs a value.";
                    return null;
                default:
                    error = $"unknown argument '{args[i]}'.";
                    return null;
            }
        }

        error = null;
        return new Options(once, taskName);
    }

    private static async Task<int> RunAsync(Options options, CourierLog journal)
    {
        var courier = CourierSettings.Load();
        var token = TelegramCourierSource.TokenFromEnvironment();
        if (TelegramCourierSource.StartBlocker(courier, token) is { Length: > 0 } why)
        {
            journal.Append("refused to start: " + why);
            await Console.Error.WriteLineAsync("error: the courier will not start - " + why).ConfigureAwait(false);
            return 1;
        }

        // DV4.3, FOUND BY THE LIVE PROOF: there is exactly ONE courier per machine by construction -
        // it owns the token, and the messenger allows one update consumer per token - and until this
        // check existed nothing enforced it. A second courier fought the first for updates AND
        // overwrote its presence record with its own pid, then DELETED the record on its way out, so
        // install.ps1 and every version handshake would have been reading a file that described a
        // process that had already exited. Refused by name, with what is running.
        if (CourierPresence.Live() is { } already)
        {
            journal.Append("refused to start: a courier is already running - " + already.Describe());
            await Console.Error.WriteLineAsync("error: a courier is already running on this machine - "
                + already.Describe() + ".\nOnly one may hold the token. Stop it first: "
                + "`conductor courier stop`, or restart it with `" + CourierProtocol.RestartVerb + "`.").ConfigureAwait(false);
            return 1;
        }

        // PK2.1 / D2(b): a record that is still here, naming a process that is not, is a courier that
        // died without reaching the finally below that clears it. Said now, before this one overwrites it.
        if (await DeathRecordAsync(stateHomeRoot: null, options.TaskName).ConfigureAwait(false) is { } death)
            journal.Append(death);

        // Bug #93: every logger line reaches the courier's own file as well as the console, because
        // under the scheduled task there IS no console and the file is the only record.
        using var factory = Logging(journal);
        var log = factory.CreateLogger("courier");

        using var source = new TelegramCourierSource(courier, token!, log);
        var presence = CourierPresence.Current(options.TaskName);
        var daemon = new CourierDaemon(source, courier, stateHomeRoot: null, log: m => log.LogInformation("{Line}", m),
            beat: polled => presence = presence.Beat(polled));

        // Ctrl-C is a STOP, not a kill: the loop finishes the delivery it is on and writes its offset
        // before returning, which is the difference between a clean restart and a replayed note.
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = stopping.CancelAsync(); };

        // DV4.3 / §6.5: the loopback seam, opened before the presence record is written so the
        // record can state the port it actually bound. `--once` never opens it: a one-shot poll has
        // no run to serve, and binding the named port from a rig is how a test starves the real
        // courier of the socket its runs are dialling.
        var secret = CourierSecret.Resolve();
        using var listener = options.Once
            ? null
            : new CourierListener(() => presence,
                new CourierDesk(source, courier, stateHomeRoot: null, log: m => log.LogWarning("{Line}", m)),
                secret, log);

        if (listener is not null) presence = Listen(listener, presence, log);

        if (CourierSecret.ProtectionComplaint() is { Length: > 0 } exposed)
            log.LogWarning("Courier secret: {Why}", exposed);

        // DV4.2 / §6.4: what is running, written down where install.ps1 and a version handshake can
        // both read it. Cleared on the way out so the next reader sees the truth and not a pid.
        presence.Write();
        ArmFault(Environment.GetEnvironmentVariable(FaultEnvVar), journal);
        try
        {
            if (options.Once)
            {
                var tick = await daemon.PollOnceAsync(stopping.Token).ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"one poll: {tick.Received.ToString(CultureInfo.InvariantCulture)} received, "
                    + $"{tick.Filed.ToString(CultureInfo.InvariantCulture)} filed, "
                    + $"{tick.Duplicates.ToString(CultureInfo.InvariantCulture)} already filed, "
                    + $"{tick.Parked.ToString(CultureInfo.InvariantCulture)} parked").ConfigureAwait(false);
                return 0;
            }

            await Console.Out.WriteLineAsync(TelegramCourierSource.RetentionNotice).ConfigureAwait(false);
            await daemon.RunAsync(stopping.Token).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            CourierPresence.Clear();
        }
    }

    /// <summary>PK2.2 - the rig's way to make this binary die the way F-COUR-1's courier dies, from
    /// outside its own control flow: <c>exit0:N</c> calls <c>Environment.Exit(0)</c> after N seconds,
    /// <c>throw:N</c> throws on a thread nothing awaits. Never set on the owner's machine; journaled when
    /// armed, so a courier running with it can never pass for one that died by itself.</summary>
    internal const string FaultEnvVar = "CONDUCTOR_COURIER_FAULT";

    internal static (string Kind, int Seconds)? ParseFault(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(':', 2);
        return parts.Length == 2 && parts[0] is "exit0" or "throw"
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? (parts[0], seconds)
            : null;
    }

    private static void ArmFault(string? value, CourierLog journal)
    {
        if (ParseFault(value) is not { } fault)
        {
            if (!string.IsNullOrWhiteSpace(value)) journal.Append($"{FaultEnvVar}='{value}' not understood; no fault armed");
            return;
        }

        journal.Append($"FAULT SEAM ARMED ({FaultEnvVar}): {fault.Kind} after {fault.Seconds.ToString(CultureInfo.InvariantCulture)}s - this death is a rig's, not a finding");
        // A timer callback, not a task: an exception thrown here reaches no await and no catch, which
        // is exactly the exit the unhandled-exception handler exists for.
        _fault = new Timer(_ =>
        {
            if (fault.Kind == "exit0") Environment.Exit(0);
            throw new InvalidOperationException("fault seam: an exception on a thread nothing awaits");
        }, null, TimeSpan.FromSeconds(fault.Seconds), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Held so the armed fault's timer is not collected before it fires.</summary>
    private static Timer? _fault;

    /// <summary>D2(b) - the death record, or null when the last courier cleared its presence on the way
    /// out. The scheduler is asked about the task the DEAD courier named, falling back to this one's:
    /// that is the run whose result says how it ended.</summary>
    /// <param name="tasks">Builds the task to ask, by name. Null for the real scheduler.</param>
    internal static async Task<string?> DeathRecordAsync(string? stateHomeRoot, string? taskName,
        Func<string, CourierTask>? tasks = null)
    {
        if (CourierPresence.Read(stateHomeRoot) is not { } previous) return null;

        var name = previous.TaskName ?? taskName;
        if (name is null) return CourierVitals.DeathRecord(previous, null, null);

        var (run, unread) = await (tasks ?? (n => new CourierTask(n)))(name).LastRunAsync().ConfigureAwait(false);
        return CourierVitals.DeathRecord(previous, name, run, unread);
    }

    private static ILoggerFactory Logging(CourierLog journal) =>
        LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .AddProvider(journal.AsProvider()));

    /// <summary>Opens the loopback seam and returns the presence record with the port it bound.</summary>
    private static CourierPresence Listen(CourierListener listener, CourierPresence presence, ILogger log)
    {
        if (listener.TryStart(out var refused))
        {
            log.LogInformation("Courier listening on {Url} - runs on this machine push through it",
                CourierEndpoint.BaseUrl(listener.Port));
            return presence with { Port = listener.Port };
        }

        // Not fatal, and that is the point: inbound notes are the half of this daemon that works with
        // no run alive at all, and they do not need a socket.
        log.LogWarning("Courier has no loopback listener: {Why}", refused);
        return presence;
    }
}