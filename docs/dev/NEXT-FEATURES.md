# Conductor — next features backlog

Ideas captured during the v2 dashboard work, kept here so they survive across sessions. Each should
stay **resume-friendly** (persisted in RunState / `.conductor/`) and must never disrupt an in-flight
run.

**Refreshed 2026-08-01 (SF7.1).** This page had drifted into the failure mode the Sarban era exists
to kill: it promised as *future* a long list of things the tree already does. Ten of its entries had
shipped — some of them eras ago — and a reader planning work off it would have rebuilt them. Every
item below was checked against the code on that date, not against this page's own prose, and the
check is pinned by `SF7_1DocsMatchRealityTests` so the next drift is a red test rather than a
re-read: an item named shipped whose symbol disappears fails, and an item named open whose symbol
appears fails too.

---

## Shipped — kept as a record of what closed, not as work

Do not re-plan these. Each names the thing in the tree that answers it.

- **Token-budget rollover.** `RolloverCommand`, `limits.maxSessionTokens`, and the live
  `set-rollover` control on `ControlDispatcher`. A session that crosses the ceiling ends cleanly
  after writing its handoff, and no attempt is burned.
- **Learning pipeline / instruction batteries.** The whole section landed as `IPromptBattery`
  composed by `PromptBuilder.BatterySection`: `LessonsBattery` (over `.conductor/lessons.md`),
  `LedgerBattery`, `BugsBattery`, `RecentFailureBattery`, `LaneArtifactBattery`. `plan.batteryCollapse`
  is the single-source-of-truth switch that stops the agent and the engine running the battery twice.
- **Handover gaps → follow-up work.** `FollowupParser` reads the handover's weak/deferred/bug bullets
  into `.conductor/followups.md`, and `LaneCoordinator` opens Tier B lanes from them.
- **Heartbeat REPORT.md.** `HeartbeatCommand` writes the report *during* a long session, so the AFK
  view stops lagging a session behind.
- **Serilog structured logging.** Referenced in `src/Conductor/Conductor.csproj`, file+console sinks,
  written to `logs/conductor-YYYYMMDD.json`.
- **Diagnostic console.** The Face's Agent tab (SF1.3) — a scrollable live view of what the engine and
  the agent are doing, without tailing a log file.
- **Graceful Ctrl+C, and window close.** `ConsoleCtrlRails` (W3.3): state saved, resume queued, logs
  flushed, on both the interrupt and the close-button paths.
- **Zero-config bootstrap.** `InitCommand` detects dotnet / node / go / rust / python and scaffolds a
  starter plan with sensible default gates.
- **Cost per checkpoint.** `sessions/NNN/cost.json` plus the `costs` table in `run.db`; surfaced in
  the digest and the Face.
- **Colour/readability + beauty pass.** SF1–SF3 across the Go Face.
- **Lifecycle pause → redeploy → resume.** Supported and documented; `conductor doctor` prints what
  will happen on resume, and `conductor update` (SC8.3) replaces the binary and refuses to do it
  mid-run.

## Still open — real work, none of it started

- **Branch hygiene is warn-only.** `RunLoop.Control.cs` `WarnOnBranchPattern` is the *only* enforcement
  of `branchPattern` in the engine: a session on the wrong branch is told so and proceeds. The open
  work is optionally creating/checking out the per-stage branch and asserting the pattern before a
  session is allowed to commit.
- **Commit/push discipline and git safety are unenforced.** There is no `requireCleanTree` anywhere in
  the tree — no post-session assertion that the working tree is clean, that the branch was pushed, or
  that the per-checkpoint commit convention held. Nothing refuses a detached HEAD or a wrong remote,
  and nothing guards a force-push. The rules exist only as prose the agent is asked to follow.
- **The processes lane.** Live gate timers surface conductor's own shell; a nested view of agent bash
  tools + gates + hooks, à la Claude Code's tool tree, does not exist.
- **Two batteries that were designed and never built.** `RepoMapBattery` (most-touched files this
  phase, surfaced next session) and a definition-of-done recap pulling the active checkpoint's
  acceptance from the doc. Both fit the shipped `IPromptBattery` seam — this is implementation, not
  design.
- **Plan import still needs an existing plan to diff against.** There is no from-scratch import path.
- **Gate on unacknowledged handover gaps.** Phase-confirm could fail when the handover lists a
  critical gap nobody acknowledged. The parse exists; the gate does not.

## Filed 2026-08-01 (SF7.1) — the MCP config the engine writes is not the one the harness gets

`SessionRunner.Mcp.cs` `WireMcpServer` writes a config containing **only** the `conductor-tasks`
server, in both dialects. Any MCP server the operator has configured for their harness is absent from
what conductor hands the agent, and in at least one shipped harness the conductor tools themselves
arrive **deferred** — the agent must search for `task_update` before it can claim a checkpoint at all.
Field evidence: `docs/dev/FIELD-NOTES-2026-07-29-devcontext.md` section 8, lines 170–172, where
exactly that happened and the session nearly ended without a claim.

SF6.1 folded the fallback into the session template, so an agent that hits it now recovers in one
line. That is the *workaround*. The engine-side fix — merge conductor's server into the harness's
existing config rather than replacing it, and verify the tools are live before the session is told to
depend on them — is deliberately out of the Sarban era's scope and belongs to whoever opens the next.

## Research + polish (queued)

- Survey comparable autonomous multi-session/agent orchestrators; blend useful patterns.
- Terser session prompt templates: drop boilerplate, keep only the contract rules.
- Optional planning step for complex checkpoints (agent decides whether to plan first, not forced).
