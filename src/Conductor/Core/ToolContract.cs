using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// The block every session prompt carries telling the agent which Conductor tools exist and when it is
/// obliged to use them (rendered as <c>{tools}</c>).
/// </summary>
/// <remarks>
/// This exists because the mechanisms were all built and none were used. The MCP server is wired into every
/// session (<c>OPENCODE_CONFIG</c>) and exposes note/bg/task/query tools, but no prompt ever mentioned them,
/// so: the ledger stayed empty (knowledge died with every killed session — failure C1), <c>conductor bg</c>
/// was never called (long commands blocked the foreground and got killed as stalls — failure C2), and the
/// task graph stayed empty. A capability an agent is not told about does not exist. Keep this block in the
/// prompt contract; it is load-bearing, not documentation.
/// </remarks>
public static class ToolContract
{
    public static string Render(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return $$"""
            ## Conductor tools — use them, they are how this run stays alive

            You are running inside Conductor. These verbs are wired into this session (CLI + MCP). Use them
            instead of hand-rolled bookkeeping. Every one of them exists because its absence has already
            cost this project real work.

            **Knowledge ledger — `conductor note "<what you learned>"`  (MCP: `conductor_note`)**
            The moment you learn something — a root cause, a dead end, a constraint that surprised you, a
            command that does not work here — write it down IMMEDIATELY. Not at session end.
            If you are killed mid-session, everything not in the ledger dies with you and the next agent
            repeats your mistake from scratch. That has happened: a session found the correct fix, was
            killed before it said so, and the next two sessions chased theories it had already disproved.
            A session that learned something and left an empty ledger has failed part of its job.

            **Long-running commands — `conductor bg start|status|logs|stop`  (MCP: `bg_start`/`bg_status`/`bg_logs`/`bg_stop`)**
            ANYTHING you expect to take more than ~3 minutes (builds, full test suites, servers, long
            scripts) MUST run through `conductor bg`, never in the foreground:
                conductor bg start --name tests -- {{ExampleLongCommand(plan)}}
                conductor bg status
                conductor bg logs --name tests
            Conductor's stall detector counts a live background child as proof of life. Block the
            foreground on a long command instead and you look silent — you will be killed as stalled while
            doing perfectly good work. Never kill processes by name (`Stop-Process dotnet`): you will take
            out unrelated work. Use `conductor bg stop`.

            **Checkpoint progress — `conductor task`  (MCP: `task_list`/`task_update`/`task_add`)**
                conductor task --list
                conductor task --in-progress <id>
                conductor task --done <id> --evidence <path-to-artifact>
            You CLAIM a checkpoint; you do not confirm it. Conductor confirms it — and only after its own
            independent gate battery and the Verifier agree with your claim. `{{plan.Tracker}}` is a
            GENERATED VIEW of the database: editing its rows by hand achieves nothing, because it is
            rewritten from the database. Report through the verb.

            **Ask the database — MCP `run_query`, `ledger_list`, `session_detail`**
            Prior sessions, gate history, costs, what previous agents learned and struggled with are all
            queryable. Query before you guess.

            **Evidence or it did not happen.** A checkpoint claimed DONE without a fresh artifact path (a
            file, a gate log, a commit sha) will be rejected by verification. Never weaken a gate, a golden
            file, or a test to get green — that is the one unforgivable move here. If a gate is wrong, say
            so in the ledger and the handoff; do not edit it into passing.
            """;
    }

    /// <summary>A concrete example command for the bg block, drawn from the plan's own gates so the sample
    /// is something the agent will actually run rather than a generic placeholder it has to translate.</summary>
    private static string ExampleLongCommand(PlanConfig plan)
    {
        var slowest = plan.Gates?.FirstOrDefault(g => g.Command?.Contains("test", StringComparison.OrdinalIgnoreCase) == true);
        return slowest?.Command ?? "<your long command>";
    }
}

/// <summary>
/// Fails a prompt that still contains an unresolved <c>{placeholder}</c> after rendering.
/// </summary>
/// <remarks>
/// A silent miss ships broken instructions to the agent and nobody notices. The live proof: the verifier
/// template contained <c>{plan.VerifierThreshold}</c>, which was never a template variable, so every
/// verifier was told its bar was literally "≥{plan.VerifierThreshold}". Prompts are code; an unbound name
/// is a compile error, not a formatting quirk.
/// </remarks>
public static partial class PromptValidator
{
    /// <summary>A placeholder is <c>{name}</c> / <c>{name.with.dots}</c> — deliberately NOT matching
    /// <c>{"json": ...}</c> or <c>{}</c>, both of which legitimately appear in prompt bodies (the verifier
    /// is asked to emit a JSON object).</summary>
    [GeneratedRegex(@"\{[a-zA-Z][a-zA-Z0-9_.]*\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlaceholderRegex();

    public static void ThrowIfUnresolved(string rendered, string templateName)
    {
        var leftovers = PlaceholderRegex().Matches(rendered)
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (leftovers.Count == 0) return;

        throw new InvalidOperationException(
            $"Template '{templateName}' has unresolved placeholder(s): {string.Join(", ", leftovers)}. " +
            "Either the name is misspelled or the variable is not supplied by PromptBuilder.Vars(). " +
            "An unresolved placeholder would be sent to the agent verbatim.");
    }
}
