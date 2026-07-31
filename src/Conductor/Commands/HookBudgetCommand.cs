using System.ComponentModel;
using System.Text.Json;

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

    public override int Execute(CommandContext context, Settings settings)
    {
        try
        {
            var signal = Path.Combine(settings.StateDir, "soft-break");
            if (!File.Exists(signal)) return 0;

            // One notice per session, not one per tool call: the point is to change what the agent does
            // next, and repeating it every call would both nag and cost a few hundred tokens a turn for
            // the rest of the session — spending budget to announce that budget is short.
            var claimed = Path.Combine(settings.StateDir, "soft-break.delivered");
            if (File.Exists(claimed)) return 0;
            File.WriteAllText(claimed, DateTime.UtcNow.ToString("o"));

            Console.Out.Write(JsonSerializer.Serialize(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PostToolUse",
                    additionalContext = Notice,
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

    /// <summary>What the agent is actually told. Phrased as the next action rather than as a warning:
    /// "you are near a limit" invites either ignoring it or downing tools immediately, and the whole
    /// value of the cooperative rail is the third option — finish the piece in hand, write it down,
    /// stop.</summary>
    internal const string Notice = """
        CONDUCTOR — SESSION TOKEN BUDGET NEARLY SPENT.

        This session has used most of the tokens allotted to it and will be ended by the orchestrator
        when they run out. Work already committed is kept; anything still only in your head is not.

        Do this now, in order, and nothing else:
        1. Finish ONLY the sub-task in your hands. Start nothing new — no new checkpoint, no refactor,
           no "while I'm here" fix, no exploratory reading.
        2. Land it: run the gates you would normally run, claim any finished checkpoint with
           `conductor task --done <id> --evidence <path>`, and COMMIT. Uncommitted work is the only
           work that can be lost here.
        3. Overwrite the tracker handoff block so the next session can start cold: what you finished,
           what is half-done and exactly where, what is red, and the single next action.
        4. Print your `SESSION-RESULT:` paragraph and end the session.

        Ending here is the expected outcome and costs you nothing — the next session continues from
        your handoff with a fresh, cheap context. Stopping cleanly now is worth more than one more
        edit made in a hurry.
        """;
}
