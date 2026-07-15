using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

using Conductor.Core.Face;
using Conductor.Core.Http;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>Attaches a Face TUI to a run that is already going — a second terminal, or a reattach after the
/// Face was closed. The port is read from the run's <c>control-plane.json</c>, so concurrent runs (which
/// auto-scan to different ports) are told apart by their plan, never by a port the user has to remember.</summary>
public sealed class FaceCommand : Command<FaceCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--demo")]
        [Description("Run the TUI against synthetic data — no conductor process needed.")]
        public bool Demo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var entry = FaceLauncher.ResolveEntrypoint();
        if (entry is null)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] no built Face found. Run [yellow]go build -o bin/{FaceLauncher.BinaryName} ./cmd/conductor-face/[/] in [yellow]face-go/[/].");
            return 1;
        }

        string url;
        if (settings.Demo)
        {
            url = "--demo";
        }
        else
        {
            var plan = PlanConfig.Load(settings.ResolvePlanPath());
            var discovery = ControlPlaneServer.DiscoveryPath(plan.StateDir);
            if (!File.Exists(discovery))
            {
                AnsiConsole.MarkupLine($"[red]error:[/] no live run for plan [yellow]{Markup.Escape(plan.Name)}[/] (no {Markup.Escape(discovery)}). Start one with [yellow]conductor run[/].");
                return 1;
            }
            var info = JsonSerializer.Deserialize(File.ReadAllText(discovery), ControlPlaneJsonContext.Default.ControlPlaneInfo);
            if (info is null) { AnsiConsole.MarkupLine("[red]error:[/] control-plane.json is unreadable."); return 1; }
            url = info.BaseUrl;
        }

        var psi = new ProcessStartInfo(entry) { UseShellExecute = false };
        if (settings.Demo) psi.ArgumentList.Add("--demo");
        else { psi.ArgumentList.Add("--url"); psi.ArgumentList.Add(url); }

        using var proc = Process.Start(psi);
        if (proc is null) return 1;
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
