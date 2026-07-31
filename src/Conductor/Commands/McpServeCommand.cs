using System.ComponentModel;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

using EventLog = Conductor.Core.Events.EventLog;

namespace Conductor.Commands;

/// <summary>
/// B11.2 — tab completion: generates shell completion scripts for PowerShell and bash.
/// </summary>
public sealed class McpServeCommand : Command<McpServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--events <path>")]
        [Description("Path to the events.jsonl file.")]
        [DefaultValue(".conductor/events.jsonl")]
        public string Events { get; init; } = ".conductor/events.jsonl";

        [CommandOption("--journal <path>")]
        [Description("Path to the MCP side-journal file.")]
        [DefaultValue(".conductor/mcp-journal.jsonl")]
        public string Journal { get; init; } = ".conductor/mcp-journal.jsonl";

        [CommandOption("--run-id <id>")]
        [Description("Run identifier for event authorship.")]
        [DefaultValue("mcp-standalone")]
        public string RunId { get; init; } = "mcp-standalone";

        [CommandOption("--state-dir <path>")]
        [Description("Plan state directory for bg tools (e.g. .conductor/). Optional.")]
        public string? StateDir { get; init; }

        [CommandOption("--repo <path>")]
        [Description("Repo root for bg_start working directory. Optional.")]
        public string? Repo { get; init; }

        [CommandOption("--session <number>")]
        [Description("SC4.1: conductor session number, stamped on every bg child this server starts.")]
        public int? Session { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var eventsPath = Path.GetFullPath(settings.Events);
        var journalPath = Path.GetFullPath(settings.Journal);

        // F1.3: wire store if run.db exists so conductor_note MCP tool works
        var runDbPath = Path.Combine(Path.GetDirectoryName(eventsPath) ?? ".conductor", "run.db");
        IRunStore? store = null;
        if (File.Exists(runDbPath))
        {
            try
            {
                var sqlite = new SqliteRunStore(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteRunStore>.Instance);
                // W2.2: task/note events now go straight into the run's event log, and Emit stamps
                // whatever run id the store last saw. Unset, that is the empty string — the events
                // would persist under a run nobody reads, i.e. vanish. Stamp it before first use.
                sqlite.SetRunId(settings.RunId);
                store = sqlite;
            }
            catch { /* best-effort — MCP works without store */ }
        }

        var stateDir = settings.StateDir ?? Path.GetDirectoryName(eventsPath);
        var repoPath = settings.Repo ?? (stateDir != null ? Path.GetDirectoryName(stateDir) : null);

        var server = new McpTaskServer(eventsPath, journalPath, settings.RunId, store, stateDir, repoPath, settings.Session);
        server.Init();
        server.FoldJournal();

        using var cts = new CancellationTokenSource();
#pragma warning disable MA0045
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
#pragma warning restore MA0045
        try
        {
            server.RunAsync(Console.In, Console.Out, cts.Token).GetAwaiter().GetResult();
        }
        finally
        {
            store?.Dispose();
        }
        return 0;
    }
}
