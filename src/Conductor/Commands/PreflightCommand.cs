using System.ComponentModel;
using System.Diagnostics;

using Conductor.Core;
using Conductor.Models;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// KS3.4 — the launch drill as one verb. The drill was a checklist an operator (or an agent reading
/// a skill file) typed by hand: <c>doctor</c>, then <c>journey</c>, then <c>run --dry-run</c>, then
/// remember to ask whether this binary is the one the source tree would build, then remember that a
/// tracker handoff still asking for a human parks session one before anything spawns. Four of those
/// five steps were only ever done when someone remembered them, and the two that were remembered
/// least are the two that cost the most.
/// <para>Six legs, one invocation, one verdict, one exit code. Nothing here is a second opinion:
/// every leg runs a shipped implementation and sorts its answer into a named leg, so a green
/// preflight means exactly what a green doctor, a resolved journey and a composable dry-run mean.</para>
/// </summary>
public sealed class PreflightSettings : PlanSettings
{
    /// <summary>Same switch, same meaning as <c>doctor</c>'s: skip the one-token auth ping — the only
    /// leg of the drill that spends money or talks to the model backend.</summary>
    [CommandOption("--no-auth-check")]
    [Description("Skip the one-token auth smoke test (~$0.001) against the configured agent CLI")]
    public bool NoAuthCheck { get; init; }

    /// <summary>Same switch, same meaning as <c>doctor</c>'s: no release-feed lookup. Also honoured
    /// as <c>CONDUCTOR_NO_UPDATE_CHECK</c>.</summary>
    [CommandOption("--no-update-check")]
    [Description("Skip the check for a newer released engine")]
    public bool NoUpdateCheck { get; init; }
}

public sealed partial class PreflightCommand : AsyncCommand<PreflightSettings>
{
    /// <summary>One leg of the drill. <paramref name="State"/> is <c>ok</c> | <c>warn</c> |
    /// <c>fail</c>; <paramref name="Detail"/> is what the operator has to read to fix it, printed
    /// under the leg rather than crammed into its headline.</summary>
    internal sealed record Leg(string Name, string State, string Headline, IReadOnlyList<string> Detail);

    /// <summary>The legs, in the order they run and print. Named so a failure line can be grepped
    /// and so a test can assert on the leg rather than on a sentence.</summary>
    internal const string DoctorLegName = "doctor";
    internal const string JourneyLegName = "journey";
    internal const string ComposeLegName = "compose";
    internal const string VersionLegName = "version";
    internal const string RebuildLegName = "rebuild";
    internal const string EscalationLegName = "escalation";

    public override async Task<int> ExecuteAsync(CommandContext context, PreflightSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sw = Stopwatch.StartNew();

        string planPath;
        PlanConfig plan;
        try
        {
            planPath = settings.ResolvePlanPath();
            plan = PlanConfig.Load(planPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or System.Text.Json.JsonException)
        {
            // Same shape as doctor's: a plan that does not load is a finding, not a stack trace and a
            // crash log in whatever directory the operator happened to be standing in.
            AnsiConsole.MarkupLine("[bold aqua]conductor preflight[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(Render(new Leg(DoctorLegName, "fail", ex.Message, [])));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[red]NOT READY[/] — the plan does not load, so no other leg ran ({sw.Elapsed.TotalMilliseconds:0}ms)");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold aqua]conductor preflight[/] — {Markup.Escape(plan.Name)}");
        AnsiConsole.MarkupLine($"repo: {Markup.Escape(plan.Repo)}");
        AnsiConsole.WriteLine();

        var legs = await RunLegsAsync(plan, authCheck: !settings.NoAuthCheck, updateCheck: !settings.NoUpdateCheck)
            .ConfigureAwait(false);
        sw.Stop();

        foreach (var leg in legs)
        {
            AnsiConsole.MarkupLine(Render(leg));
            foreach (var line in leg.Detail)
                AnsiConsole.MarkupLine($"           [grey]{Markup.Escape(line)}[/]");
        }

        var failed = legs.Where(l => l.State == "fail").Select(l => l.Name).ToList();
        var warned = legs.Count(l => l.State == "warn");
        var ms = $"{sw.Elapsed.TotalMilliseconds:0}ms";

        AnsiConsole.WriteLine();
        if (failed.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]NOT READY[/] — {failed.Count} of {legs.Count} legs failed: {string.Join(", ", failed)} ({ms})");
            return 1;
        }

        var caveat = warned > 0 ? $", {warned} with warnings" : "";
        AnsiConsole.MarkupLine(
            $"[green]READY[/] — {legs.Count} legs clear{caveat}; nothing has spent anything. " +
            $"Launch with [yellow]conductor run -p {Markup.Escape(planPath)}[/] ({ms})");
        return 0;
    }

