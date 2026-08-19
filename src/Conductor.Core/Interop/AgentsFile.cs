using System.Globalization;

namespace Conductor.Core.Interop;

/// <summary>
/// KS8.2 — <c>AGENTS.md</c> as a courtesy to every agent that is not the one conductor spawned, and
/// the import line that makes Claude Code actually read it.
/// </summary>
/// <remarks>
/// <para><b>Why an import and not just the file.</b> AGENTS.md is the convention most coding agents
/// now look for, and Claude Code still does not read it natively — it reads <c>CLAUDE.md</c>. The
/// working play, and the one this generates, is a one-line <c>CLAUDE.md</c> that imports the other
/// file with <c>@AGENTS.md</c>: one source of truth, honoured by both families of agent, and no
/// second copy to drift.</para>
///
/// <para><b>Neither file is ever overwritten.</b> An existing <c>AGENTS.md</c> is the repo's own and
/// is left exactly as found. An existing <c>CLAUDE.md</c> is APPENDED to, and only when it does not
/// already import — clobbering the file that steers somebody's agent is the one failure mode this
/// whole feature could have, and it is worth being boring about.</para>
/// </remarks>
public static class AgentsFile
{
    /// <summary>The import directive Claude Code expands.</summary>
    public const string ImportLine = "@AGENTS.md";

    public const string AgentsFileName = "AGENTS.md";
    public const string ClaudeFileName = "CLAUDE.md";

    /// <summary>True when this <c>CLAUDE.md</c> text already pulls AGENTS.md in. Matches the bare
    /// directive anywhere in the file, because that is all Claude Code needs to see.</summary>
    public static bool ImportsAgents(string? claudeMd) =>
        claudeMd is { Length: > 0 } && claudeMd.Contains(ImportLine, StringComparison.Ordinal);

    /// <summary>
    /// What <c>CLAUDE.md</c> should contain after this repo is scaffolded, or null when it already
    /// imports and must not be touched.
    /// </summary>
    /// <param name="existing">The current file's text, or null when there is no CLAUDE.md.</param>
    public static string? ClaudeMdWithImport(string? existing)
    {
        if (ImportsAgents(existing)) return null;

        var header =
            "# CLAUDE.md" + Environment.NewLine + Environment.NewLine
            + "This repo keeps its agent instructions in `AGENTS.md`, the convention most coding" + Environment.NewLine
            + "agents look for. Claude Code reads this file instead, so it imports that one — one" + Environment.NewLine
            + "source of truth, no second copy to drift." + Environment.NewLine + Environment.NewLine
            + ImportLine + Environment.NewLine;

        if (string.IsNullOrWhiteSpace(existing)) return header;

        // Appended, never rewritten: this file is somebody's, and the import is an addition to it.
        var separator = existing.EndsWith('\n') ? "" : Environment.NewLine;
        return existing + separator + Environment.NewLine
            + "<!-- added by conductor init: agent instructions live in AGENTS.md -->" + Environment.NewLine
            + ImportLine + Environment.NewLine;
    }

    /// <summary>
    /// The AGENTS.md conductor scaffolds into a repo it is about to drive. Deliberately short: it
    /// says what conductor is, what the session's own verbs are, and where the plan lives — the
    /// things an agent cannot work out from the source — and leaves the repo's engineering rules to
    /// whoever knows them.
    /// </summary>
    public static string Generate(string planName, string trackerFileName)
    {
        ArgumentNullException.ThrowIfNull(planName);
        return string.Create(CultureInfo.InvariantCulture, $"""
# AGENTS.md

Agent instructions for this repo. Read once; it is short on purpose.

## This repo is driven by conductor

An autonomous session in this repo is spawned by **conductor**, an orchestrator that runs a plan
stage by stage, gates every delivery, and keeps the record. The plan is `conductor.plan.json`; the
board a session reports against is `{trackerFileName}`. The current plan is **{planName}**.

Sessions are independent — the context is reset between every one. Whatever the next session needs
to know has to be written down, not remembered.

## The verbs a session has

These are wired into every session conductor spawns, as CLI and as MCP tools. Use them instead of
hand-rolled bookkeeping; each exists because its absence cost real work.

| verb | what it is for |
|---|---|
| `conductor task --list` | the checkpoints of the current stage |
| `conductor task --in-progress <id>` | **before** the first edit, not after the work |
| `conductor task --done <id> --evidence <path>` | the ONLY way progress is reported |
| `conductor note "<what you learned>"` | the knowledge ledger — write findings immediately |
| `conductor bug new\|list\|fix` | a defect you are not fixing now, so it outlives the session |
| `conductor bg start\|status\|logs\|stop` | anything over ~3 minutes, or the stall watchdog sees silence |

A checkpoint claimed any other way — prose in a handoff, a tick in a table — did not happen.
Conductor confirms the claim independently; the session claims, it does not decide.

## Evidence

A checkpoint is done when there is an artifact a reviewer can open: a file, a gate log, a commit
sha. Never weaken a gate, a golden file or a test to get green. If a bar is genuinely wrong, say so
in the ledger and stop — that is a result too.

## Where things are

- `conductor.plan.json` — stages, checkpoints, gates.
- `{trackerFileName}` — the board, and the handoff block the next session reads.
- `templates/` — the prompt templates conductor renders per session kind.

""");
    }
}
