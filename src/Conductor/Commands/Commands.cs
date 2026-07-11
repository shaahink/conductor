using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Face;
using Conductor.Core.Hosting;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Models;

using EventLog = Conductor.Core.Events.EventLog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        [CommandOption("--headless")]
        [Description("No TUI: plain line output in this terminal. The control plane still runs, so a Face can attach later.")]
        public bool Headless { get; init; }

        [CommandOption("--no-face")]
        [Description("Run the control plane but do not spawn the Face TUI (attach your own: `conductor face`).")]
        public bool NoFace { get; init; }

        [CommandOption("--no-control-plane")]
        [Description("Disable the localhost HTTP+SSE control plane entirely. Implies --headless (the Face needs it).")]
        public bool NoControlPlane { get; init; }

        [CommandOption("--port <PORT>")]
        [Description("Preferred control-plane port (default 4317). If taken, the next free port is used — concurrent runs never collide.")]
        public int ControlPlanePort { get; init; } = 4317;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        Directory.CreateDirectory(plan.StateDir);
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        if (string.IsNullOrEmpty(state.RunId)) state.RunId = Guid.NewGuid().ToString("N");

        // `conductor run` is ONE command: engine + control plane + Face TUI, one process tree. The plain
        // (headless) path exists for CI, dry runs and redirected output — it is no longer the way you get
        // a UI, and there is no second terminal to start.
        var controlPlane = !settings.NoControlPlane && !settings.DryRun;
        var wantFace = controlPlane
                       && !settings.Headless
                       && !settings.NoFace
                       && !settings.DryRun
                       && !Console.IsOutputRedirected;

        var opts = new RunOptions(settings.DryRun, settings.Once, settings.MaxSessions, controlPlane, settings.ControlPlanePort);
        using var cts = new CancellationTokenSource();
#pragma warning disable MA0045 // CancelAsync doesn't exist on CancellationTokenSource
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
#pragma warning restore MA0045

        // Event log is additive alongside state.json (B2.1). A dry-run previews without writing.
        IEventSink events = settings.DryRun
            ? NullEventSink.Instance
            : new EventLog(Path.Combine(plan.StateDir, "events.jsonl"), state.RunId);
        FaceLauncher.FaceHandle? face = null;
        try
        {
            // When the Face owns the terminal, the engine's console sink must stay off or the two
            // interleave and corrupt the render. Everything still goes to .conductor/logs/.
            using var host = ConductorHost.Build(plan, state, statePath, new PlainSink(), events, opts, consoleSink: !wantFace);

            var server = host.Services.GetService<ControlPlaneServer>();
            var bound = server?.Start() == true; // never fatal: a bind failure just means no clients

            if (wantFace && bound)
            {
                face = FaceLauncher.Start(
                    $"http://127.0.0.1:{server!.Port}",
                    host.Services.GetRequiredService<ILogger<RunCommand>>(),
                    host.Services.GetService<ProcessSupervisor>());
            }

#pragma warning disable MA0045 // sync-over-async boundary: Spectre.Cli Execute must return int
            return host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token).GetAwaiter().GetResult();
#pragma warning restore MA0045
        }
        finally
        {
            // The Face is disposable: tearing it down can never affect the run's outcome, already decided above.
            face?.Dispose();
            (events as IDisposable)?.Dispose();
        }
    }
}

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
            AnsiConsole.MarkupLine("[red]error:[/] no built Face found. Run [yellow]npm install && npm run build[/] in [yellow]face/[/].");
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

        var psi = new ProcessStartInfo("node") { UseShellExecute = false };
        psi.ArgumentList.Add(entry);
        if (settings.Demo) psi.ArgumentList.Add("--demo");
        else { psi.ArgumentList.Add("--url"); psi.ArgumentList.Add(url); }

        using var proc = Process.Start(psi);
        if (proc is null) return 1;
        proc.WaitForExit();
        return proc.ExitCode;
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

#pragma warning disable MA0045, CA1849 // short CLI-boundary helper, no concurrent async work to protect (same category as Spectre.Cli sync boundary)
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
#pragma warning restore MA0045, CA1849

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
        var gates = GateRunner.RunAllAsync(plan, msg => LogGateEvent(logPath, msg), ct, fastOnly, state.CurrentStage, null)
            .GetAwaiter().GetResult();

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
                    "deliver" => "[grey]deliver[/]",
                    "planner" => "[grey]deliver[/]",
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

