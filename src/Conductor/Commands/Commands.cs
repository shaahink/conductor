using System.ComponentModel;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Core.Integrations;
using Conductor.Models;
using Conductor.Ui;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

public class PlanSettings : CommandSettings
{
    [CommandOption("-p|--plan <PLAN>")]
    [Description("Path to the plan JSON. Falls back to CONDUCTOR_PLAN env var, then ./conductor.plan.json")]
    public string? Plan { get; init; }

    public string ResolvePlanPath()
    {
        var p = Plan
                ?? Environment.GetEnvironmentVariable("CONDUCTOR_PLAN")
                ?? (File.Exists("conductor.plan.json") ? "conductor.plan.json" : null);
        if (p == null)
            throw new InvalidOperationException("No plan given. Use --plan <path>, set CONDUCTOR_PLAN, or place conductor.plan.json in the cwd.");
        return p;
    }
}

public sealed class RunCommand : Command<RunCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--dry-run")]
        [Description("Print the next session's prompt and exit — nothing is spawned.")]
        public bool DryRun { get; init; }

        [CommandOption("--once")]
        [Description("Run exactly one session, then stop.")]
        public bool Once { get; init; }

        [CommandOption("--max-sessions <N>")]
        [Description("Stop after N sessions this run (0 = unlimited).")]
        public int MaxSessions { get; init; }

        [CommandOption("--no-dashboard")]
        [Description("Plain line output instead of the live dashboard.")]
        public bool NoDashboard { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        Directory.CreateDirectory(plan.StateDir);
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        if (string.IsNullOrEmpty(state.RunId)) state.RunId = Guid.NewGuid().ToString("N");

        var opts = new RunOptions(settings.DryRun, settings.Once, settings.MaxSessions);
        using var cts = new CancellationTokenSource();
#pragma warning disable MA0045 // CancelAsync doesn't exist on CancellationTokenSource
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
#pragma warning restore MA0045

        // Event log is additive alongside state.json (B2.1). A dry-run previews without writing.
        IEventSink events = settings.DryRun
            ? NullEventSink.Instance
            : new EventLog(Path.Combine(plan.StateDir, "events.jsonl"), state.RunId);
        try
        {
            var usePlain = settings.NoDashboard || settings.DryRun || Console.IsOutputRedirected;
            if (usePlain)
            {
                // Host = composition + structured-logging root (B2.5). The console sink is on for plain
                // runs (no TUI to corrupt); options are validated on start inside Build.
                using var host = ConductorHost.Build(plan, state, statePath, new PlainSink(), events, opts, consoleSink: true);
                return host.Services.GetRequiredService<Orchestrator>().Run(cts.Token);
            }

            var dash = new LiveDashboard(plan);
            // Dashboard owns stdout, so the Serilog console sink is disabled here (file sink only).
            using var dashHost = ConductorHost.Build(plan, state, statePath, dash, events, opts, consoleSink: false);
            var orchestrator = dashHost.Services.GetRequiredService<Orchestrator>();
            var task = Task.Run(() => orchestrator.Run(cts.Token));
            dash.RunUiLoop(task);
#pragma warning disable MA0045 // sync-over-async boundary: Spectre.Cli Execute must return int
            return task.GetAwaiter().GetResult();
#pragma warning restore MA0045
        }
        finally
        {
            (events as IDisposable)?.Dispose();
        }
    }
}

public sealed class StatusCommand : Command<StatusCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--since <DATETIME>")]
        [Description("Only show delta since the given UTC datetime (ISO 8601). Default: full report.")]
        public string? Since { get; init; }

        [CommandOption("--no-llm")]
        [Description("Skip the LLM analysis — show only tables (fast, offline).")]
        public bool NoLlm { get; init; }
    }

    private const int LogTailLines = 50;
    private const string InvocationsFile = "status-invocations.jsonl";

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var track = TrackerParser.ParseFile(plan.TrackerPath);

        var totalDone = track.Checkpoints.Count(c => c.IsDone);
        var totalCk = track.Checkpoints.Count;

        // Read conductor.log tail
        var logPath = Path.Combine(plan.StateDir, "conductor.log");
        var logTail = "";
        if (File.Exists(logPath))
        {
            try { logTail = GateRunner.TailOf(File.ReadAllText(logPath), LogTailLines); }
            catch (IOException) { /* best-effort */ }
        }

        // Parse --since
        DateTime? sinceUtc = null;
        if (!string.IsNullOrWhiteSpace(settings.Since))
        {
            if (DateTime.TryParse(settings.Since, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                sinceUtc = parsed;
            else
            {
                AnsiConsole.MarkupLine($"[red]Invalid --since value: '{Markup.Escape(settings.Since)}'. Use ISO 8601 (e.g. 2026-07-09T12:00Z).[/]");
                return 1;
            }
        }

        // Build git summary
        var head = Git.Head(plan.Repo);
        var gitSummary = $"branch: {Git.Branch(plan.Repo)}\n" +
                         $"HEAD: {(head.Length > 7 ? head[..7] : head)}\n" +
                         $"recent commits:\n" + RecentCommits(plan.Repo, 5);

        // Show compact header
        var statusColor = state.Status switch
        {
            RunStatus.Idle or RunStatus.Completed => "green",
            RunStatus.Running or RunStatus.VerifyingGates => "yellow",
            RunStatus.Backoff => "orange1",
            RunStatus.Paused or RunStatus.NeedsHuman or RunStatus.AwaitingOwner => "red",
            RunStatus.Aborted => "red",
            _ => "grey",
        };
        AnsiConsole.MarkupLine($"[bold aqua]Conductor[/] — [bold]{Markup.Escape(plan.Name)}[/] · [{statusColor}]{state.Status}[/]" +
                               (state.AttentionReason != null ? $" — [red]{Markup.Escape(state.AttentionReason)}[/]" : ""));
        AnsiConsole.MarkupLine($"repo {Markup.Escape(plan.Repo)} · branch {Markup.Escape(Git.Branch(plan.Repo))} · " +
                               $"checkpoints [bold]{totalDone}/{totalCk}[/] · " +
                               $"sessions {state.SessionCounter} · cost ${state.TotalCostUsd:0.00}");

        // Render stage overview table (always shown)
        AnsiConsole.WriteLine();
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("Stage"); t.AddColumn("Title"); t.AddColumn("Done"); t.AddColumn("State");
        foreach (var s in plan.Stages)
        {
            var rows = track.ForStage(s.Id).ToList();
            var done = rows.Count(r => r.IsDone);
            var st = state.SkippedStages.Contains(s.Id) ? "[red]skipped[/]"
                : rows.Count > 0 && done == rows.Count ? "[green]done[/]"
                : s.Id == state.CurrentStage ? "[yellow]active[/]" : "[grey]todo[/]";
            t.AddRow(Markup.Escape(s.Id), Markup.Escape(s.Title), $"{done}/{rows.Count}", st);
        }
        AnsiConsole.Write(t);

        // Render recent sessions table
        if (state.History.Count > 0)
        {
            var h = new Table().Border(TableBorder.Rounded).Title("recent sessions");
            h.AddColumn("#"); h.AddColumn("Stage"); h.AddColumn("Kind"); h.AddColumn("Outcome"); h.AddColumn("DONE"); h.AddColumn("Commits"); h.AddColumn("Gates");
            foreach (var r in state.History.TakeLast(10))
                h.AddRow(r.Number.ToString(), Markup.Escape(r.Stage), r.Kind.ToString(),
                         Markup.Escape(r.Outcome?.ToString() ?? "running"),
                         Markup.Escape(string.Join(" ", r.NewlyDone)),
                         r.NewCommits.Count.ToString(), Markup.Escape(r.GateSummary));
            AnsiConsole.Write(h);
        }

        // LLM analysis (unless --no-llm or statusAgent is disabled)
        var sc = plan.StatusAgent;
        if (settings.NoLlm || sc == null || !sc.Enabled)
        {
            if (settings.NoLlm)
                AnsiConsole.MarkupLine("[grey]LLM analysis skipped (--no-llm).[/]");
            else
                AnsiConsole.MarkupLine("[grey]LLM analysis disabled (statusAgent.enabled = false).[/]");
            RecordInvocation(plan.StateDir);
            return 0;
        }

        // Rate limit check
        if (sc.MaxPerHour > 0)
        {
            var recentCount = CountRecentInvocations(plan.StateDir, TimeSpan.FromHours(1));
            if (recentCount >= sc.MaxPerHour)
            {
                AnsiConsole.MarkupLine($"[red]Rate limited: {recentCount}/{sc.MaxPerHour} calls this hour.[/] Wait or increase statusAgent.maxPerHour in the plan.");
                return 1;
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold aqua]Running LLM status analysis…[/]");

        var prompt = StatusAgent.BuildCliPrompt(plan, state, track, logTail, gitSummary, totalDone, totalCk, sinceUtc);
        var result = StatusAgent.Run(sc, prompt);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold aqua]── Status Report (LLM) ──[/]");
        AnsiConsole.WriteLine(result);
        AnsiConsole.MarkupLine("[bold aqua]──────────────────────────[/]");

        RecordInvocation(plan.StateDir);
        return 0;
    }

    private static string RecentCommits(string repo, int count)
    {
        try
        {
            var output = ProcessRunner.Run("git", new[] { "log", $"-n{count}", "--oneline", "--no-decorate" }, repo,
                TimeSpan.FromSeconds(5)).Output;
            return string.IsNullOrWhiteSpace(output) ? "(no commits)" : output.Trim();
        }
        catch { return "(git failed)"; }
    }

#pragma warning disable MA0045 // sync file I/O at Spectre.Cli sync boundary (same pattern as RunCommand)
    private static void RecordInvocation(string stateDir)
    {
        try
        {
            var file = Path.Combine(stateDir, InvocationsFile);
            File.AppendAllText(file, DateTime.UtcNow.ToString("u") + Environment.NewLine);
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
            var lines = File.ReadAllLines(file);
            return lines.Count(line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return false;
                return DateTime.TryParse(line, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var dt) && dt >= cutoff;
            });
        }
        catch (IOException) { return 0; }
    }
