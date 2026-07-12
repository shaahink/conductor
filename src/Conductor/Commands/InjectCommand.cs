using System.ComponentModel;

using Conductor.Core;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

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
