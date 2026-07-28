using System.ComponentModel;
using System.Text.Json;

using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

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