#pragma warning restore MA0045
}

/// <summary>
/// D2 — Ad-hoc gate re-run at HEAD without spawning an agent session. Re-runs the plan's
/// gate battery directly and reports PASS/FAIL. If all required gates pass and a
/// <see cref="RunState.PendingFix"/> exists, it is cleared and the state set to Idle.
/// </summary>
public sealed class GateCommand : Command<GateCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--full")]
        [Description("Run the full battery (not just fast-tier gates). Default: fast-tier only.")]
        public bool Full { get; init; }
    }

#pragma warning disable MA0045 // sync file I/O at Spectre.Cli sync boundary (same pattern as RunCommand/StatusCommand)
    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var logPath = Path.Combine(plan.StateDir, "conductor.log");

        LogGateEvent(logPath, $"gate {(settings.Full ? "FULL" : "fast")} battery starting @ HEAD {Git.Head(plan.Repo)}");

        var fastOnly = !settings.Full;
        var ct = CancellationToken.None;
        var gates = GateRunner.RunAll(plan, msg => LogGateEvent(logPath, msg), ct, fastOnly, state.CurrentStage, null);

        var allGreen = GateRunner.AllRequiredPassed(gates);
        var summary = GateRunner.Summary(gates);

        // Report results
        var verdict = allGreen ? "[green]PASS[/]" : "[red]FAIL[/]";
        AnsiConsole.MarkupLine($"[bold aqua]conductor gate[/] ({ (settings.Full ? "full" : "fast") }): {verdict} — {Markup.Escape(summary)}");
        foreach (var g in gates)
        {
            var icon = g.Skipped ? "[grey]-[/]"
                : g.Passed ? "[green]OK[/]"
                : g.Optional ? "[yellow]warn[/]"
                : "[red]FAIL[/]";
            AnsiConsole.MarkupLine($"  {icon} {Markup.Escape(g.Name)} ({g.Duration.TotalSeconds:0.0}s)");
            if (!g.Passed && !g.Skipped)
                AnsiConsole.WriteLine(g.Tail);
        }

        LogGateEvent(logPath, $"gate battery done — {(allGreen ? "GREEN" : "RED")}: {summary}");

        // If all green and previously-red, clear pendingFix
        if (allGreen && state.PendingFix != null)
        {
            state.PendingFix = null;
            state.Status = RunStatus.Idle;
            state.AttentionReason = null;
            state.Save(statePath);
            LogGateEvent(logPath, "gate: all green — cleared pendingFix, set Idle");
            AnsiConsole.MarkupLine("[green]Pending fix cleared — state set to Idle.[/]");
        }
        else if (allGreen)
        {
            AnsiConsole.MarkupLine("[green]All gates passed.[/]");
        }
        else
        {
            var details = GateRunner.FailureDetails(gates);
            LogGateEvent(logPath, $"gate FAILURE details:\n{details}");
        }

        return allGreen ? 0 : 1;
    }

    private static void LogGateEvent(string logPath, string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        try { File.AppendAllText(logPath, stamped + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
#pragma warning restore MA0045
}

/// <summary>
/// B9.5 — CLI task view. Reads <c>events.jsonl</c>, folds it through <see cref="TaskGraph"/>,
/// and renders sub-tasks per checkpoint as a Spectre table with status indicators.
/// </summary>
public sealed class TasksCommand : Command<PlanSettings>
{
    public override int Execute(CommandContext context, PlanSettings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var eventsPath = Path.Combine(plan.StateDir, "events.jsonl");
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);

        var graph = new TaskGraph();
        if (File.Exists(eventsPath))
            graph.Fold(EventLog.ReadAll(eventsPath));

        if (graph.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]no tasks recorded yet.[/] run the planner or agent to populate the task graph.");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold aqua]Conductor[/] — [bold]{Markup.Escape(plan.Name)}[/] · task graph · {graph.Count} tasks");
        AnsiConsole.WriteLine();

        var checkpoints = graph.Tasks
            .GroupBy(t => t.CheckpointId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var ck in checkpoints)
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[bold]{Markup.Escape(ck.Key)}[/]")
                .AddColumn("Status")
                .AddColumn("Title")
                .AddColumn("Source");

            foreach (var task in ck.OrderBy(t => t.Order))
            {
                var icon = task.Status switch
                {
                    "done" => "[green]DONE[/]",
                    "in_progress" => "[yellow]▶ ACTV[/]",
                    "skipped" => "[red]SKIP[/]",
                    _ => "[grey]TODO[/]",
                };
                var source = task.Source switch
                {
                    "planner" => "[grey]planner[/]",
                    "agent" => "[grey]agent[/]",
                    "human" => "[grey]human[/]",
                    _ => $"[grey]{Markup.Escape(task.Source)}[/]",
                };
                table.AddRow(icon, Markup.Escape(task.Title), source);
            }
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }

        return 0;
    }
}

