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

            **Tracked bugs — `conductor bug new|list|fix`  (MCP: `bug_new`/`bug_list`/`bug_fix`)**
            When you find a real defect you are not fixing right now, FILE it: `conductor bug new "<title>"`.
            A filed bug is a row in run.db that outlives your session — it is injected into later prompts and
            handed to the audit phase, so the next agent fixes it instead of re-discovering it from scratch.
            Before hunting for bugs, run `conductor bug list` (MCP `bug_list`): the open ones are already
            known — do not re-file them. When you genuinely resolve one, `conductor bug fix <id>` closes it.

            **Checkpoint progress — `conductor task`  (MCP: `task_list`/`task_update`/`task_add`)**
                conductor task --list
                conductor task --in-progress <id>
                conductor task --done <id> --evidence <path-to-artifact>
            THIS IS THE ONLY WAY TO REPORT PROGRESS. It is the one channel Conductor reads when it works
            out what you delivered: a checkpoint you did not claim through this verb did not happen, no
            matter what you wrote elsewhere. There is no second mechanism to also update.
            You CLAIM a checkpoint; you do not confirm it. Conductor confirms it — and only after its own
            independent gate battery and the Verifier agree with your claim.
            `{{plan.Tracker}}` is a GENERATED VIEW of the database, rewritten from it every time, so its
            checkpoint rows are not somewhere you report — edits there are overwritten. The one part of
            that file that IS yours to write is the **handoff block**: Conductor reads it back and gives
            it to the next session, so put your handoff there.

            **Ask the database — MCP `run_query`, `ledger_list`, `bug_list`, `session_detail`**
            Prior sessions, gate history, costs, filed bugs, what previous agents learned and struggled with
            are all queryable. Query before you guess.

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
