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

        /// <summary>K3.1: the run database, which no longer lives beside the events file. The
        /// engine always passes it; the fallback keeps a hand-run <c>mcp-serve</c> (and any older
        /// caller) working against a legacy repo-local store.</summary>
        [CommandOption("--run-db <path>")]
        [Description("K3.1: path to run.db. Defaults to run.db beside the events file (pre-K3.1 layout).")]
        public string? RunDb { get; init; }

        [CommandOption("--session <number>")]
        [Description("SC4.1: conductor session number, stamped on every bg child this server starts.")]
        public int? Session { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var eventsPath = Path.GetFullPath(settings.Events);
        var journalPath = Path.GetFullPath(settings.Journal);

        // F1.3: wire store if run.db exists so conductor_note MCP tool works
        var runDbPath = string.IsNullOrWhiteSpace(settings.RunDb)
            ? Path.Combine(Path.GetDirectoryName(eventsPath) ?? StateHome.ScratchDirName, StateHome.RunDbFileName)
            : Path.GetFullPath(settings.RunDb);
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
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; SyncCancellation.RequestStop(cts); };
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