/// <summary>
/// O1 — Structured log query. Reads the rolling JSON log files (<c>.conductor/logs/conductor-*.json</c>)
/// and filters entries by query expression. Each line is a valid compact JSON object with correlation
/// properties (runId, sessionId, stage, gate, outcome) plus the message (<c>@m</c>).
/// </summary>
public sealed class LogCommand : Command<LogCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("-q|--query <EXPR>")]
        [Description("Filter expression: key=value pairs separated by ' and ' (case-insensitive). Example: --query \"stage=P7 and gate=build and outcome=fail\"")]
        public string? Query { get; init; }

        [CommandOption("--since <DATETIME>")]
        [Description("Only show entries on or after this UTC datetime (ISO 8601).")]
        public string? Since { get; init; }

        [CommandOption("--tail <N>")]
        [Description("Show only the last N matching entries.")]
        public int? Tail { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var logDir = Path.Combine(plan.StateDir, "logs");
        if (!Directory.Exists(logDir))
        {
            AnsiConsole.MarkupLine("[yellow]No log directory found.[/] Run conductor at least once to generate logs.");
            return 0;
        }

        var pattern = settings.Query;
        var filters = ParseQuery(pattern);

        DateTime? sinceUtc = null;
        if (!string.IsNullOrWhiteSpace(settings.Since))
        {
            if (DateTime.TryParse(settings.Since, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
                sinceUtc = parsed;
            else
            {
                AnsiConsole.MarkupLine($"[red]Invalid --since value: '{Markup.Escape(settings.Since)}'. Use ISO 8601 (e.g. 2026-07-09T12:00Z).[/]");
                return 1;
            }
        }

        var jsonFiles = Directory.EnumerateFiles(logDir, "conductor-*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (jsonFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No JSON log files found.[/] Run conductor at least once to generate structured logs.");
            return 0;
        }

        var matched = new List<JsonLogEntry>();
        foreach (var file in jsonFiles)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = System.Text.Json.JsonSerializer.Deserialize<JsonLogEntry>(line,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (entry == null) continue;
                    if (!Matches(entry, filters)) continue;
                    if (sinceUtc.HasValue && entry.Timestamp < sinceUtc.Value) continue;
                    matched.Add(entry);
                }
                catch (System.Text.Json.JsonException) { /* tolerate corrupt lines */ }
            }
        }

        if (settings.Tail is { } limit and > 0 && matched.Count > limit)
            matched = matched.Skip(matched.Count - limit).ToList();

        if (matched.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No matching log entries.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold aqua]conductor log[/] — {matched.Count} match{(matched.Count == 1 ? "" : "es")}" +
                               (pattern != null ? $" for '{Markup.Escape(pattern)}'" : ""));
        AnsiConsole.WriteLine(new string('-', 80));
        foreach (var e in matched)
            AnsiConsole.WriteLine(FormatEntry(e));
        AnsiConsole.WriteLine(new string('-', 80));

        return 0;
    }

    internal sealed record JsonLogEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("@t")]
        public DateTime Timestamp { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("@m")]
        public string Message { get; init; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("@l")]
        public string? Level { get; init; }
        public string? RunId { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("sessionId")]
        public string? SessionId { get; init; }
        public string? Stage { get; init; }
        public string? Gate { get; init; }
        public string? Outcome { get; init; }
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, object>? Extra { get; init; }
    }

    /// <summary>Parses <c>key=value and key=value</c> into a case-insensitive filter dictionary.</summary>
    internal static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return result;
        var parts = query.Split([" and ", " AND "], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (key.Length > 0) result[key] = value;
        }
        return result;
    }

    internal static bool Matches(JsonLogEntry entry, Dictionary<string, string> filters)
    {
        if (filters.Count == 0) return true;
        foreach (var (key, value) in filters)
        {
            var fieldValue = key.ToLowerInvariant() switch
            {
                "runid" => entry.RunId,
                "sessionid" => entry.SessionId,
                "stage" => entry.Stage,
                "gate" => entry.Gate,
                "outcome" => entry.Outcome,
                "level" => entry.Level,
                _ => null,
            };
            if (fieldValue == null || !string.Equals(fieldValue, value, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    internal static string FormatEntry(JsonLogEntry e)
    {
        var tags = new List<string>();
        if (e.Stage != null) tags.Add($"stage:{e.Stage}");
        if (e.Gate != null) tags.Add($"gate:{e.Gate}");
        if (e.Outcome != null) tags.Add(e.Outcome.ToUpperInvariant());
        var tagStr = tags.Count > 0 ? $" [{string.Join(" ", tags)}]" : "";
        return $"{e.Timestamp:yyyy-MM-dd HH:mm:ss} [{e.Level ?? "?"}]{tagStr} {e.Message}";
    }
}

public sealed class ReportCommand : Command<PlanSettings>
{
    public override int Execute(CommandContext context, PlanSettings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var track = TrackerParser.ParseFile(plan.TrackerPath);
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Reporter.ReportPath(plan), Reporter.Build(plan, state, track, null, null,
            Reporter.ReadTimeline(plan), Reporter.ReadHealth(plan),
            confidence: Reporter.ReadConfidence(track), mcp: Reporter.ReadMcpMetrics(plan),
            repo: Reporter.ReadRepoStrip(plan)), Reporter.Utf8Bom);
        AnsiConsole.MarkupLine($"report written to [bold]{Markup.Escape(Reporter.ReportPath(plan))}[/]");
        return 0;
    }
}

/// <summary>
/// B5.2 — replay / time-travel viewer. Reconstructs a past run from its append-only
/// <c>events.jsonl</c>, printing every transition in order with the run state reconstructed as of
/// that moment (the same fold the TUI <c>F8</c> modal renders). The source is an explicit path to an
/// <c>events.jsonl</c> (or a repo/dir containing <c>.conductor/events.jsonl</c>); omit it to replay
/// the current plan's log.
/// </summary>
public sealed class ReplayCommand : Command<ReplayCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[SOURCE]")]
        [Description("Path to an events.jsonl, or a repo/dir containing .conductor/events.jsonl. Omit to use the plan's log (--plan / CONDUCTOR_PLAN).")]
        public string? Source { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var logPath = ResolveLogPath(settings);
        if (logPath == null || !File.Exists(logPath))
        {
            AnsiConsole.MarkupLine($"[red]no event log found[/]{(logPath != null ? $" at {Markup.Escape(logPath)}" : "")} — pass a path to events.jsonl, or use --plan.");
            return 1;
        }

        var events = EventLog.ReadAll(logPath);
        var steps = Replay.Build(events);
        if (steps.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]event log has no transitions to replay[/] ({Markup.Escape(logPath)}).");
            return 0;
        }

        var runId = events.Select(e => e.RunId).FirstOrDefault(r => !string.IsNullOrEmpty(r)) ?? "?";
        AnsiConsole.WriteLine($"replay · {logPath}");
        AnsiConsole.WriteLine($"run {runId} · {events.Count} events · {steps.Count} transitions");
        AnsiConsole.WriteLine(new string('-', 72));
        foreach (var line in steps.SelectMany(Replay.FormatStep))
            AnsiConsole.WriteLine(line);
        AnsiConsole.WriteLine(new string('-', 72));
        AnsiConsole.WriteLine("final: " + Replay.FormatState(steps[^1].StateAsOf));
        return 0;
    }

    // A given source may be a direct events.jsonl, a repo/dir (→ .conductor/events.jsonl), or absent
    // (→ the resolved plan's log). A given-but-missing path is returned as-is so the caller's
    // File.Exists check surfaces the exact path in the error.
    private static string? ResolveLogPath(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Source))
        {
            if (File.Exists(settings.Source)) return settings.Source;
            if (Directory.Exists(settings.Source))
            {
                var nested = Path.Combine(settings.Source, ".conductor", "events.jsonl");
                if (File.Exists(nested)) return nested;
                var direct = Path.Combine(settings.Source, "events.jsonl");
                return File.Exists(direct) ? direct : nested;
            }
            return settings.Source;
        }
        try
        {
            var plan = PlanConfig.Load(settings.ResolvePlanPath());
            return Path.Combine(plan.StateDir, "events.jsonl");
        }
        catch (InvalidOperationException) { return null; }
    }
}

/// <summary>Offline dashboard preview: renders the current plan/tracker state (read-only) with
/// representative synthetic session data, so the UI can be verified without running the plan.</summary>
public sealed class PreviewCommand : Command<PlanSettings>
{
    public override int Execute(CommandContext context, PlanSettings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        TrackerSnapshot track;
        // Preview is a read-only UI convenience: a missing/unreadable tracker just renders an empty
        // plan rather than aborting the preview. A malformed table is not fatal here.
        try { track = TrackerParser.ParseFile(plan.TrackerPath); }
        catch (IOException) { track = new TrackerSnapshot(); }

        var dash = new LiveDashboard(plan);
        DashboardPreview.Seed(dash, plan, state, track);
        AnsiConsole.MarkupLine("[grey]rendering preview — press any key to exit…[/]");
        dash.RunPreview();
        return 0;
    }
}

