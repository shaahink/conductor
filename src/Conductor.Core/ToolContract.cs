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

            You are running inside Conductor; these verbs are wired into this session (CLI + MCP). Use them
            instead of hand-rolled bookkeeping. Each exists because its absence already cost real work.

            **Knowledge ledger — `conductor note "<what you learned>"`  (MCP: `conductor_note`)**
            Write what you learn IMMEDIATELY — a root cause, a dead end, a constraint that surprised you. If
            you are killed, everything not in the ledger dies with you: one session found the correct fix, was
            killed before saying so, and the next two chased theories it had disproved.

            **Long-running commands — `conductor bg start|status|logs|stop`  (MCP: `bg_start`/`bg_status`/`bg_logs`/`bg_stop`)**
            ANYTHING over ~3 minutes (builds, full test suites, servers) MUST run through `conductor bg`:
                conductor bg start --name tests -- {{ExampleLongCommand(plan)}}
                conductor bg status · conductor bg logs --name tests · conductor bg stop
            A live background child is proof of life to the stall detector; block the foreground instead and
            you look dead — a session was killed at 15 minutes of silence doing perfectly good work.

            **Never kill a pid you have not identified**, and never kill by name (`Stop-Process dotnet`
            takes out unrelated work). The conductor supervising you is PID {{Environment.ProcessId}}, also in
            `CONDUCTOR_PID`; `locked by: conductor (PID)` in a build error is almost always THIS run holding
            its own binary, not an orphan — a fix session inferred one and killed the conductor running it.
            Another repo's run may share this machine: check a pid's command line first.

            **Tracked bugs — `conductor bug new|list|fix`  (MCP: `bug_new`/`bug_list`/`bug_fix`)**
            A defect you are not fixing now: `conductor bug new "<title>"` outlives your session and reaches
            later prompts and the audit phase. `bug list` before hunting — the open ones are known, do not
            re-file. `bug fix <id>` closes one.

            **Checkpoint progress — `conductor task`  (MCP: `task_list`/`task_update`/`task_add`)**
                conductor task --list
                conductor task --in-progress <id>          # BEFORE your first edit, not after the work
                conductor task --done <id> --evidence <path-to-artifact>
            THIS IS THE ONLY WAY TO REPORT PROGRESS — the one channel Conductor reads when it works out what
            you delivered. There is no second mechanism to also update, and a checkpoint not claimed through
            this verb did not happen, whatever you wrote elsewhere. Mark IN PROGRESS before your first edit —
            a session that delivered for 56 minutes without it left the owner watching a wall of TODO — and
            claim BEFORE the handoff, or you run out of room having delivered nothing that counts. If the MCP
            tools arrive DEFERRED, `ToolSearch` for `task_update`, or use the CLI. You CLAIM; Conductor confirms
            once its gates and the Verifier agree. `{{plan.Tracker}}` is a GENERATED VIEW of the database — its
            rows are overwritten, so the part that IS yours is the **handoff block**, for the next session.

            **Correcting the board — `conductor task --todo|--blocked|--skipped <id>` and `--amend <id> --note "<text>"`**
            Put a card back with `--todo`, park one with `--blocked`, retire one with `--skipped`. Every move
            prints the card's REAL status and exits non-zero if refused — believe the output, not intent. When
            a checkpoint's acceptance encodes a false premise, do not argue in prose nothing reads:
                conductor task --amend <id> --note "acceptance says X; X is impossible because Y - delivering Z"
            The amendment rides the card into the next session's prompt and lands in the ledger.

            **Blocked on a clock — `conductor task --blocked-until <iso8601> --reason "<text>"`  (MCP: `task_blocked_until`)**
            Cannot proceed until a known future instant (rate-limit window, deploy slot)? Do not end the
            session hoping the wall is gone, and do not re-measure the clock:
                conductor task --blocked-until 2026-07-31T15:12:00Z --reason "deploy window full, next slot 15:12"
            Conductor sleeps until then and spawns ONE more session — no attempt burned, your reason handed
            on. Must be in the future and within 24h; longer is an escalation, not a nap. End the session at
            once: the engine is waiting.

            **Ask the database — MCP `run_query`, `ledger_list`, `bug_list`, `session_detail`.** Prior sessions,
            gates, costs, bugs and what earlier agents learned are queryable. Query before you guess.

            **Keep your own context small — delegate the wide reads.** Cost is (turns) x (context) and
            context only grows, so early bytes are paid for on every turn after. A directory sweep, a
            "where is this referenced", or a survey of files you will not edit belongs in a SUBAGENT:
            keep its conclusion, not the file dumps. Read the SECTION your work names, not the whole
            document, and never re-read what is already in context.

            **Evidence or it did not happen.** A checkpoint claimed DONE without a fresh artifact path (a file,
            a gate log, a sha) is rejected by verification. Never weaken a gate, a golden file or a test to get
            green — the one unforgivable move here; if a gate is wrong, say so in the ledger and the handoff,
            never by editing it into passing.
            """ + MultiRepoSection(plan);
    }

    /// <summary>The anchor-repo rule, rendered only for plans that declare satellites — on a single-repo
    /// plan it is noise, and every line of the tools block is paid for in every session's prompt.</summary>
    /// <remarks>
    /// sk-platform field note 3: a stage whose entire output was a sibling-repo PR scored NoProgress twice
    /// and cost $3.82 in fix sessions for nothing being broken. SC4.3 made commits in DECLARED satellites
    /// count as delivery, which is why this block names the declared list — an undeclared repo is still
    /// invisible — and why the anchor commit is about the handoff and evidence travelling with the run
    /// rather than about dodging the verdict.
    /// </remarks>
    private static string MultiRepoSection(PlanConfig plan)
    {
        if (plan.SatelliteRepos is not { Count: > 0 }) return "";
        var declared = string.Join(", ", plan.SatelliteRepos.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => $"`{s}`"));
        return $"""


            **This plan spans repos — land at least one commit HERE every session.** Commits in the declared
            satellites ({declared}) count as delivery, but the handoff, evidence and notes the run reads live
            here, so finish in the anchor repo. A repo NOT in that list is invisible to the verdict: if work
            belongs somewhere new, say so in the handoff rather than committing into the dark.
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
