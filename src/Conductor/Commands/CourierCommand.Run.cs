using System.Diagnostics;

using Conductor.Core;
using Conductor.Core.Courier;

using Spectre.Console;

namespace Conductor.Commands;

/// <summary>PK1.1 / D1 - <c>courier run</c> is an alias. The daemon is <c>conductor-courier</c>, its own
/// executable beside this one, so a running courier no longer holds the engine's binary open.</summary>
public sealed partial class CourierCommand
{
    /// <summary>Starts <c>conductor-courier</c> with the flags this verb was given and becomes it: the
    /// console is inherited, Ctrl-C reaches the courier directly (it shares this console) and stops it
    /// cleanly, and the exit code is the courier's.
    ///
    /// <para>The child is held in a kill-on-close job, because an alias that can die without its child
    /// is two processes pretending to be one: a task registered before D1 runs <c>conductor courier
    /// run</c>, and <c>schtasks /End</c> ends the process it started - this one - so without the job the
    /// courier would outlive its own task, still holding the token, with the scheduler reporting it
    /// stopped.</para>
    ///
    /// <para>Bug #93: a courier binary that is not there is an exit, and every exit of this verb is a line
    /// in the courier's own log.</para></summary>
    private static async Task<int> RunAsync(Settings settings)
    {
        var exe = CourierBinary.Beside(AppContext.BaseDirectory);
        if (!File.Exists(exe))
        {
            var why = exe + " is not there - the courier is its own executable now, built and published "
                    + "beside the engine. Rebuild, or reinstall with tools/install.ps1.";
            CourierLog.At().Append("courier run refused: " + why);
            AnsiConsole.MarkupLine("[red]error:[/] the courier will not start - " + Markup.Escape(why));
            return 1;
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (settings.Once) start.ArgumentList.Add("--once");
        if (settings.TaskName is { Length: > 0 } name)
        {
            start.ArgumentList.Add("--task-name");
            start.ArgumentList.Add(name);
        }

        // The courier receives the same Ctrl-C and finishes the delivery it is on; this process only
        // has to still be here to hand back its exit code.
        ConsoleCancelEventHandler wait = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += wait;
        using var job = new JobObject();
        try
        {
            using var courier = Process.Start(start)
                ?? throw new InvalidOperationException("the courier process did not start: " + exe);
            job.Assign(courier);
            await courier.WaitForExitAsync().ConfigureAwait(false);
            return courier.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            CourierLog.At().Append("courier run refused: " + exe + " would not start (" + ex.Message + ")");
            AnsiConsole.MarkupLine("[red]error:[/] the courier will not start - " + Markup.Escape(ex.Message));
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= wait;
        }
    }
}