/// <summary>Writes the control file consumed by a running conductor (works from any terminal).</summary>
public abstract class CtlCommand(string command, string explanation, bool dangerous = false) : Command<CtlCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--yes")]
        [Description("Skip confirmation prompt for destructive actions (abort/kill/skip/rollback).")]
        public bool Yes { get; init; }

        [CommandOption("--force")]
        [Description("rollback only: discard an uncommitted (dirty) working tree during the reset.")]
        public bool Force { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        if (dangerous && !settings.Yes)
        {
            AnsiConsole.MarkupLine($"[red]DESTRUCTIVE: {Markup.Escape(command)} — {Markup.Escape(explanation)}[/]");
            AnsiConsole.MarkupLine("[yellow]Use --yes to confirm, or interact via the dashboard TUI (double-tap the key).[/]");
            return 2;
        }
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Path.Combine(plan.StateDir, "control.json"),
            JsonSerializer.Serialize(new
            {
                command,
                issuedUtc = DateTime.UtcNow,
                confirmed = dangerous ? true : (bool?)null,
                intentId = dangerous ? Guid.NewGuid().ToString("N") : null,
                force = settings.Force ? true : (bool?)null,
            }));
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(command)}[/] queued — {Markup.Escape(explanation)}");
        return 0;
    }
}

public sealed class PauseCommand() : CtlCommand("pause", "the running conductor will pause after the current session");
public sealed class ResumeCtlCommand() : CtlCommand("resume", "a paused/needs-human conductor will continue");
public sealed class AbortCommand() : CtlCommand("abort", "the running conductor will kill the session and stop", dangerous: true);
public sealed class SkipCommand() : CtlCommand("skip", "the current stage will be skipped and flagged for review", dangerous: true);
public sealed class KillCommand() : CtlCommand("kill", "the current agent session will be killed (conductor keeps running)", dangerous: true);
public sealed class ApproveCommand() : CtlCommand("approve", "approve the currently owner-gated stage so the conductor advances past it");
public sealed class RetryStageCommand() : CtlCommand("retry-stage", "reset the attempt counter and re-queue a deliver session for the current stage");
public sealed class RollbackCommand() : CtlCommand("rollback", "reset the working tree to the stage's checkpoint commit (refuses if dirty)", dangerous: true);
public sealed class PauseAfterStageCommand() : CtlCommand("pause-after-stage", "park at Paused after the current stage completes rather than advancing");

/// <summary>Jump to a specific stage (clears pending fix/resume/gates for the old stage).</summary>
public sealed class GotoCommand : Command<GotoCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "<STAGE>")]
        [Description("The stage ID to jump to (e.g. B3).")]
        public string StageId { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Path.Combine(plan.StateDir, "control.json"),
            JsonSerializer.Serialize(new { command = "goto", stageId = settings.StageId, issuedUtc = DateTime.UtcNow }));
        AnsiConsole.MarkupLine($"[green]goto[/] queued → stage {Markup.Escape(settings.StageId)}");
        return 0;
    }
}

/// <summary>Toggle heartbeat on|off at runtime without restarting conductor.</summary>
public sealed class HeartbeatCommand : Command<HeartbeatCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "<on|off>")]
        [Description("on = enable heartbeat, off = pause heartbeats.")]
        public string Value { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var v = settings.Value.ToLowerInvariant();
        if (v is not "on" and not "off")
        {
            AnsiConsole.MarkupLine("[red]heartbeat expects 'on' or 'off'[/]");
            return 1;
        }
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Path.Combine(plan.StateDir, "control.json"),
            JsonSerializer.Serialize(new { command = "toggle-heartbeat", value = v, issuedUtc = DateTime.UtcNow }));
        AnsiConsole.MarkupLine($"[green]heartbeat {v}[/] queued — the running conductor will toggle heartbeats {(v == "on" ? "on" : "off")}");
        return 0;
    }
}

/// <summary>
/// P1 — Dynamic plan reconfiguration: plan set, reload, add-stage. Subcommands dispatch to
/// Set / Reload / AddStage; a bare `conductor plan` prints the current plan summary.
/// </summary>
public sealed class PlanCommand : Command<PlanCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[VERB]")]
        [Description("Sub-command: set, reload, or add-stage. Omit to show plan summary.")]
        public string Verb { get; init; } = "";

        [CommandArgument(1, "[KEY]")]
        [Description("Dot-notation key path (set only, e.g. limits.maxRunCostUsd).")]
        public string? Key { get; init; }

        [CommandArgument(2, "[VALUE]")]
        [Description("New value (set only).")]
        public string? Value { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var verb = settings.Verb.ToLowerInvariant();
        return verb switch
        {
            "set" => PlanSetCommand.ExecuteSet(settings.ResolvePlanPath(), settings.Key, settings.Value),
            "reload" => PlanReloadCommand.ExecuteReload(settings.ResolvePlanPath()),
            "add-stage" => PlanAddStageCommand.ExecuteAddStage(settings.ResolvePlanPath(), settings),
            _ => PrintPlanSummary(settings),
        };
    }

    private static int PrintPlanSummary(Settings settings)
    {
        try
        {
            var planPath = settings.ResolvePlanPath();
            var plan = PlanConfig.Load(planPath);
            AnsiConsole.MarkupLine($"[bold aqua]conductor plan[/] — [bold]{Markup.Escape(plan.Name)}[/] v{plan.PlanVersion}");
            AnsiConsole.MarkupLine($"repo: {Markup.Escape(plan.Repo)}");
            AnsiConsole.MarkupLine($"stages: {plan.Stages.Count}   gates: {plan.Gates.Count}   gate-policy: {plan.GatePolicy}");
            AnsiConsole.MarkupLine($"limits: stall={plan.Limits.StallMinutes}m timeout={plan.Limits.SessionTimeoutMinutes}m backoff={plan.Limits.BackoffMinutes}m");
            if (plan.Limits.MaxRunCostUsd is { } cap) AnsiConsole.MarkupLine($"cost-cap: ${cap:0.00}");
            if (plan.Limits.MaxRunTokens is { } tok) AnsiConsole.MarkupLine($"token-cap: {tok / 1000}K");
            AnsiConsole.MarkupLine($"plan: [grey]{Markup.Escape(planPath)}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]sub-commands: plan set <key> <value> | plan reload | plan add-stage[/]");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}

/// <summary>
/// P1 — `conductor plan set <key> <value>`: hot-update a single plan field via dot-notation path.
/// Loads the plan JSON, navigates to the key, writes the value, re-serialises, and validates.
/// Applied immediately to the plan file on disk; the orchestrator picks it up at next session boundary.
/// </summary>
public static class PlanSetCommand
{
    public static int ExecuteSet(string planPath, string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || value == null)
        {
            AnsiConsole.MarkupLine("[red]plan set requires <key> <value>. Examples:[/]");
            AnsiConsole.MarkupLine("  conductor plan set limits.maxRunCostUsd 0.50");
            AnsiConsole.MarkupLine("  conductor plan set limits.stallMinutes 15");
            AnsiConsole.MarkupLine("  conductor plan set gates.0.timeoutMinutes 30");
            AnsiConsole.MarkupLine("  conductor plan set report.heartbeatMinutes 5");
            return 1;
        }

        try
        {
            if (!File.Exists(planPath))
            {
                AnsiConsole.MarkupLine($"[red]Plan file not found: {Markup.Escape(planPath)}[/]");
                return 1;
            }

            // Load+serialise roundtrip to get clean JSON (strips comments)
            var plan = PlanConfig.Load(planPath);
            var cleanJson = System.Text.Json.JsonSerializer.Serialize(plan, PlanConfig.JsonOpts);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(cleanJson, new System.Text.Json.Nodes.JsonNodeOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Plan file produced empty JSON on serialisation.");

            // Navigate to the parent and set the leaf value
            var parts = key.Split('.');
            var node = doc.Root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                if (int.TryParse(part, out var idx) && node is System.Text.Json.Nodes.JsonArray arr)
                {
                    if (idx < 0 || idx >= arr.Count)
                    {
                        AnsiConsole.MarkupLine($"[red]Array index {idx} out of range for '{key}' (array has {arr.Count} items).[/]");
                        return 1;
                    }
                    node = arr[idx];
                }
                else
                {
                    var child = node![part];
                    if (child == null)
                    {
                        AnsiConsole.MarkupLine($"[red]Key segment '{part}' not found in path '{key}'. Check the key name (case-insensitive).[/]");
                        return 1;
                    }
                    node = child;
                }
            }

            var leafKey = parts[^1];
            var oldValue = node?[leafKey]?.ToString() ?? "(null)";

            // Parse the value: try numbers, booleans, then string
            if (decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var decimalVal))
            {
                if (value.Contains('.', StringComparison.Ordinal))
                    node![leafKey] = decimalVal;
                else
                    node![leafKey] = (int)decimalVal;
            }
            else if (bool.TryParse(value, out var boolVal))
            {
                node![leafKey] = boolVal;
            }
            else
            {
                node![leafKey] = value;
            }

            // Bump planVersion
            var pv = doc.Root["planVersion"];
            if (pv != null)
                doc.Root["planVersion"] = pv.GetValue<int>() + 1;
            else
                doc.Root["planVersion"] = 2;

            var newJson = doc.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });

            // Validate the result by deserialising it
            try
            {
                var test = System.Text.Json.JsonSerializer.Deserialize<PlanConfig>(newJson, PlanConfig.JsonOpts);
                if (test != null)
                {
                    test.PlanFilePath = planPath;
                    test.Validate();
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Validation failed after set: {Markup.Escape(ex.Message)}[/]");
                AnsiConsole.MarkupLine("[yellow]Plan file was NOT modified. Fix the value and try again.[/]");
                return 1;
            }

            File.WriteAllText(planPath, newJson, System.Text.Encoding.UTF8);
            AnsiConsole.MarkupLine($"[green]plan set[/] {Markup.Escape(key)} = [bold]{Markup.Escape(value)}[/] (was {Markup.Escape(oldValue)})");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or System.Text.Json.JsonException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}

