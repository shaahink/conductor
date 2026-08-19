using System.ComponentModel;

using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Core.Store;

using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// KS8.1 — <c>conductor mcp-observe</c>: the read-only MCP surface, JSON-RPC 2.0 over stdio.
/// Serves this machine's run catalogue as resources (history, per-run status, per-run money) and
/// serves no tools at all — control operations are excluded by design, not by configuration
/// (<c>docs/dev/adr/0007-read-only-mcp-surface.md</c>).
/// </summary>
/// <remarks>
/// Separate verb from <c>mcp-serve</c> on purpose. <c>mcp-serve</c> is the run's own agent surface and
/// it writes; this is what an editor, a dashboard or a second model is given, and the difference
/// between them must be visible in the command line an operator types, not buried in a flag.
/// </remarks>
public sealed class McpObserveCommand : Command<McpObserveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--home <PATH>")]
        [Description("Serve a state home other than this machine's.")]
        public string? Home { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = string.IsNullOrWhiteSpace(settings.Home) ? StateHome.Root : Path.GetFullPath(settings.Home);

        var server = new McpObserveServer(root);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; SyncCancellation.RequestStop(cts); };
        server.RunAsync(Console.In, Console.Out, cts.Token).GetAwaiter().GetResult();
        return 0;
    }
}
