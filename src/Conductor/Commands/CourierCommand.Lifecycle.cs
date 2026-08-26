using Conductor.Core.Courier;

using Spectre.Console;

namespace Conductor.Commands;

/// <summary>DV4.2 / findings §6.4 — <c>courier install | uninstall | restart | stop</c>.
///
/// <para>§1.4-B put a daemon on the machine and never said who starts it. These four verbs are that
/// answer, and they are deliberately thin: every decision about WHAT a per-user scheduled task looks
/// like lives in <see cref="CourierTask"/>, where a test can read the XML it produces without the
/// suite registering anything on the machine running it.</para></summary>
public sealed partial class CourierCommand
{
    private static async Task<int> InstallAsync(Settings settings)
    {
        var exe = settings.Exe is { Length: > 0 } named ? named : Environment.ProcessPath;
        if (exe is not { Length: > 0 })
        {
            AnsiConsole.MarkupLine("[red]error:[/] this build cannot tell where its own binary is, so "
                + "the task would have nothing to run. Pass it: [yellow]--exe <path-to-conductor.exe>[/].");
            return 1;
        }

        // A task pointed at dotnet.exe would run the SDK and exit. It is worth one sentence here
        // because `dotnet run -- courier install` is exactly how this verb gets exercised in a rig.
        if (Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[red]error:[/] that would register [yellow]dotnet.exe[/] as the courier. "
                + "Install from conductor.exe, or name the binary with [yellow]--exe <path>[/].");
            return 1;
        }

        var task = new CourierTask(settings.TaskName);
        var made = await task.InstallAsync(exe).ConfigureAwait(false);
        if (!made.Ok)
        {
            AnsiConsole.MarkupLine("[red]error:[/] the scheduled task was not registered — "
                + Markup.Escape(made.Complaint()));
            return 1;
        }

        AnsiConsole.MarkupLine("[green]installed[/] " + Markup.Escape(task.Name)
            + " [dim]→ " + Markup.Escape(exe) + " courier run[/]");
        AnsiConsole.MarkupLine("[dim]starts at your logon · restarts on failure every minute · "
            + "no admin rights, no elevation[/]");

        if (settings.NoStart)
        {
            AnsiConsole.MarkupLine("[dim]not started — it starts at your next logon, or now with "
                + Markup.Escape(RestartCommand(task)) + "[/]");
            return 0;
        }

        var started = await task.StartAsync().ConfigureAwait(false);
        AnsiConsole.MarkupLine(started.Ok
            ? "[green]started[/] [dim]— `conductor courier status` says what it is doing.[/]"
            : "[yellow]registered, but it did not start:[/] " + Markup.Escape(started.Complaint()));
        return started.Ok ? 0 : 1;
    }

    private static async Task<int> UninstallAsync(Settings settings)
    {
        var task = new CourierTask(settings.TaskName);
        var state = await task.StateAsync().ConfigureAwait(false);
        if (!state.Registered)
        {
            AnsiConsole.MarkupLine("[dim]nothing to remove:[/] no scheduled task named "
                + Markup.Escape(task.Name) + ".");
            return 0;
        }

        var gone = await task.UninstallAsync().ConfigureAwait(false);
        if (!gone.Ok)
        {
            AnsiConsole.MarkupLine("[red]error:[/] the task is still there — " + Markup.Escape(gone.Complaint()));
            return 1;
        }

        AnsiConsole.MarkupLine("[green]uninstalled[/] " + Markup.Escape(task.Name)
            + " [dim]— nothing polls for this machine now. Notes sent from now on reach nobody, and "
            + "Telegram keeps them for 24 hours.[/]");
        return 0;
    }

    private static async Task<int> RestartAsync(Settings settings)
    {
        var task = new CourierTask(settings.TaskName);
        var state = await task.StateAsync().ConfigureAwait(false);
        if (!state.Registered)
        {
            AnsiConsole.MarkupLine("[red]error:[/] there is no scheduled task named "
                + Markup.Escape(task.Name) + ". Register it with [yellow]conductor courier install[/].");
            return 1;
        }

        await task.StopAsync().ConfigureAwait(false);
        var started = await task.StartAsync().ConfigureAwait(false);
        if (!started.Ok)
        {
            AnsiConsole.MarkupLine("[red]error:[/] it stopped but did not start again — "
                + Markup.Escape(started.Complaint()));
            return 1;
        }

        AnsiConsole.MarkupLine("[green]restarted[/] " + Markup.Escape(task.Name)
            + " [dim]— it is running this machine's installed engine again.[/]");
        return 0;
    }

    private static async Task<int> StopAsync(Settings settings)
    {
        var task = new CourierTask(settings.TaskName);
        var state = await task.StateAsync().ConfigureAwait(false);
        if (!state.Registered)
        {
            AnsiConsole.MarkupLine("[dim]not installed:[/] no scheduled task named "
                + Markup.Escape(task.Name) + ".");
            return 0;
        }

        await task.StopAsync().ConfigureAwait(false);
        AnsiConsole.MarkupLine("[green]stopped[/] " + Markup.Escape(task.Name)
            + " [dim]— it starts again at your next logon, or now with "
            + Markup.Escape(RestartCommand(task)) + ".[/]");
        return 0;
    }

    /// <summary>The restart command for THIS task — the default verb, or the verb plus the name.
    /// Named rather than described, because §6.4's whole point is that a person told "restart the
    /// courier" with no command reaches for a pid.</summary>
    internal static string RestartCommand(CourierTask task) =>
        CourierTask.IsDefaultName(task.Name)
            ? CourierProtocol.RestartVerb
            : $"{CourierProtocol.RestartVerb} --task-name \"{task.Name}\"";
}