/// <summary>
/// P1 — `conductor plan reload`: re-read the full plan JSON from disk, validate it, and report.
/// The running orchestrator picks up changes at its next session boundary.
/// </summary>
public static class PlanReloadCommand
{
    public static int ExecuteReload(string planPath)
    {
        try
        {
            if (!File.Exists(planPath))
            {
                AnsiConsole.MarkupLine($"[red]Plan file not found: {Markup.Escape(planPath)}[/]");
                return 1;
            }

            var plan = PlanConfig.Load(planPath);
            var stageCount = plan.Stages.Count;
            var gateCount = plan.Gates.Count;
            var table = new Table().Border(TableBorder.Rounded).Title("[bold aqua]plan reloaded[/]");
            table.AddColumn("field"); table.AddColumn("value");
            table.AddRow("name", Markup.Escape(plan.Name));
            table.AddRow("version", plan.Version);
            table.AddRow("planVersion", plan.PlanVersion.ToString());
            table.AddRow("repo", Markup.Escape(plan.Repo));
            table.AddRow("stages", stageCount.ToString());
            table.AddRow("gates", gateCount.ToString());
            table.AddRow("gatePolicy", plan.GatePolicy);
            table.AddRow("gate (fast tier)", plan.Gates.Count(g => g.IsFast).ToString());
            table.AddRow("limits.stallMinutes", plan.Limits.StallMinutes.ToString());
            table.AddRow("limits.sessionTimeoutMinutes", plan.Limits.SessionTimeoutMinutes.ToString());
            if (plan.Limits.MaxRunCostUsd is { } cap) table.AddRow("limits.maxRunCostUsd", $"${cap:0.00}");
            if (plan.Limits.MaxRunTokens is { } tok) table.AddRow("limits.maxRunTokens", $"{tok / 1000}K");
            table.AddRow("agent.command", plan.Agent.Command);
            table.AddRow("report.heartbeatMinutes", plan.Report.HeartbeatMinutes.ToString());
            table.AddRow("statusAgent.enabled", plan.StatusAgent?.Enabled.ToString() ?? "false");
            if (plan.ReadOrder is { Count: > 0 }) table.AddRow("readOrder", string.Join(", ", plan.ReadOrder));
            AnsiConsole.Write(table);

            AnsiConsole.MarkupLine($"[green]Plan validated — {stageCount} stages, {gateCount} gates, v{plan.PlanVersion}.[/]");
            AnsiConsole.MarkupLine("[grey]The running conductor will pick up changes at its next session boundary.[/]");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]Plan validation failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}

/// <summary>
/// P1 — `conductor plan add-stage <json>`: append a new stage with checkpoints to the plan.
/// The stage JSON is provided inline or piped via stdin; it is validated against the StageConfig schema
/// before being appended. The plan's version is bumped automatically.
/// Examples:
///   conductor plan add-stage "{\"id\":\"P9\",\"title\":\"New phase\",\"sessions\":2}"
/// </summary>
public static class PlanAddStageCommand
{
    public static int ExecuteAddStage(string planPath, PlanCommand.Settings settings)
    {
        // The Value field is the 3rd positional arg, but for add-stage we interpret the remaining args
        // as raw JSON. The Settings model puts KEY as the 2nd arg and VALUE as the 3rd, so
        // add-stage's JSON is in settings.Key (since verb=add-stage, the next arg is JSON).
        var json = settings.Key;
        if (string.IsNullOrWhiteSpace(json))
        {
            // Try reading from stdin (piped input)
            try
            {
                if (Console.IsInputRedirected)
                    json = Console.In.ReadToEnd();
            }
            catch { }
            if (string.IsNullOrWhiteSpace(json))
            {
                AnsiConsole.MarkupLine("[red]plan add-stage requires a JSON stage definition.[/]");
                AnsiConsole.MarkupLine("Example: conductor plan add-stage \"{\\\"id\\\":\\\"P9\\\",\\\"title\\\":\\\"New phase\\\",\\\"sessions\\\":2}\"");
                return 1;
            }
        }

        try
        {
            var plan = PlanConfig.Load(planPath);

            var stage = System.Text.Json.JsonSerializer.Deserialize<StageConfig>(json, PlanConfig.JsonOpts)
                ?? throw new InvalidOperationException("Stage JSON deserialised to null.");

            // Validate the stage
            if (string.IsNullOrWhiteSpace(stage.Id))
                throw new InvalidOperationException("stage.id is required.");
            if (string.IsNullOrWhiteSpace(stage.Title))
                throw new InvalidOperationException("stage.title is required.");
            if (plan.Stages.Any(s => s.Id.Equals(stage.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"stage '{stage.Id}' already exists in the plan.");

            plan.AddStage(stage);
            plan.Save();

            AnsiConsole.MarkupLine($"[green]stage added[/] → [bold]{Markup.Escape(stage.Id)}[/] [grey]{Markup.Escape(stage.Title)}[/] (plan v{plan.PlanVersion})");
            AnsiConsole.MarkupLine($"[grey]Total stages now: {plan.Stages.Count}. Don't forget to add checkpoint rows to the tracker.[/]");
            return 0;
        }
        catch (System.Text.Json.JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]Invalid stage JSON: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}

/// <summary>Queues a human instruction for the agent (from any terminal) — injected into the next session.</summary>
public sealed class InjectCommand : Command<InjectCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "<INSTRUCTION>")]
        [Description("The instruction to queue for the agent's next session.")]
        public string Instruction { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var prev = InstructionQueue.List(plan).LastOrDefault()?.File;
        var entry = InstructionQueue.Write(plan, settings.Instruction, prev);
        AnsiConsole.MarkupLine($"[green]queued[/] {Markup.Escape(entry.File)} — injected into the next session prompt");
        return 0;
    }
}

/// <summary>Scaffolds a new plan + TRACKER.md from a built-in template (B1.6).</summary>
public sealed class NewPlanCommand : Command<NewPlanCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--template <TEMPLATE>")]
        [Description("Template name: minimal, dotnet, node, or shamshir. Default: dotnet.")]
        public string Template { get; init; } = "dotnet";

        [CommandOption("-o|--output <DIR>")]
        [Description("Directory to create the files in. Created if missing. Default: cwd.")]
        public string? Output { get; init; }

        [CommandOption("--name <NAME>")]
        [Description("Plan name. Default: directory name or 'plan'.")]
        public string? Name { get; init; }

        [CommandOption("--repo <PATH>")]
        [Description("Absolute path to the repo. Default: output directory.")]
        public string? Repo { get; init; }

