using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Face;
using Conductor.Core.Integrations;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

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
