using System.ComponentModel;
using System.Globalization;

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
        var entry = Queue(plan, settings.Instruction);
        // One sentence, one source: the printed characters are QueuedLine's characters, with the
        // first word green. Anything else and the count the test pins is not the count you read.
        var line = QueuedLine(entry);
        var firstSpace = line.IndexOf(' ', StringComparison.Ordinal);
        AnsiConsole.MarkupLine($"[green]{line[..firstSpace]}[/]{Markup.Escape(line[firstSpace..])}");
        return 0;
    }

    /// <summary>Everything the verb does to the queue: link to the previous instruction, then store
    /// the WHOLE argument. Shared with the test so both drive one path.</summary>
    public static InstructionQueue.Entry Queue(PlanConfig plan, string instruction)
    {
        var prev = InstructionQueue.List(plan).LastOrDefault()?.File;
        return InstructionQueue.Write(plan, instruction, prev);
    }

    /// <summary>KS2.0: the success line says how much was stored. `inject` is the one channel a human
    /// has to steer a live run, and a cut anywhere before this process (a here-string through a .cmd
    /// shim loses everything after the first newline) used to look exactly like success — well-formed
    /// JSON, a confident green line, nothing in `status`. A character count cannot be misread:
    /// `queued 001-… (2,919 chars)` against an instruction the operator knows is 2,919 characters
    /// long is the whole check, on the first try.</summary>
    public static string QueuedLine(InstructionQueue.Entry entry)
        => $"queued {entry.File} ({entry.Text.Length.ToString("N0", CultureInfo.InvariantCulture)} chars) — injected into the next session prompt";
}
