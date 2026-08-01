using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

using Conductor.Core.Fleet;
using Conductor.Core.Planning;
using Conductor.Models;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// SF5.4 — <c>conductor ps</c>: every conductor run on this machine, in one table.
///
/// <para>Concurrent runs have always worked — the control plane scans forward from 4317 so two engines
/// never fight over a port — but nothing ever listed them. The owner runs several websites at once;
/// "which port is the other one on, and is it parked?" was a question answered by memory, by hunting
/// for a terminal, or not at all.</para>
///
/// <para>Read-only, by construction: loopback <c>GET /state</c> on the twenty ports the server itself
/// would bind, nothing else. It never sends a token and never POSTs, so pointing it at a machine full
/// of other people's runs cannot perturb one. See <see cref="FleetScan"/> for why the port probe leads
/// and the discovery file only enriches.</para>
///
/// <code>
///   conductor ps                 # the fleet, newest port last
///   conductor ps --json          # same, for a script or a model
///   conductor ps --timeout 5000  # a busy engine folding a long log
/// </code>
/// </summary>
public sealed class PsCommand : AsyncCommand<PsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Emit the fleet as JSON on stdout and nothing else.")]
        public bool Json { get; init; }

        [CommandOption("--timeout <MS>")]
        [Description("Per-port probe budget in milliseconds (default 2500). Raise it if a busy engine is missing from the listing.")]
        public int? TimeoutMs { get; init; }

        [CommandOption("--ports <FIRST-LAST>")]
        [Description("Port window to scan (default 4317-4336, the range the control plane itself binds).")]
        public string? Ports { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var ports = ParsePorts(settings.Ports);
        if (ports is null)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] --ports wants FIRST-LAST, e.g. [yellow]4317-4336[/] (got [yellow]{Markup.Escape(settings.Ports ?? "")}[/]).");
            return 1;
        }

        var timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs is > 0 ? settings.TimeoutMs.Value : FleetScan.DefaultProbeTimeout.TotalMilliseconds);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // the per-probe CTS owns the clock
        var answered = await FleetScan.ScanAsync(FleetScan.HttpProbe(http, timeout), ports).ConfigureAwait(false);

        var runs = new List<FleetRun>();
        foreach (var r in answered) runs.Add(await FleetScan.EnrichFromDiskAsync(r).ConfigureAwait(false));

        // The run in this directory, if there is one, is the row the reader most expects to see — and a
        // headless engine (the control plane is opt-in) answers no port at all.
        var localStateDir = LocalStateDir(out var localPlanName);
        if (localStateDir is not null &&
            await FleetScan.UnattachedRunAsync(localStateDir, localPlanName, runs).ConfigureAwait(false) is { } orphan)
            runs.Add(orphan);

        if (settings.Json)
        {
            var report = new FleetReport(DateTime.UtcNow,
                $"{ports[0].ToString(CultureInfo.InvariantCulture)}-{ports[^1].ToString(CultureInfo.InvariantCulture)}",
                runs.Select(r => ToDto(r, localStateDir)).ToArray());
            Console.WriteLine(JsonSerializer.Serialize(report, FleetJsonContext.Default.FleetReport));
            return 0;
        }

        if (runs.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]no conductor runs answering on ports {ports[0]}-{ports[^1]}.[/]");
            return 0;
        }

        Render(runs, localStateDir);
        return 0;
    }

    private static void Render(IReadOnlyList<FleetRun> runs, string? localStateDir)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("REPO");
        table.AddColumn("PLAN");
        table.AddColumn("RUN");
        table.AddColumn("STAGE");
        table.AddColumn("STATUS");
        table.AddColumn(new TableColumn("PORT").RightAligned());
        table.AddColumn(new TableColumn("PID").RightAligned());
        table.AddColumn("UP");

        foreach (var r in runs)
        {
            var self = FleetScan.SameDir(r.StateDir, localStateDir);
            var repo = string.IsNullOrWhiteSpace(r.RepoLabel) ? Shorten(r.StateDir, 28) : r.RepoLabel;
            table.AddRow(
                (self ? "[bold]* [/]" : "  ") + $"[white]{Markup.Escape(repo)}[/]",
                Markup.Escape(Shorten(r.PlanName, 26)),
                $"[grey]{Markup.Escape(r.ShortRunId)}[/]",
                Markup.Escape(Shorten(string.IsNullOrWhiteSpace(r.StageId) ? "-" : r.StageId, 12)),
                StatusMarkup(r),
                r.Port > 0 ? r.Port.ToString(CultureInfo.InvariantCulture) : "[grey]-[/]",
                r.Pid > 0 ? r.Pid.ToString(CultureInfo.InvariantCulture) : "[grey]?[/]",
                $"[grey]{Markup.Escape(Age(r.StartedUtc, DateTime.UtcNow))}[/]");
        }

        AnsiConsole.Write(table);
        if (runs.Any(r => FleetScan.SameDir(r.StateDir, localStateDir)))
            AnsiConsole.MarkupLine("[grey]* the run in this directory. Attach the face with [/][yellow]conductor face[/][grey].[/]");
    }

    /// <summary>A parked run must not read like a healthy one at a glance — the whole point of listing
    /// the fleet is spotting the one that stopped needing electricity and started needing a human.</summary>
    private static string StatusMarkup(FleetRun r)
    {
        var attention = !string.IsNullOrWhiteSpace(r.AttentionReason);
        var text = attention ? $"{r.Status} ({r.AttentionReason})" : r.Status;
        var colour = attention ? "yellow"
            : r.Status.Contains("Fail", StringComparison.OrdinalIgnoreCase) ? "red"
            : r.Status.Contains("Paused", StringComparison.OrdinalIgnoreCase) ? "yellow"
            : r.Port == 0 ? "grey"
            : "green";
        return $"[{colour}]{Markup.Escape(Shorten(text, 34))}[/]";
    }

    internal static string Age(DateTime? startedUtc, DateTime nowUtc)
    {
        if (startedUtc is not { } s) return "?";
        var d = nowUtc - (s.Kind == DateTimeKind.Utc ? s : s.ToUniversalTime());
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h{d.Minutes.ToString("00", CultureInfo.InvariantCulture)}";
        return $"{(int)d.TotalDays}d{d.Hours.ToString("00", CultureInfo.InvariantCulture)}";
    }

    internal static string Shorten(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..Math.Max(1, max - 1)] + "…";
    }

    /// <summary>The port window, inclusive. Null = the caller wrote something that is not a window.</summary>
    internal static IReadOnlyList<int>? ParsePorts(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return FleetScan.DefaultPorts;
        var parts = spec.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var only) && only is > 0 and < 65536)
            return [only];
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last)) return null;
        if (first <= 0 || last < first || last >= 65536 || last - first > 512) return null;
        return Enumerable.Range(first, last - first + 1).ToArray();
    }

    private static FleetRunDto ToDto(FleetRun r, string? localStateDir) => new(
        r.Repo, r.PlanName, r.RunId, r.Status, r.Port, r.Pid, r.StageId, r.StageTitle, r.AttentionReason,
        r.Done, r.Total, r.CostUsd, r.BaseUrl, r.StateDir, r.StartedUtc, r.HasDiscoveryFile,
        FleetScan.SameDir(r.StateDir, localStateDir));

    /// <summary>The state dir of the plan in this directory, quietly — <c>ps</c> works anywhere, so a
    /// missing or ambiguous plan is a normal outcome, never a prompt and never an error.</summary>
    private static string? LocalStateDir(out string planName)
    {
        planName = "";
        try
        {
            var env = Environment.GetEnvironmentVariable("CONDUCTOR_PLAN");
            var path = env is { Length: > 0 } ? env : null;
            if (path is null)
            {
                var candidates = PlanDiscovery.Discover(Directory.GetCurrentDirectory());
                if (candidates.Count != 1) return null;
                path = candidates[0].Path;
            }
            if (!File.Exists(path)) return null;
            var plan = PlanConfig.Load(path);
            planName = plan.Name;
            return plan.StateDir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