        public static readonly string[] ValidTemplates = ["minimal", "dotnet", "node", "shamshir"];
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var template = settings.Template.ToLowerInvariant();
        if (!Settings.ValidTemplates.Contains(template))
        {
            AnsiConsole.MarkupLine($"[red]Unknown template '{Markup.Escape(settings.Template)}'.[/] Valid: {string.Join(", ", Settings.ValidTemplates)}");
            return 1;
        }

        var outputDir = Path.GetFullPath(settings.Output ?? ".");
        var name = settings.Name ?? Path.GetFileName(outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = "plan";
        var repo = settings.Repo ?? outputDir;

        Directory.CreateDirectory(outputDir);

        var planPath = Path.Combine(outputDir, "conductor.plan.json");
        var trackerPath = Path.Combine(outputDir, "TRACKER.md");

        if (File.Exists(planPath) || File.Exists(trackerPath))
        {
            AnsiConsole.MarkupLine("[red]Plan file(s) already exist — delete them first or use a different output directory.[/]");
            return 1;
        }

        File.WriteAllText(planPath, BuildPlanJson(template, name, repo), System.Text.Encoding.UTF8);
        File.WriteAllText(trackerPath, BuildTrackerMd(template, name), System.Text.Encoding.UTF8);

        // Verify the output loads (A6 ship-without-launch). Don't leave a half-written scaffold on
        // disk if the self-check fails — clean up and surface the reason.
        try
        {
            PlanConfig.Load(planPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            File.Delete(planPath);
            File.Delete(trackerPath);
            AnsiConsole.MarkupLine($"[red]Scaffold failed self-check and was removed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(planPath)}");
        AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(trackerPath)}");
        AnsiConsole.MarkupLine($"[grey]Template: {template}. Edit the plan + tracker, then run: conductor run -p {Markup.Escape(planPath)}[/]");
        return 0;
    }

    internal static string BuildPlanJson(string template, string name, string repo)
    {
        var repoNormalised = repo.Replace("\\", "/");
        return template switch
        {
            "minimal" => $$"""
            {
              "version": "1.0",
              "name": "{{name}}",
              "repo": "{{repoNormalised}}",
              "tracker": "TRACKER.md",
              "stages": [
                { "id": "S1", "title": "First phase", "sessions": 2 }
              ],
              "agent": {
                "command": "opencode",
                "args": ["run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--format", "json", "{prompt}"],
                "output": "opencode-json"
              },
              "gates": [],
              "report": { "commit": true, "push": true }
            }
            """,
            "dotnet" => $$"""
            {
              "version": "1.0",
              "name": "{{name}}",
              "repo": "{{repoNormalised}}",
              "tracker": "TRACKER.md",
              "stages": [
                { "id": "S1", "title": "First phase", "sessions": 2 }
              ],
              "agent": {
                "command": "opencode",
                "args": ["run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--thinking", "--format", "json", "{prompt}"],
                "output": "opencode-json"
              },
              "gates": [
                { "name": "build", "command": "dotnet build", "tier": "fast", "timeoutMinutes": 10 },
                { "name": "tests", "command": "dotnet test", "timeoutMinutes": 20 }
              ],
              "limits": { "stallMinutes": 12, "sessionTimeoutMinutes": 180, "stageSlackFactor": 2 },
              "report": { "commit": true, "push": true }
            }
            """,
            "node" => $$"""
            {
              "version": "1.0",
              "name": "{{name}}",
              "repo": "{{repoNormalised}}",
              "tracker": "TRACKER.md",
              "stages": [
                { "id": "S1", "title": "First phase", "sessions": 2 }
              ],
              "agent": {
                "command": "opencode",
                "args": ["run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--thinking", "--format", "json", "{prompt}"],
                "output": "opencode-json"
              },
              "gates": [
                { "name": "lint",  "command": "npm run lint",  "tier": "fast", "timeoutMinutes": 5 },
                { "name": "test",  "command": "npm test",       "timeoutMinutes": 15 },
                { "name": "build", "command": "npm run build",  "timeoutMinutes": 10 }
              ],
              "limits": { "stallMinutes": 12, "sessionTimeoutMinutes": 180, "stageSlackFactor": 2 },
              "report": { "commit": true, "push": true }
            }
            """,
            "shamshir" => $$"""
            {
              "version": "1.0",
              "name": "{{name}}",
              "repo": "{{repoNormalised}}",
              "tracker": "TRACKER.md",
              "conventions": {
                "stageIdPattern": "(?<stage>[A-Za-z]+-?\\d+)(?:\\.\\d+)?[a-z]?",
                "status": { "inProgress": ["IN PROGRESS"] }
              },
              "stages": [
                { "id": "P-0", "title": "Land the tree", "sessions": 2 },
                { "id": "P0",  "title": "First detail phase", "sessions": 2 },
                { "id": "P1",  "title": "Second phase", "sessions": 2 }
              ],
              "agent": {
                "command": "opencode",
                "args": ["run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--thinking", "--format", "json", "{prompt}"],
                "output": "opencode-json"
              },
              "gates": [
                { "name": "build", "command": "dotnet build", "tier": "fast", "timeoutMinutes": 10 },
                { "name": "tests", "command": "dotnet test", "timeoutMinutes": 20 }
              ],
              "limits": { "stallMinutes": 12, "sessionTimeoutMinutes": 180, "stageSlackFactor": 2 },
              "report": { "commit": true, "push": true }
            }
            """,
            _ => throw new ArgumentException($"Unknown template: {template}", nameof(template)),
        };
    }

    internal static string BuildTrackerMd(string template, string name)
    {
        // Checkpoints + handoff MUST match the stages declared in BuildPlanJson for this template,
        // otherwise the scaffold produces a plan whose stages own no rows and can never complete.
        // shamshir declares irregular stage ids (P-0/P0/P1); the others declare a single S1 stage.
        var (firstStage, conventionsNote, rows) = template == "shamshir"
            ? ("P-0",
               "\n\n> Conventions configured in the plan: irregular stage ids (`P-0`, `P0.1`, `P3.4b`, `F5`) supported.\n",
               "| P-0  | Land the tree           | TODO | | |\n" +
               "| P0.1 | First detail-phase task | TODO | | |\n" +
               "| P1.1 | Second-phase task       | TODO | | |")
            : ("S1",
               "",
               "| S1.1 | First task  | TODO | | |\n" +
               "| S1.2 | Second task | TODO | | |");

        return $$"""
        # {{name}} — Tracker (resume here)

        **Read order for a fresh session:** this file.{{conventionsNote}}
        ## Handoff  (overwrite this block, ≤12 lines, no history)
        last: (none) — scaffolded by conductor new-plan --template {{template}}.
        stage: **{{firstStage}} NOT STARTED**.
        gate: not yet run.
        next: **{{firstStage}}** — first checkpoint.

        ## Checkpoints

        Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path from a run this phase.

        | # | Checkpoint | Status | Commit | Evidence |
        |---|-----------|--------|--------|----------|
        {{rows}}
        """;
    }
}

/// <summary>
/// B11.2 — doctor: prints exactly what will happen on resume (pending fix/resume/phase-gate/audit/owner-gate).
/// Read-only; never writes state.
/// </summary>
public sealed class DoctorCommand : Command<PlanSettings>
{
    public override int Execute(CommandContext context, PlanSettings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);

        AnsiConsole.MarkupLine($"[bold aqua]conductor doctor[/] — {Markup.Escape(plan.Name)}");
        AnsiConsole.MarkupLine($"repo: {Markup.Escape(plan.Repo)}");
        AnsiConsole.MarkupLine($"branch: {Markup.Escape(Git.Branch(plan.Repo))}");
        AnsiConsole.MarkupLine($"state dir: {Markup.Escape(plan.StateDir)}");
        AnsiConsole.WriteLine();

        var statusColor = state.Status switch
        {
            RunStatus.Idle or RunStatus.Completed => "green",
            RunStatus.Running or RunStatus.VerifyingGates => "yellow",
            RunStatus.Backoff => "orange1",
            RunStatus.Paused or RunStatus.NeedsHuman or RunStatus.AwaitingOwner => "red",
            RunStatus.Aborted => "red",
            _ => "grey",
        };
        AnsiConsole.MarkupLine($"[bold]Status:[/] [{statusColor}]{Markup.Escape(state.Status.ToString())}[/]");
        AnsiConsole.MarkupLine($"[bold]Current stage:[/] {Markup.Escape(state.CurrentStage ?? "(none)")}");
        AnsiConsole.MarkupLine($"[bold]Session counter:[/] {state.SessionCounter}");
        AnsiConsole.MarkupLine($"[bold]Total cost:[/] ${state.TotalCostUsd:0.00}");

