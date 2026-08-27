using System.ComponentModel;
using System.Globalization;
using Conductor.Core;
using Conductor.Core.Courier;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Store;
using Conductor.Http;
using Conductor.Models;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>Bug #93 — <c>courier run</c>, in its own file so the verb that outlives every run also
/// leaves a record of itself: see <see cref="CourierLog"/>.</summary>
public sealed partial class CourierCommand
{
    /// <summary>Bug #93: the shell around the daemon that leaves a record. Every exit — a refusal to
    /// start, a clean stop, an exception nothing below caught — is one line in the courier's own log
    /// before the process ends, because the scheduled task that runs this captures nothing and the
    /// machine's Task Scheduler log is off. The exit code is unchanged; what changes is that it can be
    /// explained afterwards.</summary>
    private static async Task<int> RunAsync(Settings settings)
    {
        var journal = CourierLog.At();
        journal.Append($"courier run starting: pid {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}, " +
                       $"engine {EngineStamp.Current.Full}, protocol {CourierProtocol.Version.ToString(CultureInfo.InvariantCulture)}" +
                       (settings.Once ? ", --once" : ""));
        try
        {
            var exit = await RunCoreAsync(settings, journal).ConfigureAwait(false);
            journal.Append($"courier run stopped: exit {exit.ToString(CultureInfo.InvariantCulture)}");
            return exit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            journal.Append($"courier run DIED: {ex.GetType().Name}: {ex.Message}");
            journal.Append(ex.ToString());
            AnsiConsole.MarkupLine("[red]error:[/] the courier died - " + Markup.Escape(ex.Message)
                + " [dim](recorded in " + Markup.Escape(journal.Path) + ")[/]");
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(Settings settings, CourierLog journal)
    {
        var courier = CourierSettings.Load();
        var token = Token();
        if (Blocker(courier, token) is { Length: > 0 } why)
        {
            journal.Append("refused to start: " + why);
            AnsiConsole.MarkupLine("[red]error:[/] the courier will not start — " + Markup.Escape(why));
            return 1;
        }

        // DV4.3, FOUND BY THE LIVE PROOF: there is exactly ONE courier per machine by construction -
        // it owns the token, and the messenger allows one update consumer per token - and until this
        // check existed nothing enforced it. A second `courier run` fought the first for updates AND
        // overwrote its presence record with its own pid, then DELETED the record on its way out, so
        // install.ps1 and every version handshake would have been reading a file that described a
        // process that had already exited. Refused by name, with what is running.
        if (CourierPresence.Live() is { } already)
        {
            journal.Append("refused to start: a courier is already running - " + already.Describe());
            AnsiConsole.MarkupLine("[red]error:[/] a courier is already running on this machine - "
                + Markup.Escape(already.Describe()) + ".");
            AnsiConsole.MarkupLine("[dim]Only one may hold the token. Stop it first: "
                + "`conductor courier stop`, or restart it with `" + CourierProtocol.RestartVerb + "`.[/]");
            return 1;
        }

        // Bug #93: every logger line reaches the courier's own file as well as the console, because
        // under the scheduled task there IS no console and the file is the only record.
        using var factory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .AddProvider(journal.AsProvider()));
        var log = factory.CreateLogger("courier");

        using var source = new TelegramCourierSource(courier, token!, log);
        var daemon = new CourierDaemon(source, courier, stateHomeRoot: null, log: m => log.LogInformation("{Line}", m));

        // Ctrl-C is a STOP, not a kill: the loop finishes the delivery it is on and writes its offset
        // before returning, which is the difference between a clean restart and a replayed note.
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = stopping.CancelAsync(); };

        // DV4.3 / §6.5: the loopback seam, opened before the presence record is written so the
        // record can state the port it actually bound. `--once` never opens it: a one-shot poll has
        // no run to serve, and binding the named port from a rig is how a test starves the real
        // courier of the socket its runs are dialling.
        var secret = CourierSecret.Resolve();
        var presence = CourierPresence.Current(settings.TaskName);
        using var listener = settings.Once
            ? null
            : new CourierListener(() => presence, (push, c) => DeliverAsync(source, push, c), secret, log);

        if (listener is not null)
        {
            if (listener.TryStart(out var refused))
            {
                presence = presence with { Port = listener.Port };
                log.LogInformation("Courier listening on {Url} - runs on this machine push through it",
                    CourierEndpoint.BaseUrl(listener.Port));
            }
            else
            {
                // Not fatal, and that is the point: inbound notes are the half of this daemon that
                // works with no run alive at all, and they do not need a socket.
                log.LogWarning("Courier has no loopback listener: {Why}", refused);
            }
        }

        if (CourierSecret.ProtectionComplaint() is { Length: > 0 } exposed)
            log.LogWarning("Courier secret: {Why}", exposed);

        // DV4.2 / §6.4: what is running, written down where install.ps1 and a version handshake can
        // both read it. Cleared on the way out so the next reader sees the truth and not a pid.
        presence.Write();
        try
        {
            if (settings.Once)
            {
                var tick = await daemon.PollOnceAsync(stopping.Token).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[dim]one poll:[/] {tick.Received.ToString(CultureInfo.InvariantCulture)} received, "
                    + $"{tick.Filed.ToString(CultureInfo.InvariantCulture)} filed, "
                    + $"{tick.Duplicates.ToString(CultureInfo.InvariantCulture)} already filed, "
                    + $"{tick.Parked.ToString(CultureInfo.InvariantCulture)} parked");
                return 0;
            }

            AnsiConsole.MarkupLine("[dim]" + Markup.Escape(RetentionNotice) + "[/]");
            await daemon.RunAsync(stopping.Token).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            CourierPresence.Clear();
        }
    }
}
