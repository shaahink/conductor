using System.ComponentModel;
using System.Text.Json;

using Conductor.Core;

using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// B13.3 — the channel the cooperative soft-break was missing. Run by the agent CLI as a PostToolUse
/// hook; prints a wrap-up instruction into the session's context when, and only when, the orchestrator
/// has raised the soft-break signal for that session.
/// </summary>
/// <remarks>
/// <para>The soft-break was written as a cooperative rail — spend most of the budget, then be asked to
/// land the current sub-task and hand off cleanly — and half of it was missing. The engine wrote
/// <c>.conductor/soft-break</c> and emitted an event, and NOTHING carried either to the agent: a
/// non-interactive <c>claude -p</c> session has no inbox, does not poll the state directory, and was
/// never told the file existed. So the nudge fired into a void every time, and the only rail that could
/// still act was the hard one, which is a kill. A budget with no cooperative half spends its whole
/// margin on discarded work.</para>
/// <para>A hook is the right carrier because it is the one thing that runs INSIDE the agent's loop on
/// the agent's own schedule. Attached to PostToolUse, it rides tool calls the session is making anyway,
/// so the notice arrives within one tool call of the signal without the engine having to interrupt
/// anything. It stays silent otherwise: no signal, no output, no tokens. Timing out or failing must
/// leave the session alone, so every failure path here exits 0 with nothing written.</para>
/// </remarks>
public sealed class HookBudgetCommand : Command<HookBudgetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--state-dir <path>")]
        [Description("The run's state directory — the one holding the soft-break signal file.")]
        [DefaultValue(".conductor")]
        public string StateDir { get; init; } = ".conductor";
    }

    /// <summary>
    /// Where the hook's JSON goes; <see cref="Console.Out"/> when nothing is supplied, resolved at
    /// write time so a host that redirects the console still receives it.
    /// </summary>
    /// <remarks>Bug #26. The test used to capture this command's output with
    /// <c>Console.SetOut</c>, which is PROCESS-GLOBAL: under the full parallel suite another test's
    /// console writes landed in the same buffer and <c>JsonDocument.Parse</c> failed on them, so the
    /// test passed 10/10 alone and flaked in the battery. A writer the caller owns cannot be
    /// contaminated by whatever is running beside it.</remarks>
    internal TextWriter? Output { get; init; }

    public override int Execute(CommandContext context, Settings settings)
    {
        try
        {
            if (SoftBreak.ReadSignal(settings.StateDir) is not { } signal) return 0;

            // K1.2: NOT one notice per session. It was, and across the Sarban face run's eleven
            // post-cap rollovers not one session stopped at the nudge — every one of them ran on to
            // the hard ceiling and was killed mid-turn. A rail announced once, hundreds of thousands
            // of tokens before the end, is a rail an agent deep in a task reads and forgets. It is
            // re-stated on a token step and on an interval (SoftBreak.ShouldRestate) until the session
            // ends, and each restatement quotes the budget that is left AT THAT MOMENT.
            var previous = SoftBreak.ReadDelivery(settings.StateDir);
            if (!SoftBreak.ShouldRestate(signal, previous, DateTime.UtcNow, out _)) return 0;
            var delivery = SoftBreak.RecordDelivery(settings.StateDir, previous, signal, DateTime.UtcNow);

            (Output ?? Console.Out).Write(JsonSerializer.Serialize(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PostToolUse",
                    additionalContext = SoftBreak.Notice(signal, delivery.Count),
                },
            }));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A hook that cannot read a file must never be the reason a session ends.
            return 0;
        }
    }
}
