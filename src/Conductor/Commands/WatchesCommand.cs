using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

using Conductor.Core.Fleet;
using Conductor.Core.Planning;
using Conductor.Core.Watch;
using Conductor.Models;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// KS2.6 — <c>conductor watches</c>: what is armed on this machine.
///
/// <para><c>watch</c> blocks on one run; a <c>supervisor</c> block in a plan names who gets woken and
/// how often they may be. Neither of them could ever be ASKED. The two failures this checkpoint is
/// named for are the same gap from opposite ends: a preflight blip parked a run for fourteen hours
/// with nobody told, and a handoff mentioning the escalation token told the owner two hundred times.
/// Before you can trust either answer you have to be able to see, in one place, which runs are alive,
/// what would wake somebody for each, how much of its hourly fuse it has already burnt, and what
/// park-push cap it is running under.</para>
///
/// <para>Read-only by construction, exactly like <c>ps</c>: a loopback <c>GET /state</c> on the twenty
/// ports the control plane itself binds, plus reads of each run's plan file and its two supervisor
/// fire ledgers. It never sends a token, never POSTs and never writes — pointing it at a machine full
/// of other people's runs cannot perturb one.</para>
///
/// <code>
///   conductor watches            # every live run and what is watching it
///   conductor watches --json     # same, for a script or a model
/// </code>
/// </summary>
public sealed class WatchesCommand : AsyncCommand<WatchesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Emit the roster as JSON on stdout and nothing else.")]
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

        var ports = PsCommand.ParsePorts(settings.Ports);
        if (ports is null)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] --ports wants FIRST-LAST, e.g. [yellow]4317-4336[/] (got [yellow]{Markup.Escape(settings.Ports ?? "")}[/]).");
            return 1;
        }

        var timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs is > 0
            ? settings.TimeoutMs.Value : FleetScan.DefaultProbeTimeout.TotalMilliseconds);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var answered = await FleetScan.ScanAsync(FleetScan.HttpProbe(http, timeout), ports).ConfigureAwait(false);

        var runs = new List<FleetRun>();
        foreach (var r in answered) runs.Add(await FleetScan.EnrichFromDiskAsync(r).ConfigureAwait(false));

        // A headless engine answers no port at all, so the run in this directory is asked for by name.
        var local = LocalPlan();
        if (local is not null &&
            await FleetScan.UnattachedRunAsync(local.StateDir, local.Name, runs).ConfigureAwait(false) is { } orphan)
            runs.Add(orphan);

        var now = DateTimeOffset.UtcNow;
        var rows = runs.Select(r => WatchRoster.Describe(
            string.IsNullOrWhiteSpace(r.RepoLabel) ? r.StateDir : r.RepoLabel,
            r.PlanName, r.RunId, r.Status, r.Port, r.Pid,
            PlanFor(r, local), r.StateDir, now)).ToList();

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new { scannedUtc = now.UtcDateTime, ports = $"{ports[0]}-{ports[^1]}", watches = rows },
                JsonOut));
            return 0;
        }

        Render(rows, local, ports);
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOut =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static void Render(IReadOnlyList<WatchRosterEntry> rows, PlanConfig? local, IReadOnlyList<int> ports)
    {
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]no conductor runs answering on ports {ports[0]}-{ports[^1]} — nothing is armed on this machine.[/]");
            if (local is not null)
                AnsiConsole.MarkupLine(WatchRoster.Runs(local.Supervisor) || WatchRoster.Delivers(local.Supervisor?.Remote)
                    ? $"[grey]the plan here ([/]{Markup.Escape(local.Name)}[grey]) declares a supervisor — it arms when the run starts.[/]"
                    : $"[grey]the plan here ([/]{Markup.Escape(local.Name)}[grey]) declares no supervisor block.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("REPO");
        table.AddColumn("PLAN");
        table.AddColumn("RUN");
        table.AddColumn("STATUS");
        table.AddColumn("SUPERVISOR");
        table.AddColumn("FUSE");
        table.AddColumn("REMOTE");
        table.AddColumn("PUSHES");

        foreach (var e in rows)
        {
            table.AddRow(
                $"[white]{Markup.Escape(PsCommand.Shorten(e.Repo, 24))}[/]",
                Markup.Escape(PsCommand.Shorten(e.PlanName, 20)),
                $"[grey]{Markup.Escape(e.ShortRunId)}[/]",
                Markup.Escape(PsCommand.Shorten(e.Status, 16)),
                e.Unwatched
                    ? $"[yellow]{Markup.Escape(e.Supervisor)}[/]"
                    : Markup.Escape(PsCommand.Shorten(e.Supervisor, 42)),
                Markup.Escape(e.Fuse),
                Markup.Escape(PsCommand.Shorten(e.Remote, 26)),
                $"[grey]{Markup.Escape(e.Pushes)}[/]");
        }

        AnsiConsole.Write(table);
        var unwatched = rows.Count(r => r.Unwatched);
        if (unwatched > 0)
            AnsiConsole.MarkupLine($"[yellow]{unwatched}[/][grey] run(s) nothing would wake anybody for — add a [/][yellow]supervisor[/][grey] block, or run [/][yellow]conductor watch[/][grey] against them.[/]");
    }

    /// <summary>The plan for a listed run: the local one when it is that run, else the single plan
    /// discoverable in the run's own repo (matched by name when the repo holds several). Best-effort
    /// and non-prompting — an unreadable plan is reported as such, never as "no supervisor".</summary>
    private static PlanConfig? PlanFor(FleetRun run, PlanConfig? local)
    {
        if (local is not null && FleetScan.SameDir(run.StateDir, local.StateDir)) return local;
        try
        {
            if (string.IsNullOrWhiteSpace(run.Repo) || !Directory.Exists(run.Repo)) return null;
            var candidates = PlanDiscovery.Discover(run.Repo);
            var match = candidates.Count == 1
                ? candidates[0]
                : candidates.FirstOrDefault(c => string.Equals(c.Name, run.PlanName, StringComparison.OrdinalIgnoreCase));
            return match is null ? null : PlanConfig.Load(match.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The plan in this directory, quietly — <c>watches</c> works anywhere, so a missing or
    /// ambiguous plan is a normal outcome, never a prompt and never an error (the KS2.1/KS2.5 rule:
    /// never <c>PlanSettings.ResolvePlanPath</c> on a machine-level surface).</summary>
    private static PlanConfig? LocalPlan()
    {
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
            return File.Exists(path) ? PlanConfig.Load(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