        if (state.AttentionReason is { } reason)
            AnsiConsole.MarkupLine($"[bold]Attention:[/] [red]{Markup.Escape(reason)}[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold aqua]On resume, this will happen:[/]");

        var step = 1;
        if (state.PendingFix is { } fix)
        {
            AnsiConsole.MarkupLine($"  {step++}. [yellow]Fix session[/] for stage {Markup.Escape(state.CurrentStage ?? "?")} (from session #{fix.FromSession})");
            AnsiConsole.MarkupLine($"      gates failed: {Markup.Escape(fix.GateFailures)}");
        }
        if (state.PendingResume is { } resume)
        {
            AnsiConsole.MarkupLine($"  {step++}. [yellow]Resume session[/] from session #{resume.FromSession} — {Markup.Escape(resume.Reason)}");
        }
        if (state.Status == RunStatus.AwaitingOwner)
        {
            var awaitReason = state.AwaitingOwnerReason?.ToString() ?? "OwnerGate";
            AnsiConsole.MarkupLine($"  {step++}. [green]Awaiting owner approval[/] for stage {Markup.Escape(state.CurrentStage ?? "?")} (reason: {Markup.Escape(awaitReason)})");
            AnsiConsole.MarkupLine($"      approve: conductor approve -p <plan>");
        }
        if (state.PendingPhaseGate is { } pg)
        {
            AnsiConsole.MarkupLine($"  {step++}. [yellow]Phase gate pending[/] for stage {Markup.Escape(pg.StageId)} — full battery will run");
        }
        if (state.PendingAudit is { } audit)
        {
            AnsiConsole.MarkupLine($"  {step++}. [yellow]Audit pending[/] for stage {Markup.Escape(audit.StageId)}");
        }

        if (step == 1)
        {
            AnsiConsole.MarkupLine($"  {step++}. Next session: deliver for stage {Markup.Escape(state.CurrentStage ?? "?")}");
        }

        // Remaining stages
        var track = SafeParseTracker(plan);
        var remaining = plan.Stages
            .Where(s =>
            {
                if (state.SkippedStages.Contains(s.Id)) return false;
                if (state.ConfirmedStages.Contains(s.Id)) return false;
                if (track != null)
                {
                    var rows = track.ForStage(s.Id).ToList();
                    if (rows.Count == 0) return true;
                    return !rows.All(r => r.IsDone);
                }
                return true;
            })
            .Select(s => s.Id)
            .ToList();

        if (remaining.Count > 0)
            AnsiConsole.MarkupLine($"  {step}. [grey]Remaining stages:[/] {string.Join(" → ", remaining)}");
        else
            AnsiConsole.MarkupLine($"  {step}. [green]All stages complete[/]");

        return 0;
    }

    private static TrackerSnapshot? SafeParseTracker(PlanConfig plan)
    {
        try { return TrackerParser.ParseFile(plan.TrackerPath); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[grey]Note:[/] could not parse tracker at {Markup.Escape(plan.TrackerPath)} — {Markup.Escape(ex.GetType().Name)}. Shown remaining stages are state-based only.");
            return null;
        }
    }
}

/// <summary>
/// P5 — Post-hoc audit replay. Runs a read-only audit prompt against a completed stage,
/// capturing the output to <c>.conductor/audits/&lt;stage&gt;-replay-&lt;timestamp&gt;.md</c>.
/// Never modifies RunState. Use --replay to enable replay mode; the agent reviews the
/// stage's checkpoints, git history, evidence artifacts, and design context.
/// </summary>
public sealed class AuditCommand : Command<AuditCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "<STAGE>")]
        [Description("Stage ID to replay-audit (e.g., D1, P4).")]
        public string Stage { get; init; } = "";

        [CommandOption("--replay")]
        [Description("Run as a read-only diagnostic audit replay — does not affect RunState.")]
        public bool Replay { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var stageId = settings.Stage.Trim();
        if (string.IsNullOrWhiteSpace(stageId))
        {
            AnsiConsole.MarkupLine("[red]A stage id is required.[/] Usage: conductor audit <STAGE> --replay");
            return 1;
        }

        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var track = TrackerParser.ParseFile(plan.TrackerPath);

        // Find the stage config
        var stage = plan.Stages.Find(s => s.Id.Equals(stageId, StringComparison.OrdinalIgnoreCase));
        if (stage == null)
        {
            AnsiConsole.MarkupLine($"[red]Stage '{Markup.Escape(stageId)}' not found in the plan.[/]");
            return 1;
        }
        stageId = stage.Id; // canonical casing

        if (!settings.Replay)
        {
            AnsiConsole.MarkupLine("[yellow]Use --replay to run a post-hoc audit replay. Without --replay, this command is a no-op (regular audits are orchestrated, not CLI-driven).[/]");
            return 0;
        }

        // Gather stage context
        var rows = track.ForStage(stageId).ToList();
        var doneCount = rows.Count(r => r.IsDone);
        var totalCkForStage = rows.Count;

        // Git history: recent log bounded by this branch's scope
        var gitLog = "";
        try
        {
            var branch = Git.Branch(plan.Repo);
            var logResult = ProcessRunner.Run("git", new[] { "-C", plan.Repo, "log", "-n", "20", "--format=%h %s (%an, %ar)", "--no-decorate", "--no-merges" },
                plan.Repo, TimeSpan.FromSeconds(10));
            gitLog = string.IsNullOrWhiteSpace(logResult.Output) ? "(no commits)" : logResult.Output.Trim();
        }
        catch
        {
            gitLog = "(git failed)";
        }

        // Build evidence tail: read the stage's evidence files if any
        var evidenceTail = "";
        var evidenceDir = Path.Combine(plan.StateDir, "..", "docs", "era3", "evidence", stageId);
        try
        {
            if (Directory.Exists(evidenceDir))
            {
                var evidenceFiles = Directory.EnumerateFiles(evidenceDir, "*.txt", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .Take(3)
                    .ToList();
                foreach (var ef in evidenceFiles)
                {
                    var content = File.ReadAllText(ef);
                    if (content.Length > 4000) content = content[..4000] + "\n…(truncated)";
                    evidenceTail += $"## Evidence: {Path.GetFileName(ef)}\n```\n{content}\n```\n\n";
                }
            }
        }
        catch (IOException) { /* best-effort */ }

        // Build the replay audit prompt
        var prompt = BuildReplayPrompt(plan.Name, stage, rows, doneCount, totalCkForStage, gitLog, evidenceTail, state.SessionCounter);
        AnsiConsole.MarkupLine($"[bold aqua]conductor audit replay[/] — stage [bold]{Markup.Escape(stageId)}[/] ({doneCount}/{totalCkForStage} checkpoints DONE)");
        AnsiConsole.MarkupLine($"[grey]Prompt length: {prompt.Length} chars. Running agent…[/]");
        AnsiConsole.WriteLine();

        // Run the agent (read-only, in a scratch dir)
        var result = RunAgent(plan.Agent, prompt, TimeSpan.FromMinutes(30));
        var outputDir = Path.Combine(plan.StateDir, "audits");
        Directory.CreateDirectory(outputDir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outputPath = Path.Combine(outputDir, $"{stageId}-replay-{timestamp}.md");
        File.WriteAllText(outputPath, result, System.Text.Encoding.UTF8);

        AnsiConsole.WriteLine(result);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Audit replay written to[/] [bold]{Markup.Escape(outputPath)}[/]");

        return 0;
    }

    private static string BuildReplayPrompt(string planName, StageConfig stage, List<CheckpointRow> rows,
        int doneCount, int totalCk, string gitLog, string evidenceTail, int sessionCounter)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are a post-hoc AUDIT REPLAY session for the \"{planName}\" mega plan.");
        sb.AppendLine();
        sb.AppendLine("The following stage is complete (all checkpoints DONE). Review what was built and");
        sb.AppendLine("provide an honest, critical post-hoc diagnostic assessment.");
        sb.AppendLine("Do NOT modify files or run commands. This is a READ-ONLY diagnostic.");
        sb.AppendLine();
        sb.AppendLine($"## Stage: {stage.Id} — {stage.Title}");
        if (!string.IsNullOrWhiteSpace(stage.Notes))
            sb.AppendLine($"Notes: {stage.Notes}");
        sb.AppendLine($"Checkpoints: {doneCount}/{totalCk} DONE");
        sb.AppendLine($"Total sessions across the entire plan so far: {sessionCounter}");
        sb.AppendLine();
        sb.AppendLine("### Checkpoints");
        foreach (var r in rows)
            sb.AppendLine($"- {r.Id} [{r.Status}] {r.Title}" +
                (r.Commit != null ? $" (commit: {r.Commit})" : "") +
                (r.Evidence != null ? $" Evidence: {r.Evidence}" : ""));
        sb.AppendLine();
        sb.AppendLine("### Recent Git History");
        sb.AppendLine("```");
        sb.AppendLine(gitLog);
        sb.AppendLine("```");
        sb.AppendLine();
        if (evidenceTail.Length > 0)
            sb.Append(evidenceTail);
        sb.AppendLine("## Instructions");
        sb.AppendLine("Write a comprehensive but terse diagnostic audit covering:");
        sb.AppendLine("1. **What was built** — factual summary of changes and deliverables.");
        sb.AppendLine("2. **Correctness** — bugs, risks, edge cases, regressions you spot.");
        sb.AppendLine("3. **Code quality** — patterns, conventions, duplication, maintainability.");
        sb.AppendLine("4. **Testing** — coverage assessment, gaps, brittle tests.");
        sb.AppendLine("5. **Risks and followups** — what could bite later, concrete improvements.");
        sb.AppendLine("6. **Verdict** — HONEST one-line assessment: SOLID / GOOD / ADEQUATE / WEAK.");
        sb.AppendLine();
        sb.AppendLine("Be critical. Don't oversell. If something looks thin, stubbed, or shortcut, say so plainly.");
        sb.AppendLine("End with a one-line verdict line starting with VERDICT:");
        return sb.ToString();
    }

    private static string RunAgent(AgentConfig cfg, string prompt, TimeSpan timeout)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "conductor-audit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            var args = cfg.Args.Select(a => a.Replace("{prompt}", prompt)).ToList();
            // If a model arg exists, ensure it's set; defaults from plan config are fine.
            var r = ProcessRunner.Run(cfg.Command, args, scratch, timeout);
            var text = r.Output.Trim();
            if (r.TimedOut) text += $"\n\n(audit agent timed out after {timeout.TotalMinutes:0} minutes)";
            if (!string.IsNullOrWhiteSpace(r.StdErr)) text += $"\n\n--- stderr ---\n{r.StdErr.Trim()}";
            return string.IsNullOrWhiteSpace(text)
                ? $"(audit agent produced no output — exit {r.ExitCode})"
                : text;
        }
        catch (Exception ex) { return $"(audit agent failed: {ex.Message})"; }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

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
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var eventsPath = Path.GetFullPath(settings.Events);
        var journalPath = Path.GetFullPath(settings.Journal);

        var server = new McpTaskServer(eventsPath, journalPath, settings.RunId);
        server.Init();
        server.FoldJournal();

        using var cts = new CancellationTokenSource();
