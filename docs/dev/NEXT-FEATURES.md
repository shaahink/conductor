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

**Re-checked 2026-08-05 (K7.1).** The Karvan era closed twenty-three checkpoints and none of them
had reached this page, so its output is added to the shipped list below. Every item still on the
*open* list was re-verified against `src/` on that date rather than trusted: `requireCleanTree`,
`RepoMapBattery` and `DefinitionOfDoneBattery` return no hits, `WarnOnBranchPattern` is still the
only enforcement of `branchPattern` (`RunLoop.Control.cs`), so all four entries stand as written.

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
- **The MCP config merges the operator's servers (K1.4).** `OperatorMcpServers` reads what the machine
  already has — `mcpServers` from `~/.claude.json` (user and `projects[<repo>]` scopes) and the repo's
  `.mcp.json`, the `mcp` map from `~/.config/opencode/opencode.json` and the repo's own — and
  `SessionRunner.Mcp.cs` folds it into the per-session config beside `conductor-tasks` instead of
  writing conductor's server alone. `agent.inheritMcpServers: false` opts out. This closes the
  engine-side half of the item filed on 2026-08-01 below; the harness-side half stays open by
  decision, not by omission.

### Added by the Karvan era (K1–K6, 2026-08-04 → 2026-08-05)

- **The engine is a library again (K2.1–K2.3).** `Conductor.Core` holds the domain, orchestration
  and store with no Spectre and no HTTP hosting; `Conductor` is CLI plus hosting. The boundary is not
  a convention — `ArchitectureBoundaryTests` fails the build when it is crossed, naming the type and
  the rule. `ARCHITECTURE.md` is a real map, and its *file-organisation convention* section says where
  a new endpoint, event or partial belongs.
- **Conductor remembers across runs (K3).** State has a machine-level home with a catalogue keyed by
  repo and plan (`StateHome`), an environment override, and an idempotent migration that *imports*
  existing `run.db` files instead of orphaning them. `HistoryCommand` lists and opens past runs
  read-only, and the Face's run picker offers them. Every run records the engine version, its commit,
  its dirty flag and a snapshot of the limits that governed it.
- **Token truth (K4).** Context size per turn — high-water and mean — is derived from the stream.
  `BudgetAnalyzer` behind `conductor budget` prints floor, wrap-up, cap, nudge-versus-floor and
  rollover rate and *prescribes* a correction; `MoneyCommand` answers what a project cost per
  checkpoint, per stage and per month with cache-read share. Live session tokens, distance to the
  nudge, burn rate and projection sit beside live money in the Face and on the wire. This page's own
  token doc was corrected by that tool at K7.1 — see `TOKEN-BUDGET-TUNING.md` §3 and §9.
- **The result contract and the channels (K5).** The session result has one format conductor owns,
  parsed once and rendered by five consumers instead of cut mid-word at five different lengths.
  `EvidenceArtifact` makes evidence first-class — path, kind, checkpoint, session, sha, created-at —
  written as an event and surfaced in the Face. Telegram gained owner-editable per-event templates,
  links for commits and PRs, photo and document sending, a thread per run, and 4096-char chunking.
- **The surfaces read (K6).** `docs/dev/adr/0006` fixes the TUI conventions after an actual read of
  glow, soft-serve, gh-dash and lazygit; `bubbles` v2 is a declared dependency; four panes scroll
  through a real viewport; each tab owns its own model, state, update and view so the root update is
  a dispatch rather than 826 lines; and one theme-aware markdown renderer serves everywhere markdown
  belongs.

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
  *(Re-checked at KS10.1, 2026-08-15: still true — `PlanImportCommand.ExecuteImport` takes a
  `planPath` and diffs into it. What KS3.5 changed is the other half: `PlanImportService.ParseKnown`
  now reads three foreign formats as well as this project's own, selected by content, still with no
  model call. The gap is the missing plan, not the missing parser.)*
- **Gate on unacknowledged handover gaps.** Phase-confirm could fail when the handover lists a
  critical gap nobody acknowledged. The parse exists; the gate does not.

### Filed 2026-08-06 — the two lane gaps, found reading the code to answer "what can run in parallel?"

Both were found by grep over `src/` on 2026-08-06 while advising a live run, not by a failure. Neither
is "not built" in the usual sense — the machinery exists in both cases and the gap is in what reaches
it.

- ~~**`mutatingLanes[]` is plan config that nothing reads.**~~ **Closed by deletion, KS3.3
  (2026-08-14)** — the "delete the field **and** its doc section" branch. `PlanConfig.MutatingLanes`
  declared a block that no code path in `src/` ever touched; its only reference outside the
  declaration was a test asserting it defaults empty. The property, its doc table and the key in four
  shipped plans are gone, `plan set mutatingLanes …` now refuses, and `doctor` names the key as inert
  if an old file still carries it. `KS3_3SchemaHonestyTests.NoSettablePlanRootPropertyIsReadByNothing`
  keeps the general shape from coming back.
  <br>**Still open, and the half the deletion did not touch:** Tier B is reachable from exactly one
  direction. `MutatingLaneRunner`, `Lanes/PathClaimTracker`, worktree isolation and the merge staged in
  a third worktree are all real, but the only path in is
  `LaneCoordinator.RunFollowupFixLanesAsync`, turning `.conductor/followups.md` entries into lanes via
  `FollowupEntryToMutatingLane` after a stage confirms, in a sequential `foreach`. So Tier B lanes are
  never concurrent with anything, and nothing an author writes can schedule one.
- **Lane spend is outside the cost cap.** `LaneRunner`, `LaneWorkerPool` and `MutatingLaneRunner`
  contain no cost or token accounting of any kind, while `maxRunCostUsd` / `maxRunTokens` are computed
  from session costs (`sessions/NNN/cost.json`). Every analysis lane, parallel audit and fix-lane
  therefore spends real money the run's own cap cannot see, and `conductor status` under-reports the
  bill by that amount. Harmless at `maxConcurrentLanes: 1` with no lanes declared, which is what most
  plans have; it scales with exactly the feature the next era wants to promote.

## Filed 2026-08-01 (SF7.1) — the MCP config the engine writes is not the one the harness gets

**Engine-side half: closed by K1.4 (see the shipped list).** `WireMcpServer` used to write a config
containing **only** the `conductor-tasks` server, in both dialects, so any MCP server the operator had
configured was absent from what conductor handed the agent — a user-scope chrome-devtools server was
invisible to every spawned session. `OperatorMcpServers` now merges those servers in.

**Harness-side half: open by decision, not by omission.** In at least one shipped harness conductor's
own tools arrive **deferred** — the agent must search for `task_update` before it can claim a
checkpoint at all. Field evidence: `docs/dev/FIELD-NOTES-2026-07-29-devcontext.md` section 8, lines
170–172, where exactly that happened and the session nearly ended without a claim. Whether a tool is
deferred is the harness's choice and conductor cannot assert otherwise, so the answer stays the
prompt-side fallback SF6.1 shipped: `ToolContract` tells the agent to search for the tool and names
the CLI that works regardless. Do not "fix" this by deleting that line.

## Research + polish (queued)

- Survey comparable autonomous multi-session/agent orchestrators; blend useful patterns.
- Terser session prompt templates: drop boilerplate, keep only the contract rules.
- Optional planning step for complex checkpoints (agent decides whether to plan first, not forced).
