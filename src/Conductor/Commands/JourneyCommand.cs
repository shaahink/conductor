using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;
using Conductor.Planning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// U0.2 — a pre-flight itinerary: what will run, in what order, under what model, gated by what,
/// and every point a human might be asked to step in — all before a single token is spent or a
/// byte of state is written. Read-only: never spawns an agent, never writes state.json/run.db (the
/// resume peek reuses <see cref="RunStateResume"/>'s own read-only connection, the same one
/// <c>RunCommand</c> uses to decide whether it is resuming). This is the map; <c>run --dry-run</c>
/// stays the next-step preview.
/// </summary>
public sealed class JourneyCommand : AsyncCommand<PlanSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PlanSettings settings)
    {
        var planPathArg = settings.ResolvePlanPath();
        var plan = PlanConfig.Load(planPathArg);
        var track = SafeReadTracker(plan);

        AnsiConsole.MarkupLine($"[bold aqua]conductor journey[/] — {Markup.Escape(plan.Name)}");
        AnsiConsole.WriteLine();

        await RenderIdentityAsync(plan).ConfigureAwait(false);
        RenderStages(plan, track);
        RenderGates(plan);
        RenderHumanMoments(plan, track);
        RenderFooter(planPathArg);

        return 0;
    }

    private static async Task RenderIdentityAsync(PlanConfig plan)
    {
        var t = new Table().Border(TableBorder.Rounded).Title("identity").HideHeaders();
        t.AddColumn("");
        t.AddColumn("");
        t.AddRow("plan", Markup.Escape(plan.Name));
        t.AddRow("repo", Markup.Escape(plan.Repo));
        t.AddRow("tracker", Markup.Escape(plan.Tracker));
        t.AddRow("state dir", Markup.Escape(plan.StateDir));
        t.AddRow("resume", Markup.Escape(await DescribeResumeAsync(plan).ConfigureAwait(false)));
        AnsiConsole.Write(t);
        AnsiConsole.WriteLine();
    }

    /// <summary>Mirrors RunCommand's own resume detection exactly (state.json first, the run.db row
    /// when it's empty) so journey never lies about what `conductor run` is actually about to do —
    /// and never writes either file itself. Internal (not private): unit-tested directly against
    /// temp dirs, same shape as <see cref="Conductor.Core.Planning.PlanDiscovery"/>.</summary>
    internal static async Task<string> DescribeResumeAsync(PlanConfig plan)
    {
        var statePath = Path.Combine(plan.StateDir, "state.json");
        var state = RunState.LoadOrNew(statePath, plan.Name);
        if (string.IsNullOrEmpty(state.RunId) && state.SessionCounter == 0)
        {
            var resumed = await RunStateResume.TryLoadLatestAsync(
                Path.Combine(plan.StateDir, "run.db"), plan.Name, CancellationToken.None).ConfigureAwait(false);
            if (resumed != null) state = resumed;
        }
        return string.IsNullOrEmpty(state.RunId)
            ? "fresh run — no saved state found"
            : $"resumes session #{state.SessionCounter + 1}, stage {state.CurrentStage ?? "?"} (run {Short(state.RunId)}, status {state.Status})";
    }

    private static string Short(string id) => string.IsNullOrEmpty(id) ? "?" : id.Length >= 8 ? id[..8] : id;

    private static void RenderStages(PlanConfig plan, TrackerSnapshot track)
    {
        var resolver = new WorkflowEngine();
        var qa = new DefaultQaPolicy();

        var t = new Table().Border(TableBorder.Rounded).Title("stages");
        t.AddColumn("Stage"); t.AddColumn("Title"); t.AddColumn("Sessions");
        t.AddColumn("Workflow"); t.AddColumn("Model"); t.AddColumn("Checkpoints");

        foreach (var stage in plan.Stages)
        {
            var workflow = resolver.Resolve(plan, stage, qa);
            var chain = string.Join(" -> ", workflow.Steps.Select(s => s.Kind));
            var model = plan.ResolveAgent(stage).Model ?? "(default)";
            var rows = track.ForStage(stage.Id).ToList();
            var checkpoints = rows.Count == 0 ? "-" : $"{rows.Count(r => r.IsDone)}/{rows.Count}";

            t.AddRow(Markup.Escape(stage.Id), Markup.Escape(stage.Title), stage.Sessions.ToString(),
                Markup.Escape(chain), Markup.Escape(model), checkpoints);
        }
        AnsiConsole.Write(t);
        AnsiConsole.WriteLine();
    }

    private static void RenderGates(PlanConfig plan)
    {
        AnsiConsole.MarkupLine("[bold]gates[/]");
        if (plan.Gates.Count == 0)
        {
            AnsiConsole.MarkupLine("  [grey](none configured — every session verdict will trust commits + tracker only)[/]");
            AnsiConsole.WriteLine();
            return;
        }

        foreach (var tier in new[] { "fast", "full", "truth" })
        {
            var inTier = plan.Gates.Where(g => g.Tier.Equals(tier, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inTier.Count == 0) continue;
            AnsiConsole.MarkupLine($"  [bold]{tier}[/]");
            foreach (var g in inTier)
                AnsiConsole.MarkupLine($"    {Markup.Escape(g.Name)} — [grey]{Markup.Escape(g.Command)}[/]");
        }
        AnsiConsole.WriteLine();
    }

    private static void RenderHumanMoments(PlanConfig plan, TrackerSnapshot track)
    {
        AnsiConsole.MarkupLine("[bold]human moments[/] — every point this run can stop for you");
        foreach (var line in DescribeHumanMoments(plan, track))
            AnsiConsole.MarkupLine($"  • {Markup.Escape(line)}");
        AnsiConsole.WriteLine();
    }

    /// <summary>Every point a live run can stop and wait for a person, in plain English. Pure (no
    /// console I/O) — internal so it is unit-tested directly rather than by scraping rendered
    /// output, same shape as <see cref="Conductor.Core.Planning.PlanDiscovery.Discover"/>.</summary>
    internal static IReadOnlyList<string> DescribeHumanMoments(PlanConfig plan, TrackerSnapshot track)
    {
        var lines = new List<string>
        {
            plan.PauseOnBlocked
                ? "pauseOnBlocked: on — a session reporting BLOCKED parks at AwaitingOwner instead of retrying"
                : "pauseOnBlocked: off — a BLOCKED verdict is retried like any other failure",
        };

        var ownerGated = plan.Stages.Where(s => s.OwnerGate).Select(s => s.Id).ToList();
        lines.Add(ownerGated.Count > 0
            ? $"owner-gated stages: {string.Join(", ", ownerGated)} — parks for approval (conductor approve) even when green"
            : "owner-gated stages: none");

        if (plan.Conventions.MentionsHuman(track.HandoffBlock))
            lines.Add($"the tracker handoff currently contains a {plan.Conventions.HumanToken} request — a human decision is pending right now");

        if (plan.Limits.MaxRunCostUsd is { } cost)
            lines.Add($"maxRunCostUsd: ${cost:0.00} — the run parks at AwaitingOwner once total cost reaches this");
        if (plan.Limits.MaxRunTokens is { } tokens)
            lines.Add($"maxRunTokens: {tokens:N0} — the run parks at AwaitingOwner once total tokens reach this");
        if (plan.Limits.MaxSessions is int maxSessions && maxSessions > 0)
            lines.Add($"maxSessions: {maxSessions} — the run PARKS at the next session boundary once this many sessions have run (raise/clear + reload to resume)");
        if (plan.Limits.ApprovalMode)
            lines.Add("approvalMode: on — the run parks at AwaitingOwner before EVERY session/commit");

        return lines;
    }

    private static void RenderFooter(string planPathArg)
    {
        AnsiConsole.MarkupLine("[bold]to proceed[/]");
        AnsiConsole.MarkupLine($"  [yellow]conductor run -p {Markup.Escape(planPathArg)}[/]          start / resume");
        AnsiConsole.MarkupLine($"  [yellow]conductor run -p {Markup.Escape(planPathArg)} --paused[/]  start parked — attach the Face first");
        AnsiConsole.MarkupLine($"  [yellow]conductor run -p {Markup.Escape(planPathArg)} --dry-run[/] preview just the next session's prompt");
    }

    private static TrackerSnapshot SafeReadTracker(PlanConfig plan)
    {
        try { return ProgressProviderFactory.Create(plan).Read(plan, CancellationToken.None); }
        catch (Exception) { return new TrackerSnapshot(); }
    }
}