#pragma warning disable MA0045
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
#pragma warning restore MA0045
        server.RunAsync(Console.In, Console.Out, cts.Token).GetAwaiter().GetResult();
        return 0;
    }
}

public sealed class CompletionCommand : Command<CompletionCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SHELL>")]
        [Description("Target shell: powershell or bash")]
        public string Shell { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var shell = settings.Shell.ToLowerInvariant();
        return shell switch
        {
            "powershell" => WritePowerShellCompletion(),
            "bash" => WriteBashCompletion(),
            _ => WriteUsageError(),
        };
    }

    private static int WritePowerShellCompletion()
    {
        Console.WriteLine(GeneratePowerShell());
        return 0;
    }

    internal static string GeneratePowerShell()
    {
        var verbs = "run status gate log report replay preview pause resume approve kill skip inject abort retry-stage rollback pause-after-stage goto heartbeat plan tasks new-plan doctor audit mcp-serve completion";
        var opts = "-p --plan --yes --force --dry-run --once --max-sessions --no-dashboard -o --output --name --repo -q --query --since --tail";
        var auditOpts = "-p --plan --replay";
        var newPlanOpts = "--template -o --output --name --repo";
        return $$"""
            # conductor tab completion for PowerShell — generated by 'conductor completion powershell'
            # Source: conductor completion powershell | Invoke-Expression
            # Or save to a file and dot-source in $PROFILE: conductor completion powershell > conductor-completion.ps1

            Register-ArgumentCompleter -Native -CommandName conductor -ScriptBlock {
                param($wordToComplete, $commandAst, $cursorPosition)
                $verbs = @('{{verbs}}' -split ' ')
                $opts = @('{{opts}}' -split ' ')
                $auditOpts = @('{{auditOpts}}' -split ' ')
                $newPlanOpts = @('{{newPlanOpts}}' -split ' ')
                $tokens = $commandAst.ToString() -split '\s+' | Where-Object { $_ }
                if ($tokens.Count -eq 1) {
                    $verbs | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
                    }
                }
                elseif ($tokens[1] -eq 'new-plan') {
                    $newPlanOpts | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterName', $_)
                    }
                }
                elseif ($tokens[1] -eq 'completion') {
                    @('powershell','bash') | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
                    }
                }
                elseif ($tokens[1] -eq 'plan') {
                    @('set','reload','add-stage') | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
                    }
                }
                elseif ($tokens[1] -eq 'audit') {
                    $auditOpts | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterName', $_)
                    }
                }
                elseif ($tokens[1] -in @('run','status','gate','log','report','replay','preview','pause','resume',
                        'approve','kill','skip','inject','abort','retry-stage','rollback','pause-after-stage',
                        'goto','heartbeat','tasks','doctor')) {
                    $opts | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterName', $_)
                    }
                }
            }
            """;
    }

    private static int WriteBashCompletion()
    {
        Console.WriteLine(GenerateBash());
        return 0;
    }

    internal static string GenerateBash()
    {
        return """
            # conductor tab completion for bash — generated by 'conductor completion bash'
            # Source: source <(conductor completion bash)

            _conductor_completion() {
                local cur prev words cword
                _init_completion || return
                COMPREPLY=()
                cur="${COMP_WORDS[COMP_CWORD]}"

                if [[ $COMP_CWORD -eq 1 ]]; then
                    COMPREPLY=($(compgen -W "run status gate log report replay preview audit mcp-serve pause resume approve kill skip inject abort retry-stage rollback pause-after-stage goto heartbeat plan tasks new-plan doctor completion" -- "$cur"))
                    return
                fi
                case "${COMP_WORDS[1]}" in
                    run|status|gate|log|report|replay|preview|pause|resume|approve|kill|skip|inject|abort|retry-stage|rollback|pause-after-stage|goto|heartbeat|tasks|doctor|mcp-serve)
                        COMPREPLY=($(compgen -W "-p --plan --yes --force --dry-run --once --max-sessions --no-dashboard -o --output --name --repo" -- "$cur"))
                        ;;
                    audit)
                        COMPREPLY=($(compgen -W "-p --plan --replay" -- "$cur"))
                        ;;
                    completion)
                        COMPREPLY=($(compgen -W "powershell bash" -- "$cur"))
                        ;;
                    plan)
                        COMPREPLY=($(compgen -W "set reload add-stage" -- "$cur"))
                        ;;
                    new-plan)
                        COMPREPLY=($(compgen -W "--template -o --output --name --repo" -- "$cur"))
                        ;;
                esac
            }
            complete -F _conductor_completion conductor
            """;
    }

    private static int WriteUsageError()
    {
        AnsiConsole.MarkupLine("[red]Unknown shell.[/] Valid: powershell, bash.");
        AnsiConsole.MarkupLine("Usage: conductor completion <powershell|bash>");
        return 1;
    }
}
