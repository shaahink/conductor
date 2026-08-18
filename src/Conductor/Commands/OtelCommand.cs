using System.ComponentModel;
using System.Globalization;
using System.Net.Http;

using Conductor.Core;
using Conductor.Core.Store;
using Conductor.Core.Telemetry;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// KS7.3 — <c>conductor otel</c>. Exports a run's event log to an OTLP endpoint as a trace.
/// </summary>
/// <remarks>
/// Read-only, by construction: it opens the store, folds the log, and posts. Pointing it at a LIVE run
/// is the normal case and cannot disturb it — there is no write path in this command at all.
/// <para><c>--dry-run</c> prints the OTLP body instead of sending it, which is how the mapping is
/// reviewed without standing a collector up, and what the evidence file for this checkpoint quotes.</para>
/// </remarks>
public sealed class OtelCommand : AsyncCommand<OtelCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--endpoint <URL>")]
        [Description("OTLP/HTTP base URL, e.g. http://127.0.0.1:4318. /v1/traces is appended.")]
        [DefaultValue("http://127.0.0.1:4318")]
        public string Endpoint { get; init; } = "http://127.0.0.1:4318";

        [CommandOption("--run <ID>")]
        [Description("Export this run instead of the plan's latest.")]
        public string? Run { get; init; }

        [CommandOption("--service <NAME>")]
        [Description("service.name on the exported resource.")]
        [DefaultValue("conductor")]
        public string Service { get; init; } = "conductor";

        [CommandOption("--dry-run")]
        [Description("Print the OTLP JSON body instead of sending it.")]
        public bool DryRun { get; init; }

        [CommandOption("--out <PATH>")]
        [Description("Also write the OTLP body to this file (implies nothing about sending).")]
        public string? Out { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        if (!File.Exists(plan.RunDbPath))
        {
            AnsiConsole.MarkupLine($"[yellow]no run store at[/] {Markup.Escape(plan.RunDbPath)} — nothing to export.");
            return 1;
        }

        using var store = new SqliteRunStore(plan.RunDbPath, NullLogger<SqliteRunStore>.Instance);
        var runId = settings.Run is { Length: > 0 } r ? r : store.GetLatestRunId(plan.Name);
        if (string.IsNullOrEmpty(runId))
        {
            AnsiConsole.MarkupLine($"[yellow]no run recorded for plan[/] {Markup.Escape(plan.Name)}.");
            return 1;
        }

        var events = store.ReadAllEvents(runId);
        var spans = OtelTrace.Build(events);
        if (spans.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]run {Markup.Escape(runId)} has no events[/] — nothing to export.");
            return 1;
        }

        var version = BuildInfo.Current.Version;
        var turns = spans.Sum(s => s.Events.Count);
        AnsiConsole.MarkupLine(
            $"[grey]run[/] {Markup.Escape(runId)} [grey]->[/] {spans.Count.ToString(CultureInfo.InvariantCulture)} spans, " +
            $"{turns.ToString(CultureInfo.InvariantCulture)} per-turn events, trace {Markup.Escape(spans[0].TraceId)}");

        if (settings.Out is { Length: > 0 } outPath)
        {
            await File.WriteAllTextAsync(outPath, OtlpJson.Request(spans, settings.Service, version)).ConfigureAwait(false);
            AnsiConsole.MarkupLine($"[grey]body written to[/] {Markup.Escape(outPath)}");
        }

        if (settings.DryRun)
        {
            if (settings.Out is null or { Length: 0 })
                Console.WriteLine(OtlpJson.Request(spans, settings.Service, version));
            return 0;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var exporter = new OtlpHttpExporter(http, settings.Endpoint, settings.Service, version);
        OtlpExportResult result;
        try
        {
            result = await exporter.ExportAsync(spans).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // The collector being down is the single most common failure and deserves its own sentence
            // rather than a stack trace: the operator's next move is to start one, not to file a bug.
            AnsiConsole.MarkupLine($"[red]no collector at {Markup.Escape(settings.Endpoint)}[/] — {Markup.Escape(ex.Message)}");
            return 2;
        }
        catch (TaskCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(settings.Endpoint)} did not answer within 30s.[/]");
            return 2;
        }

        if (!result.Ok)
        {
            AnsiConsole.MarkupLine($"[red]export refused:[/] {Markup.Escape(result.Describe())}");
            return 2;
        }

        AnsiConsole.MarkupLine($"[green]exported[/] {Markup.Escape(result.Describe())} [grey]to[/] {Markup.Escape(settings.Endpoint)}");
        return 0;
    }
}