public sealed class ReportCommand : Command<ReportCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--query <SQL>")]
        [Description("F1.4: Run an ad-hoc SQL query against run.db instead of generating REPORT.md. " +
                     "Example: --query \"SELECT stage_id, SUM(cost_usd) FROM costs GROUP BY stage_id\"")]
        public string? Query { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());

        if (settings.Query != null)
        {
            return RunQuery(plan, settings.Query);
        }

        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var track = TrackerParser.ParseFile(plan.TrackerPath);
        Directory.CreateDirectory(plan.StateDir);
        File.WriteAllText(Reporter.ReportPath(plan), Reporter.Build(plan, state, track, null, null,
            Reporter.ReadTimeline(plan), Reporter.ReadHealth(plan),
            mcp: Reporter.ReadMcpMetrics(plan),
            repo: Reporter.ReadRepoStrip(plan)), Reporter.Utf8Bom);
        AnsiConsole.MarkupLine($"report written to [bold]{Markup.Escape(Reporter.ReportPath(plan))}[/]");
        return 0;
    }

    private static int RunQuery(PlanConfig plan, string sql)
    {
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (!File.Exists(runDbPath))
        {
            AnsiConsole.MarkupLine("[red]No run.db found.[/] Run the conductor at least once to initialize the database.");
            return 1;
        }

        try
        {
            using var db = new RunDb(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance);
            var rows = db.Query(sql);

            if (rows.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]no rows returned[/]");
                return 0;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[bold aqua]Query result[/] ({rows.Count} row{(rows.Count == 1 ? "" : "s")})");

            var columns = rows[0].Keys;
            foreach (var col in columns)
                table.AddColumn(Markup.Escape(col));

            foreach (var row in rows)
            {
                var values = columns.Select(c =>
                {
                    var v = row.GetValueOrDefault(c);
                    return v switch
                    {
                        null => "[grey]-[/]",
                        double d => d.ToString("F4"),
                        float f => f.ToString("F4"),
                        long l => l.ToString(),
                        int i => i.ToString(),
                        _ => Markup.Escape(v.ToString()!)
                    };
                }).ToArray();
                table.AddRow(values);
            }

            AnsiConsole.Write(table);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Query failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
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
            "import" => PlanImportCommand.ExecuteImport(settings.ResolvePlanPath(), settings.Key),
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

/// <summary>
/// F7.1: Plan import — an LLM pass (advisor model) converts a natural-language mega-plan description
/// into structured stages, gates, and checkpoints in the plan JSON. Usage: conductor plan import
/// <description-file.md|"free-text description">
/// </summary>
public static class PlanImportCommand
{
    public static int ExecuteImport(string planPath, string? descriptionOrFile)
    {
        if (string.IsNullOrWhiteSpace(descriptionOrFile))
        {
            AnsiConsole.MarkupLine("[red]plan import requires a description (file path or quoted text).[/]");
            AnsiConsole.MarkupLine("[grey]Example: conductor plan import ./MEGA-PLAN.md[/]");
            AnsiConsole.MarkupLine("[grey]Example: conductor plan import \"deliver a REST API — stage 1: auth, stage 2: endpoints\"[/]");
            return 1;
        }

        try
        {
            var plan = PlanConfig.Load(planPath);
            if (plan.Advisor is not { Enabled: true } || string.IsNullOrWhiteSpace(plan.Advisor.Command))
            {
                AnsiConsole.MarkupLine("[red]Advisor model is not configured. Set advisor.enabled, advisor.command, and advisor.args in your plan.[/]");
                return 1;
            }

            var description = descriptionOrFile;
            // If the argument looks like a file path and exists, read it
            if (File.Exists(descriptionOrFile))
            {
                description = File.ReadAllText(descriptionOrFile, System.Text.Encoding.UTF8);
                AnsiConsole.MarkupLine($"[grey]Read description from {Markup.Escape(descriptionOrFile)} ({description.Length} chars)[/]");
            }

            AnsiConsole.MarkupLine("[grey]Consulting advisor model to generate task graph...[/]");

            var result = PlanImportService.ImportAsync(plan, description, msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]"))
                .GetAwaiter().GetResult();

            if (result == null)
            {
                AnsiConsole.MarkupLine("[red]Plan import failed — the advisor model could not generate a valid task graph.[/]");
                AnsiConsole.MarkupLine("[grey]Check that the advisor command is working (try: conductor chat \"hello\") and that the description is clear.[/]");
                return 1;
            }

            // Show a preview
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold aqua]Generated plan:[/] {result.Stages.Count} stages, {result.Gates.Count} gates");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("Id");
            table.AddColumn("Title");
            table.AddColumn("Sessions");
            table.AddColumn("Kind");
            table.AddColumn("Depends On");
            foreach (var stage in result.Stages)
            {
                table.AddRow(
                    Markup.Escape(stage.Id),
                    Markup.Escape(stage.Title ?? ""),
                    stage.Sessions.ToString(),
                    stage.Kind ?? "deliver",
                    stage.DependsOn is { Count: > 0 } ? Markup.Escape(string.Join(", ", stage.DependsOn)) : "-");
            }
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            if (result.Gates.Count > 0)
            {
                AnsiConsole.MarkupLine("[bold]Gates:[/]");
                foreach (var gate in result.Gates)
                    AnsiConsole.MarkupLine($"  {Markup.Escape(gate.Name)}: {Markup.Escape(gate.Command ?? "")} (tier={gate.Tier})");
            }

            // Confirm
            if (!AnsiConsole.Confirm("[yellow]Apply these stages and gates to the plan?[/]", false))
            {
                AnsiConsole.MarkupLine("[grey]Import cancelled.[/]");
                return 0;
            }

            PlanImportService.ApplyToPlan(plan, result);
            AnsiConsole.MarkupLine($"[green]Plan updated:[/] {result.Stages.Count} stages, {result.Gates.Count} gates added/merged");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or IOException)
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

/// <summary>Scaffolds a new plan + TRACKER.md (B1.6).</summary>
public sealed class NewPlanCommand : Command<NewPlanCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-o|--output <DIR>")]
        [Description("Directory to create the files in. Created if missing. Default: cwd.")]
        public string? Output { get; init; }

        [CommandOption("--name <NAME>")]
        [Description("Plan name. Default: directory name or 'plan'.")]
        public string? Name { get; init; }

        [CommandOption("--repo <PATH>")]
        [Description("Absolute path to the repo. Default: output directory.")]
        public string? Repo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
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

        File.WriteAllText(planPath, BuildMinimalPlanJson(name, repo), System.Text.Encoding.UTF8);
        File.WriteAllText(trackerPath, BuildMinimalTrackerMd(name), System.Text.Encoding.UTF8);

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
        return 0;
    }

    internal static string BuildMinimalPlanJson(string name, string repo)
    {
        var repoNormalised = repo.Replace("\\", "/");
        return $$"""
        {
          "version": "1.0",
          "name": "{{name}}",
          "repo": "{{repoNormalised}}",
          "tracker": "TRACKER.md",
          "agent": {
            "command": "opencode",
            "args": ["run", "{prompt}"],
            "provider": "opencode"
          },
          "stages": []
        }
        """;
    }

    internal static string BuildMinimalTrackerMd(string name)
    {
        return $$"""
        # {{name}} — TRACKER

        ## Handoff
        last: none. Status: idle.

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

#pragma warning disable MA0045, CA1849 // CLI --replay sync boundary, no concurrent async work to protect (same category as Spectre.Cli sync boundary)
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
#pragma warning restore MA0045, CA1849
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

        [CommandOption("--state-dir <path>")]
        [Description("Plan state directory for bg tools (e.g. .conductor/). Optional.")]
        public string? StateDir { get; init; }

        [CommandOption("--repo <path>")]
        [Description("Repo root for bg_start working directory. Optional.")]
        public string? Repo { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var eventsPath = Path.GetFullPath(settings.Events);
        var journalPath = Path.GetFullPath(settings.Journal);

        // F1.3: wire run.db if it exists so conductor_note MCP tool works
        var runDbPath = Path.Combine(Path.GetDirectoryName(eventsPath) ?? ".conductor", "run.db");
        RunDb? runDb = null;
        if (File.Exists(runDbPath))
        {
            try { runDb = new RunDb(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance); }
            catch { /* best-effort — MCP works without run.db */ }
        }

        var stateDir = settings.StateDir ?? Path.GetDirectoryName(eventsPath);
        var repoPath = settings.Repo ?? (stateDir != null ? Path.GetDirectoryName(stateDir) : null);

        var server = new McpTaskServer(eventsPath, journalPath, settings.RunId, runDb, stateDir, repoPath);
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
            runDb?.Dispose();
        }
        return 0;
    }
}

/// <summary>
/// F8.1: Spawns a conductor chat agent — an LLM wired (MCP) to run.db, the ledger, logs, and
/// control verbs. The agent answers ad-hoc questions about the run and can perform actions
/// (inject instructions, add notes, update tasks). Usage: conductor chat "how did s9 die?"
/// or conductor chat "inject X into retry for F6"
/// </summary>
public sealed class ChatCommand : Command<ChatCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[QUERY]")]
        [Description("Your question about the run. Leave blank for interactive mode.")]
        public string? Query { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        try
        {
            var plan = PlanConfig.Load(settings.ResolvePlanPath());
            if (plan.Advisor is not { Enabled: true } || string.IsNullOrWhiteSpace(plan.Advisor.Command))
            {
                AnsiConsole.MarkupLine("[red]Advisor model is not configured. Set advisor.enabled, advisor.command, and advisor.args in your plan.[/]");
                return 1;
            }

            var query = settings.Query?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(query))
            {
                AnsiConsole.MarkupLine("[bold aqua]conductor chat[/] — ask questions about your run");
                AnsiConsole.MarkupLine("[grey]Type your question and press Enter. Leave blank to exit.[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.Write("> ");
                query = Console.ReadLine()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(query))
                {
                    AnsiConsole.MarkupLine("[grey]No question — exiting.[/]");
                    return 0;
                }
            }

            return ExecuteChatAsync(plan, query).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static async Task<int> ExecuteChatAsync(PlanConfig plan, string query)
    {
        AnsiConsole.MarkupLine("[grey]Consulting advisor model...[/]");
        AnsiConsole.WriteLine();

        // Build a chat prompt with the user's query
        var promptBuilder = new PromptBuilder(plan);
        var stage = new StageConfig { Id = "chat", Title = "Conductor Chat", Kind = "deliver", Sessions = 1 };
        promptBuilder.Deliver(stage, 0, 1, 1); // warm the builder — we need the BuiltIn access pattern

        // Construct the prompt manually with the chat template
        var readOrder = plan.ReadOrder is { Count: > 0 }
            ? string.Join("\n", plan.ReadOrder.Select((d, i) => $"{i + 1}. {d}"))
            : "(no read order configured)";

        var prompt = $"""
            System: You are a helpful engineering analyst that answers questions about the "{plan.Name}" conductor run. You have access to MCP tools (run.db SQL querying, session detail lookup, ledger entries, task management, bg process control). Be concise and data-driven.

            Context:
            - Repo: {plan.Repo}
            - Tracker file: {plan.Tracker}
            - The run.db is at: .conductor/run.db relative to the repo root
            - Use the `run_query` MCP tool to execute SQL queries against run.db
            - Use `session_detail` to look up specific session info
            - Use `ledger_list` to see recent findings
            - Use `inject_instruction` to write an instruction if asked

            USER QUERY: {query}
            """;

        // Get the advisor config
        var advisorCfg = plan.Advisor!;
        var args = advisorCfg.Args.Select(a => a.Replace("{prompt}", prompt)).ToList();

        AnsiConsole.MarkupLine($"[grey]Running: {Markup.Escape(advisorCfg.Command)} {Markup.Escape(string.Join(" ", args))}[/]");
        AnsiConsole.WriteLine();

        var r = await ProcessRunner.RunAsync(advisorCfg.Command, args, plan.Repo,
            TimeSpan.FromMinutes(advisorCfg.TimeoutMinutes)).ConfigureAwait(false);

        if (r.TimedOut)
        {
            AnsiConsole.MarkupLine("[red]Chat agent timed out.[/]");
            return 1;
        }

        // Output the agent's raw response
        var output = r.Output.Trim();
        if (advisorCfg.Output.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(output);
                if (doc.RootElement.TryGetProperty("result", out var res) && res.ValueKind == System.Text.Json.JsonValueKind.String)
                    output = res.GetString() ?? output;
            }
            catch (System.Text.Json.JsonException) { /* print raw */ }
        }

        if (output.Length > 0)
            AnsiConsole.WriteLine(output);
        else
            AnsiConsole.MarkupLine("[grey](agent produced no output)[/]");

        if (!string.IsNullOrWhiteSpace(r.StdErr))
            AnsiConsole.MarkupLine($"[grey](stderr: {r.StdErr.Trim()})[/]");

        return r.ExitCode == 0 ? 0 : 1;
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
        var verbs = "run status gate log report preview pause resume approve kill skip inject abort retry-stage rollback pause-after-stage goto plan tasks task new-plan note doctor audit mcp-serve completion chat bg";
        var opts = "-p --plan --yes --force --dry-run --once --max-sessions --no-dashboard -o --output --name --repo -q --query --since --tail";
        var auditOpts = "-p --plan --replay";
        var newPlanOpts = "-o --output --name --repo";
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
                elseif ($tokens[1] -in @('run','status','gate','log','report','preview','pause','resume',
                        'approve','kill','skip','inject','abort','retry-stage','rollback','pause-after-stage',
                        'goto','tasks','task','note','doctor')) {
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
                    COMPREPLY=($(compgen -W "run status gate log report preview audit mcp-serve pause resume approve kill skip inject abort retry-stage rollback pause-after-stage goto plan tasks task new-plan note doctor completion chat bg" -- "$cur"))
                    return
                fi
                case "${COMP_WORDS[1]}" in
                    run|status|gate|log|report|preview|pause|resume|approve|kill|skip|inject|abort|retry-stage|rollback|pause-after-stage|goto|tasks|task|note|doctor|mcp-serve|chat|bg)
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
                        COMPREPLY=($(compgen -W "-o --output --name --repo" -- "$cur"))
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

/// <summary>
/// F1.3: Write a finding/observation to the knowledge ledger (run.db ledger table).
/// Agents call this via CLI or MCP to persist discoveries immediately instead of
/// only at session end — kills the "stall destroys knowledge" failure (design doc §3.3).
/// </summary>
public sealed class NoteCommand : Command<NoteCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("-k|--kind <KIND>")]
        [Description("Ledger entry kind: finding, observation, trap, decision. Default: note.")]
        public string? Kind { get; init; }

        [CommandOption("-s|--stage <STAGE>")]
        [Description("Stage id to associate the note with (e.g. F1). Optional.")]
        public string? Stage { get; init; }

        [CommandArgument(0, "<TEXT>")]
        [Description("The note content.")]
        public string Text { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (!File.Exists(runDbPath))
        {
            AnsiConsole.MarkupLine("[red]No run.db found.[/] Run the conductor at least once to initialize the database.");
            return 1;
        }

        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var kind = string.IsNullOrWhiteSpace(settings.Kind) ? "note" : settings.Kind;

        try
        {
            using var db = OpenRunDb(runDbPath);
            db.WriteLedger(state.RunId, state.SessionCounter > 0 ? state.SessionCounter : null,
                settings.Stage ?? state.CurrentStage, kind, settings.Text);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Note write failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        AnsiConsole.MarkupLine($"[green]note written:[/] [{Markup.Escape(kind)}] {Markup.Escape(settings.Text)}");
        return 0;
    }

    private static RunDb OpenRunDb(string path)
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance;
        try
        {
            return new RunDb(path, logger);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to open run.db at {path}: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// F1.3: Task/checkpoint CRUD — agents report progress via CLI verbs instead of hand-editing
/// the tracker markdown. Writes go to the run.db checkpoints table; the tracker regenerates from
/// that source of truth (F1.2 tracker-as-view).
/// </summary>
public sealed class TaskCommand : Command<TaskCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--done <CHECKPOINT>")]
        [Description("Mark a checkpoint as DONE.")]
        public string? Done { get; init; }

        [CommandOption("--in-progress <CHECKPOINT>")]
        [Description("Mark a checkpoint as IN PROGRESS (from TODO only).")]
        public string? InProgress { get; init; }

        [CommandOption("--list")]
        [Description("List all checkpoints from run.db.")]
        public bool List { get; init; }

        [CommandOption("-c|--commit <SHA>")]
        [Description("Commit SHA to attribute (for --done).")]
        public string? Commit { get; init; }

        [CommandOption("-e|--evidence <TEXT>")]
        [Description("Evidence string (for --done).")]
        public string? Evidence { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (!File.Exists(runDbPath))
        {
            AnsiConsole.MarkupLine("[red]No run.db found.[/] Run the conductor at least once to initialize the database.");
            return 1;
        }

        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        if (string.IsNullOrEmpty(state.RunId))
        {
            AnsiConsole.MarkupLine("[red]state.json has no RunId.[/] Initialize the run first (conductor run --dry-run or run at least one session).");
            return 1;
        }

        try
        {
            using var db = new RunDb(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance);

            if (settings.Done != null)
            {
                var allCps = db.GetCheckpoints(state.RunId);
                if (!allCps.Any(c => c.Id.Equals(settings.Done, StringComparison.OrdinalIgnoreCase)))
                {
                    AnsiConsole.MarkupLine($"[red]Checkpoint '{Markup.Escape(settings.Done)}' not found in run.db[/]");
                    return 1;
                }
                db.UpdateCheckpoint(state.RunId, settings.Done, "DONE",
                    settings.Commit ?? "-", settings.Evidence ?? "marked done via CLI");
                AnsiConsole.MarkupLine($"[green]checkpoint {Markup.Escape(settings.Done)} → DONE[/]");
            }
            else if (settings.InProgress != null)
            {
                var allCps = db.GetCheckpoints(state.RunId);
                if (!allCps.Any(c => c.Id.Equals(settings.InProgress, StringComparison.OrdinalIgnoreCase)))
                {
                    AnsiConsole.MarkupLine($"[red]Checkpoint '{Markup.Escape(settings.InProgress)}' not found in run.db[/]");
                    return 1;
                }
                db.MarkCheckpointInProgress(state.RunId, settings.InProgress);
                AnsiConsole.MarkupLine($"[yellow]checkpoint {Markup.Escape(settings.InProgress)} → IN PROGRESS[/]");
            }
            else if (settings.List)
            {
                var cps = db.GetCheckpoints(state.RunId);

                if (cps.Count == 0)
                {
                    AnsiConsole.MarkupLine("[grey]no checkpoints in run.db[/]");
                    return 0;
                }

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .Title("[bold aqua]Checkpoints from run.db[/]")
                    .AddColumn("Stage")
                    .AddColumn("ID")
                    .AddColumn("Title")
                    .AddColumn("Status");

                foreach (var cp in cps)
                {
                    var icon = cp.Status switch
                    {
                        var s when s.StartsWith("DONE", StringComparison.OrdinalIgnoreCase) => "[green]DONE[/]",
                        var s when s.StartsWith("IN", StringComparison.OrdinalIgnoreCase) => "[yellow]IN PROG[/]",
                        var s when s.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase) => "[red]BLOCKED[/]",
                        _ => "[grey]TODO[/]",
                    };
                    table.AddRow(Markup.Escape(cp.StageId), Markup.Escape(cp.Id), Markup.Escape(cp.Title), icon);
                }
                AnsiConsole.Write(table);
            }
            else
            {
                AnsiConsole.MarkupLine("[grey]Usage: conductor task --list | --done <id> | --in-progress <id>[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        return 0;
    }
}

/// <summary>
/// F2.3: Background process management — sanctioned primitive for agents to run commands
/// that take >3 min. Agents call <c>conductor bg start|status|logs|stop</c> via CLI or MCP
/// to spawn, monitor, and kill long-running child processes without blocking the session.
/// Every bg process is tracked in the run.db pids table and its stdout/stderr are captured
/// to a log file under <c>.conductor/bg-logs/</c>.
/// </summary>
public sealed class BgCommand : Command<BgCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[VERB]")]
        [Description("Sub-command: start, status, logs, stop. Omit to show help.")]
        public string Verb { get; init; } = "";

        [CommandArgument(1, "[PID_OR_PURPOSE]")]
        [Description("PID (number) or purpose label (for logs/stop sub-commands).")]
        public string? PidOrPurpose { get; init; }

        [CommandOption("--purpose <LABEL>")]
        [Description("Purpose label for the background process (start only). Defaults to the executable name.")]
        public string? Purpose { get; init; }

        [CommandOption("--cwd <DIR>")]
        [Description("Working directory for the background process (start only). Defaults to the plan's repo root.")]
        public string? Cwd { get; init; }

        [CommandOption("-t|--tail <N>")]
        [Description("Number of lines to tail from the log (logs only, default 30).")]
        public int Tail { get; init; } = 30;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var verb = settings.Verb.ToLowerInvariant();
        var remaining = context.Remaining;

        try
        {
            return verb switch
            {
                "start" => ExecuteStart(settings, remaining),
                "status" => ExecuteStatus(settings),
                "logs" => ExecuteLogs(settings),
                "stop" => ExecuteStop(settings),
                _ => PrintBgHelp(),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    // ---------------------------------------------------------------- bg start

    private static int ExecuteStart(Settings settings, IRemainingArguments remaining)
    {
        var cmdArgs = remaining.Raw;
        if (cmdArgs.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]bg start requires a command after --.[/]");
            AnsiConsole.MarkupLine("[grey]Example: conductor bg start --purpose backtest -- dotnet run[/]");
            return 1;
        }

        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var runId = state.RunId ?? "bg-standalone";

        var exe = cmdArgs[0];
        var exeArgs = cmdArgs.Skip(1).ToList();
        var purpose = settings.Purpose ?? Path.GetFileNameWithoutExtension(exe);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = settings.Cwd ?? plan.Repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in exeArgs) psi.ArgumentList.Add(a);

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to start '{Markup.Escape(exe)}': {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (proc == null)
        {
            AnsiConsole.MarkupLine("[red]Process.Start returned null.[/]");
            return 1;
        }

        var logDir = Path.Combine(plan.StateDir, "bg-logs");
        Directory.CreateDirectory(logDir);
        var safePurpose = SanitizeFileName(purpose);
        var logPath = Path.Combine(logDir, $"{safePurpose}-{proc.Id}.log");

        // Fire-and-forget log capture: the Process object stays alive inside the closure.
        // The StreamWriter is disposed in the fire-and-forget task below — ownership transfers.
#pragma warning disable CA2000
        var logWriter = new StreamWriter(logPath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
#pragma warning restore CA2000
        var gate = new Lock();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) logWriter.WriteLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) logWriter.WriteLine($"[stderr] {e.Data}"); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        // Track in run.db
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (File.Exists(runDbPath))
        {
            try
            {
                using var db = new RunDb(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance);
                db.TrackPid(proc.Id, runId, $"bg:{purpose}", state.CurrentStage,
                    state.SessionCounter > 0 ? state.SessionCounter : null, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Started but run.db tracking failed: {Markup.Escape(ex.Message)}[/]");
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await proc.WaitForExitAsync().ConfigureAwait(false);
                var exitCode = 0;
                try { exitCode = proc.ExitCode; } catch { }
                if (File.Exists(runDbPath))
                {
                    try
                    {
                        using var db = new RunDb(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance);
                        db.MarkPidExited(proc.Id, exitCode);
                    }
                    catch { }
                }
            }
            catch { }
            finally { try { await logWriter.DisposeAsync().ConfigureAwait(false); } catch { } }
        });

        AnsiConsole.MarkupLine($"[green]bg started[/] PID={proc.Id} purpose=[bold]{Markup.Escape(purpose)}[/]");
        AnsiConsole.MarkupLine($"  log: [grey]{Markup.Escape(logPath)}[/]");
        return 0;
    }

    // ---------------------------------------------------------------- bg status

    private static int ExecuteStatus(Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (!File.Exists(runDbPath))
        {
            AnsiConsole.MarkupLine("[grey]No run.db found — no background processes tracked.[/]");
            return 0;
        }

        using var db = new RunDb(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance);
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        var runId = state.RunId;
        if (string.IsNullOrEmpty(runId))
        {
            AnsiConsole.MarkupLine("[grey]state.json has no RunId — no background processes tracked.[/]");
            return 0;
        }

        var pids = db.GetAllPids(runId);
        if (pids.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No background processes tracked for this run.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold aqua]Background Processes[/]")
            .AddColumn("PID")
            .AddColumn("Purpose")
            .AddColumn("Status")
            .AddColumn("Started")
            .AddColumn("Runtime");

        foreach (var p in pids)
        {
            var alive = IsProcessAlive(p.Pid);
            var status = p.ExitedUtc != null
                ? $"[grey]exited ({p.ExitedUtc:HH:mm:ss})[/]"
                : alive
                    ? "[green]running[/]"
                    : "[red]dead[/]";
            var startStr = p.StartedUtc.ToString("HH:mm:ss");
            var runtime = p.ExitedUtc != null
                ? FormatDuration(p.ExitedUtc.Value - p.StartedUtc)
                : alive
                    ? FormatDuration(DateTime.UtcNow - p.StartedUtc)
                    : "—";

            table.AddRow(
                Markup.Escape(p.Pid.ToString()),
                Markup.Escape(p.Purpose),
                status,
                Markup.Escape(startStr),
                Markup.Escape(runtime));
        }
        AnsiConsole.Write(table);

        // Hint about log paths
        AnsiConsole.MarkupLine("[grey]Logs: .conductor/bg-logs/  (use 'conductor bg logs <pid>' to tail)[/]");
        return 0;
    }

    // ---------------------------------------------------------------- bg logs

    private static int ExecuteLogs(Settings settings)
    {
        var target = settings.PidOrPurpose;
        if (string.IsNullOrWhiteSpace(target))
        {
            AnsiConsole.MarkupLine("[red]Usage: conductor bg logs <pid>[/]");
            AnsiConsole.MarkupLine("[grey]Example: conductor bg logs 12345[/]");
            return 1;
        }

        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var logDir = Path.Combine(plan.StateDir, "bg-logs");
        if (!Directory.Exists(logDir))
        {
            AnsiConsole.MarkupLine("[grey]No bg-logs directory found.[/]");
            return 0;
        }

        // Find log file: if target is numeric, match by PID in filename; otherwise try partial match
        string? logFile = null;
        var files = Directory.GetFiles(logDir, "*.log").OrderByDescending(File.GetLastWriteTime).ToList();

        if (int.TryParse(target, out var pid))
        {
            var pidSuffix = $"-{pid}.log";
            logFile = files.FirstOrDefault(f => f.EndsWith(pidSuffix, StringComparison.OrdinalIgnoreCase));
        }

        if (logFile == null)
        {
            // Fuzzy match by purpose substring
            logFile = files.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Contains(target, StringComparison.OrdinalIgnoreCase));
        }

        if (logFile == null)
        {
            // Check run.db for the PID's purpose and reconstruct the filename
            var runDbPath = Path.Combine(plan.StateDir, "run.db");
            if (File.Exists(runDbPath) && int.TryParse(target, out var dbPid))
            {
                try
                {
                    using var db = new RunDb(runDbPath,
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance);
                    var statePath = Path.Combine(plan.StateDir, "state.json");
                    var state = RunState.LoadOrNew(statePath, plan.Name);
                    if (!string.IsNullOrEmpty(state.RunId))
                    {
                        var allPids = db.GetAllPids(state.RunId);
                        var match = allPids.FirstOrDefault(p => p.Pid == dbPid);
                        if (match != null)
                        {
                            var safePurpose = SanitizeFileName(match.Purpose.Replace("bg:", ""));
                            var recons = Path.Combine(logDir, $"{safePurpose}-{match.Pid}.log");
                            if (File.Exists(recons)) logFile = recons;
                        }
                    }
                }
                catch { /* best-effort */ }
            }

            if (logFile == null)
            {
                AnsiConsole.MarkupLine($"[red]No log file found for '{Markup.Escape(target)}'.[/]");
                var availFiles = files.Select(Path.GetFileName);
                AnsiConsole.MarkupLine($"[grey]Available: {Markup.Escape(string.Join(", ", availFiles))}[/]");
                return 1;
            }
        }

        // Read and print the last N lines — synchronous by design (CLI command).
#pragma warning disable MA0045
        try
        {
            var tail = settings.Tail > 0 ? settings.Tail : 30;
            var allLines = File.ReadAllLines(logFile);
            var lines = allLines.Length <= tail ? allLines : allLines[^tail..];

            AnsiConsole.MarkupLine($"[bold aqua]Log: {Markup.Escape(Path.GetFileName(logFile))}[/] ({lines.Length}/{allLines.Length} lines)");
            AnsiConsole.WriteLine();
            foreach (var line in lines)
            {
                if (line.StartsWith("[stderr]", StringComparison.Ordinal))
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(line)}[/]");
                else
                    Console.WriteLine(line);
            }
        }
        catch (IOException ex)
        {
            AnsiConsole.MarkupLine($"[red]Cannot read log: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
#pragma warning restore MA0045

        return 0;
    }

    // ---------------------------------------------------------------- bg stop

    private static int ExecuteStop(Settings settings)
    {
        var target = settings.PidOrPurpose;
        if (string.IsNullOrWhiteSpace(target))
        {
            AnsiConsole.MarkupLine("[red]Usage: conductor bg stop <pid>[/]");
            AnsiConsole.MarkupLine("[grey]Example: conductor bg stop 12345[/]");
            return 1;
        }

        if (!int.TryParse(target, out var pid))
        {
            AnsiConsole.MarkupLine($"[red]'{Markup.Escape(target)}' is not a valid PID.[/] Use the numeric PID from 'conductor bg status'.");
            return 1;
        }

        // Kill the process
        try
        {
            using var proc = Process.GetProcessById(pid);
            AnsiConsole.MarkupLine($"[yellow]Stopping PID={pid} ({Markup.Escape(proc.ProcessName)})...[/]");
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
            AnsiConsole.MarkupLine($"[green]Killed PID={pid}.[/]");
        }
        catch (ArgumentException)
        {
            AnsiConsole.MarkupLine($"[grey]PID {pid} not found (already exited).[/]");
        }
        catch (InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[grey]PID {pid} already exited.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to kill PID {pid}: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        // Mark as exited in run.db
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (File.Exists(runDbPath))
        {
            try
            {
                using var db = new RunDb(runDbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunDb>.Instance);
                db.MarkPidExited(pid, -1);
            }
            catch { /* best-effort */ }
        }

        return 0;
    }

    // ---------------------------------------------------------------- helpers

    private static int PrintBgHelp()
    {
        AnsiConsole.MarkupLine("[bold aqua]conductor bg[/] — background process management");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [bold]start[/]  [grey]Spawn a long-running background process[/]");
        AnsiConsole.MarkupLine("         [grey]Usage: conductor bg start [[--purpose <label>]] [[--cwd <dir>]] -- <command> [[args...]][/]");
        AnsiConsole.MarkupLine("  [bold]status[/] [grey]List all tracked background processes and their liveness[/]");
        AnsiConsole.MarkupLine("  [bold]logs[/]   [grey]Tail the stdout/stderr log of a background process[/]");
        AnsiConsole.MarkupLine("         [grey]Usage: conductor bg logs <pid> [[-t|--tail <N>]][/]");
        AnsiConsole.MarkupLine("  [bold]stop[/]   [grey]Kill a background process by PID[/]");
        AnsiConsole.MarkupLine("         [grey]Usage: conductor bg stop <pid>[/]");
        return 0;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{(int)ts.TotalSeconds}s";
        if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars);
        return string.IsNullOrWhiteSpace(result) ? "bg-process" : result;
    }
}