    /// <summary>The drill. One doctor pass feeds four of the six legs, so the checks that answer to a
    /// named leg are taken OUT of the doctor leg rather than reported twice — a seeded failure names
    /// one leg, which is the whole point of naming them.
    /// <para>The release probe is run here, once, rather than inside the doctor pass: the version leg
    /// needs the verdict (<c>a newer release exists</c>) and not just doctor's line, and asking twice
    /// would be two round trips for one answer.</para></summary>
    /// <param name="image">The engine binary the rebuild leg judges. Null means "the one answering
    /// this call", which is what the verb wants; a test states it instead, because a suite hosted in
    /// somebody else's process must not have its verdict decided by where that process's exe sits.</param>
    internal static async Task<List<Leg>> RunLegsAsync(PlanConfig plan, bool authCheck, bool updateCheck,
        EngineImage? image = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var checks = await DoctorCommand.RunChecksAsync(plan, authCheck: authCheck, updateCheck: false).ConfigureAwait(false);
        return
        [
            DoctorLeg(checks),
            JourneyLeg(plan, checks),
            await ComposeLegAsync(plan, checks).ConfigureAwait(false),
            await VersionLegAsync(updateCheck, DateTimeOffset.UtcNow).ConfigureAwait(false),
            RebuildLeg(plan, image ?? EngineImage.Running(), PathCopyOfConductor()),
            await EscalationLegAsync(plan, checks).ConfigureAwait(false),
        ];
    }

    /// <summary>Which leg owns which of doctor's checks. Everything not named here is the doctor
    /// leg's — a new doctor check therefore lands somewhere by default instead of falling out of the
    /// drill silently.</summary>
    private static readonly Dictionary<string, string> CheckOwner = new(StringComparer.Ordinal)
    {
        ["model"] = JourneyLegName,
        ["prompt"] = ComposeLegName,
        ["templates"] = ComposeLegName,
        ["argv"] = ComposeLegName,
        ["escalation"] = EscalationLegName,
        ["update"] = VersionLegName,
    };

    /// <summary>Doctor's own verdict, minus the checks another leg reports. Fails when doctor fails —
    /// the acceptance is <c>0 fail</c>, exactly as the hand-typed drill said.</summary>
    internal static Leg DoctorLeg(IReadOnlyList<DoctorCommand.Check> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var mine = checks.Where(c => !CheckOwner.ContainsKey(c.Name)).ToList();
        return FromChecks(DoctorLegName, mine,
            $"{mine.Count(c => c.State == "ok")} ok, {mine.Count(c => c.State == "warn")} warn, " +
            $"{mine.Count(c => c.State == "fail")} fail across {mine.Count} check(s)");
    }

    /// <summary>Rolls a set of doctor checks into one leg: the worst state wins, and every check that
    /// is not <c>ok</c> is carried through verbatim as detail. Nothing is rephrased — the check
    /// already says the actionable thing, and a second wording is a second thing to keep true.</summary>
    private static Leg FromChecks(string name, IReadOnlyList<DoctorCommand.Check> checks, string headline,
        IEnumerable<string>? extraDetail = null, string? extraState = null)
    {
        var detail = checks.Where(c => c.State != "ok")
            .OrderBy(c => c.State == "fail" ? 0 : 1)
            .Select(c => $"{c.Name}: {c.Message}")
            .ToList();
        if (extraDetail is not null) detail.AddRange(extraDetail);

        var state = Worst(checks.Select(c => c.State).Append(extraState ?? "ok"));
        return new Leg(name, state, headline, detail);
    }

    /// <summary>fail beats warn beats ok. One rule, so no leg can be green with a red inside it.</summary>
    internal static string Worst(IEnumerable<string> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var worst = "ok";
        foreach (var s in states)
        {
            if (s == "fail") return "fail";
            if (s == "warn") worst = "warn";
        }
        return worst;
    }

    private static string Render(Leg leg)
    {
        var (glyph, color) = leg.State switch
        {
            "ok" => ("✓", "green"),
            "warn" => ("⚠", "yellow"),
            _ => ("✗", "red"),
        };
        return $"[{color}]{glyph}[/] [bold]{Markup.Escape(leg.Name),-10}[/] {Markup.Escape(leg.Headline)}";
    }
}
