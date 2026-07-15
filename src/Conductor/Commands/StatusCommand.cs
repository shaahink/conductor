using System.ComponentModel;
using System.Diagnostics;

using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// M5.6 — one verdict, from the database, under a second. Reads <c>run.db</c> (the event log) via
/// <see cref="StatusReportBuilder"/> and prints "where are we, how did it go, what hurt". It never reads
/// <c>state.json</c> or the hand-edited tracker markdown. The optional <c>--deep</c> flag adds an LLM
/// narrative on top (the slow path); the default answer is pure DB and fast.
/// </summary>
public sealed class StatusCommand : Command<StatusCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--since <DATETIME>")]
        [Description("With --deep: only analyse the delta since the given UTC datetime (ISO 8601).")]
        public string? Since { get; init; }

        [CommandOption("--deep")]
        [Description("Add an LLM narrative on top of the fast database verdict (slower, opt-in).")]
        public bool Deep { get; init; }
    }

    private const string InvocationsFile = "status-invocations.jsonl";

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (!File.Exists(runDbPath))
        {
            AnsiConsole.MarkupLine("[yellow]No run.db yet.[/] Run [bold]conductor run[/] at least once to record a run.");
            return 0;
        }

        var sw = Stopwatch.StartNew();
        StatusReport report;
        using (var store = new SqliteRunStore(runDbPath, NullLogger<SqliteRunStore>.Instance))
        {
            report = StatusReportBuilder.Build(plan, store);
        }
        sw.Stop();

        RenderReport(report, sw.Elapsed);

        if (settings.Deep)
            return RunDeepAnalysis(plan, report, settings);
        return 0;
    }

    private static void RenderReport(StatusReport r, TimeSpan elapsed)
    {
        var color = r.Kind switch
        {
            "ok" => "green",
            "active" => "yellow",
            "attention" => "red",
            "interrupted" => "orange1",
            "norun" => "grey",
            _ => "aqua",
        };

        AnsiConsole.MarkupLine($"[bold aqua]Conductor[/] — [bold]{Markup.Escape(r.PlanName)}[/] · [{color}]{Markup.Escape(r.Verdict)}[/]");
        AnsiConsole.MarkupLine(
            $"checkpoints [bold]{r.DoneCount}/{r.TotalCount}[/] · " +
            $"sessions {r.SessionCount} · cost [bold]${r.TotalCostUsd:0.00}[/]" +
            (r.OverheadCostUsd > 0 ? $" (+${r.OverheadCostUsd:0.00} overhead)" : "") +
            $" · [grey]{elapsed.TotalMilliseconds:0}ms from run.db[/]");

        if (r.WhatHurt != null)
            AnsiConsole.MarkupLine($"[red]what hurt:[/] {Markup.Escape(r.WhatHurt)}");

        if (r.Stages.Count > 0)
        {
            AnsiConsole.WriteLine();
            var t = new Table().Border(TableBorder.Rounded);
            t.AddColumn("Stage"); t.AddColumn("Title"); t.AddColumn("Done"); t.AddColumn("State");
            foreach (var s in r.Stages)
                t.AddRow(Markup.Escape(s.Id), Markup.Escape(s.Title), $"{s.Done}/{s.Total}",
                    ColourState(s.State, s.Id == r.CurrentStageId));
            AnsiConsole.Write(t);
        }

        if (r.RecentSessions.Count > 0)
        {
            var h = new Table().Border(TableBorder.Rounded).Title("recent sessions");
            h.AddColumn("#"); h.AddColumn("Stage"); h.AddColumn("Kind"); h.AddColumn("Outcome"); h.AddColumn("Cost");
            foreach (var s in r.RecentSessions)
                h.AddRow(s.Number.ToString(), Markup.Escape(s.Stage), Markup.Escape(s.Kind),
                    Markup.Escape(s.Outcome), $"${s.CostUsd:0.00}");
            AnsiConsole.Write(h);
        }
    }

    private static string ColourState(string state, bool current)
    {
        var text = current ? $"{state} ◀" : state;
        var color = state switch
        {
            "confirmed" or "done" => "green",
            "active" or "gating" => "yellow",
            "skipped" => "red",
            _ => "grey",
        };
        return $"[{color}]{Markup.Escape(text)}[/]";
    }

    // ── --deep: optional LLM narrative on top of the fast verdict ──

    private int RunDeepAnalysis(PlanConfig plan, StatusReport report, Settings settings)
    {
        var sc = plan.StatusAgent;
        if (sc == null || !sc.Enabled)
        {
            AnsiConsole.MarkupLine("[grey]--deep requested but statusAgent is disabled in the plan.[/]");
            return 0;
        }
        if (sc.MaxPerHour > 0 && CountRecentInvocations(plan.StateDir, TimeSpan.FromHours(1)) >= sc.MaxPerHour)
        {
            AnsiConsole.MarkupLine($"[red]Rate limited:[/] {sc.MaxPerHour} status analyses this hour. Wait or raise statusAgent.maxPerHour.");
            return 1;
        }

        DateTime? sinceUtc = null;
        if (!string.IsNullOrWhiteSpace(settings.Since))
        {
            if (!DateTime.TryParse(settings.Since, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            {
                AnsiConsole.MarkupLine($"[red]Invalid --since value: '{Markup.Escape(settings.Since)}'. Use ISO 8601 (e.g. 2026-07-09T12:00Z).[/]");
                return 1;
            }
            sinceUtc = parsed;
        }

        // Re-fold from the database so the narrative sees exactly what the verdict did.
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        RunState state;
        using (var store = new SqliteRunStore(runDbPath, NullLogger<SqliteRunStore>.Instance))
        {
            var runId = store.GetLatestRunId(plan.Name);
            state = runId == null ? new RunState { PlanName = plan.Name }
                : RunStateProjection.Fold(store.ReadAllEvents(runId));
        }
        var track = SafeReadTracker(plan);
        var gitSummary = $"branch: {Git.Branch(plan.Repo)}";

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold aqua]Running LLM status analysis…[/]");
        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, "", gitSummary, report.DoneCount, report.TotalCount, sinceUtc);
        var result = StatusAgent.Run(sc, prompt);
        AnsiConsole.MarkupLine("[bold aqua]── Status Report (LLM) ──[/]");
        AnsiConsole.WriteLine(result);
        RecordInvocation(plan.StateDir);
        return 0;
    }

    private static TrackerSnapshot SafeReadTracker(PlanConfig plan)
    {
        try { return ProgressProviderFactory.Create(plan).Read(plan, CancellationToken.None); }
        catch (Exception) { return new TrackerSnapshot(); }
    }

#pragma warning disable MA0045 // sync file I/O at Spectre.Cli sync boundary (same pattern as RunCommand)
    private static void RecordInvocation(string stateDir)
    {
        try
        {
            File.AppendAllText(Path.Combine(stateDir, InvocationsFile), DateTime.UtcNow.ToString("u") + Environment.NewLine);
        }
        catch (IOException) { /* best-effort */ }
    }

    private static int CountRecentInvocations(string stateDir, TimeSpan window)
    {
        try
        {
            var file = Path.Combine(stateDir, InvocationsFile);
            if (!File.Exists(file)) return 0;
            var cutoff = DateTime.UtcNow - window;
            return File.ReadAllLines(file).Count(line =>
                !string.IsNullOrWhiteSpace(line)
                && DateTime.TryParse(line, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
                && dt >= cutoff);
        }
        catch (IOException) { return 0; }
    }
#pragma warning restore MA0045
}
