using System.Diagnostics;

using Conductor.Core;
using Conductor.Core.Fleet;
using Conductor.Core.History;
using Conductor.Core.Store;
using Conductor.Http;

using Microsoft.Extensions.Logging.Abstractions;

using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// KS2.2 — attaching a Face to a run that is over.
///
/// <para>The picker used to end a past run with a note: "read-only history · conductor history &lt;id&gt;".
/// Everything the Face renders for a live run is in that run's <c>run.db</c>, so what was missing was a
/// socket, not data. <see cref="ArchiveControlPlane"/> is the socket; this file is the door to it.</para>
///
/// <para><b>The handshake with the Face.</b> The picker runs INSIDE the Face process, which cannot start
/// a C# HTTP server. So the engine leaves a file path in <c>CONDUCTOR_PICK</c>, the Face writes the
/// chosen run's id there and exits, and this code opens the archive and launches a second Face at its
/// url. A file, not stdout — stdout belongs to the TUI — and not argv, which is world-readable.</para>
///
/// <para><b>No token is ever passed — and the ambient one is TAKEN AWAY.</b> The Face's source answers
/// false to <c>HasWriteToken()</c> only when it holds no token at all, and a child process inherits this
/// one's environment: a <c>CONDUCTOR_TOKEN</c> exported in the shell, or left by an earlier attach,
/// would reach the archive Face and make it offer buttons the archive can only refuse. Passing no token
/// is not the same as removing one, so <see cref="StripWriteCredentials"/> removes it. The plane refuses
/// each POST regardless: two independent layers on purpose — the affordance is courtesy, the refusal is
/// the guarantee.</para>
/// </summary>
public sealed partial class FaceCommand
{
    /// <summary>The variable naming the file the Face writes its archive choice to.</summary>
    internal const string PickEnvVar = "CONDUCTOR_PICK";

    /// <summary>
    /// Opens a finished run read-only. <paramref name="serveOnly"/> holds the plane open and prints its
    /// url instead of launching a Face — the shape a script (or a proof capture) needs.
    /// </summary>
    internal static async Task<int> ArchiveAsync(string selector, bool serveOnly, int? port)
    {
        // Before anything is opened: a port inside the fleet window is refused, not warned about and
        // not quietly moved. `--port 4320` used to be accepted and the archive DID then answer the
        // FleetScan probe — measured, `conductor ps` listed it as a run of its own, which is exactly the
        // lie the port choice exists to prevent. Refusing beats relocating because an operator who named
        // a port and silently got another would be handed a url they did not ask for.
        if (port is { } chosen && ArchiveControlPlane.InsideFleetWindow(chosen))
        {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ArchiveControlPlane.FleetWindowRefusal(chosen))}");
            return 1;
        }

        var view = ArchiveView.Open(StateHome.Root, selector, out var refusal);
        if (view is null)
        {
            // Fails soft and by name: a catalogue row whose store cannot be opened is still LISTED by
            // the picker and the hub, so the refusal has to say which of the two things went wrong
            // rather than pretending the run was never here.
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(refusal)}");
            return 1;
        }

        using var plane = new ArchiveControlPlane(view, NullLogger.Instance,
            port is > 0 ? port.Value : ArchiveControlPlane.FirstPort);
        if (!plane.Start())
        {
            AnsiConsole.MarkupLine("[red]error:[/] no free port for the archive plane.");
            return 1;
        }

        var label = string.IsNullOrWhiteSpace(view.Run.PlanName) ? view.Run.ShortRunId : view.Run.PlanName;
        AnsiConsole.MarkupLine(
            $"[grey]archive[/] [white]{Markup.Escape(label)}[/] [grey]{Markup.Escape(view.Run.ShortRunId)} · {Markup.Escape(view.Status)} · read-only · {Markup.Escape(plane.BaseUrl)}[/]");

        if (!serveOnly) return await AttachReadOnlyAsync(plane.BaseUrl).ConfigureAwait(false);

        AnsiConsole.MarkupLine("[grey]serving until ctrl-c.[/]");
        await WaitForCancelAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>Launches a Face on the archive url with no way to write. Not
    /// <see cref="AttachAsync"/>: that one passes a token it was handed, and "was handed none" is not
    /// the same as "has none" once the child inherits this process's environment.</summary>
    private static async Task<int> AttachReadOnlyAsync(string baseUrl)
    {
        var psi = FaceProcess();
        if (psi is null) return 1;
        psi.ArgumentList.Add("--url");
        psi.ArgumentList.Add(baseUrl);
        StripWriteCredentials(psi.Environment);
        return await LaunchAsync(psi).ConfigureAwait(false);
    }

    /// <summary>
    /// Empties a child's environment of every way to acquire a write credential. Three variables, for
    /// three different ways one arrives: <c>CONDUCTOR_TOKEN</c> is the one a shell or an earlier attach
    /// exports; <c>CONDUCTOR_FLEET</c> carries a token per live run and would also send the Face back
    /// into the picker; <c>CONDUCTOR_PICK</c> is this file's own handoff and must not be answered twice.
    /// <para>Internal rather than private so the guarantee is a test and not a comment — a Face that
    /// inherits a token renders write affordances against a run that cannot take a write.</para>
    /// </summary>
    internal static void StripWriteCredentials(IDictionary<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        env.Remove("CONDUCTOR_TOKEN");
        env.Remove(FaceTarget.FleetEnvVar);
        env.Remove(PickEnvVar);
    }

    /// <summary>Blocks until ctrl-c. The plane is disposed by the caller's <c>using</c> on the way out,
    /// which is what takes the port down.</summary>
    private static async Task WaitForCancelAsync()
    {
        var stop = new TaskCompletionSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        Console.CancelKeyPress += handler;
        try { await stop.Task.ConfigureAwait(false); }
        finally { Console.CancelKeyPress -= handler; }
    }

    /// <summary>Runs the Face with a pick file in hand, then acts on what it wrote. A Face that
    /// attached normally writes nothing and this is exactly <see cref="LaunchAsync"/>.</summary>
    private static async Task<int> LaunchThenMaybeArchiveAsync(ProcessStartInfo psi)
    {
        var pick = Path.Combine(Path.GetTempPath(), $"conductor-pick-{Guid.NewGuid():N}.txt");
        psi.Environment[PickEnvVar] = pick;
        var exit = await LaunchAsync(psi).ConfigureAwait(false);
        var chosen = await ReadPickAsync(pick).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(chosen)
            ? exit
            : await ArchiveAsync(chosen, serveOnly: false, port: null).ConfigureAwait(false);
    }

    /// <summary>Reads and removes the handoff. Best effort throughout: a Face that could not write it
    /// leaves the caller with the ordinary exit code, which is the correct degradation.</summary>
    private static async Task<string?> ReadPickAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = (await File.ReadAllTextAsync(path).ConfigureAwait(false)).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        finally
        {
            BestEffort.Run(() => File.Delete(path));
        }
    }
}
