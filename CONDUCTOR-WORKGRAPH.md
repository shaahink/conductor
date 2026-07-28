# CONDUCTOR-WORKGRAPH — W-series tracker (One Work Graph)

**Design brief:** `docs/CONDUCTOR-WORKGRAPH.md` (read it + `docs/GAP-ANALYSIS.md` before any
checkpoint). **Plan:** `plans/conductor-workgraph.plan.json`.

## Handoff

**W1.1 DONE 2026-07-28** (+ W6.1 delivered early on owner instruction: MIT LICENSE, publish/
un-tracked from HEAD, ignore hardening — history purge still an open owner decision). W1.1
landed the keystone: one event-sourced work graph — checkpoints table dropped (migration v8),
IRunStore checkpoint methods are now event adapters (emit + fold), seq allocated at persist
time inside the tx (two-writer safe), SeedCheckpointsFromTracker is a single seed path,
CheckpointConfirmed moved to the M4.1 confirm path and folds into TaskGraph. ADR-0002 amended
in place. Gate battery GREEN: 926/926 C# · face-go green · ratchet OK (tests 840≥550, pragmas
38≤38). Known flake to watch: `HostLoggingTests.DryRunWritesJsonLogWithCorrelationProperties`
failed once under full-suite load, passes in isolation (same family as the marker-race fix in
the baseline note).

**W1.2 DONE 2026-07-28** (`9907715`): one WorkGraphSync at run start / ApplyPlanReload /
/plan/edit / /plan/import apply / plan add-stage; upsert-never-clobber + retire-as-archived +
revive + zero-item-stage scaffolds; G13 checks in CollectErrors (inline) + doctor (work
coverage); live HTTP truth gate green (stage added mid-run → card on the board, no restart).
Battery 933/933 · go green · ratchet OK.

**W1.3 DONE 2026-07-28** (`f94528d`): newlyDone from graph claims (session-scoped by
SessionStarted seq), tracker diff = loud transition fallback, hand-edit veto retired, bug #6
fixed at the dispatch root (verify swaps to the PendingVerify.StageId stage). Three live
fake-agent truth gates green, incl. the U-series `newly DONE []` incident shape inverted.
Battery 936/936 · go green · ratchet OK.

**W1.4 DONE 2026-07-28** (`a34b00d`) — **stage W1 CLOSED 4/4.** /state and /tasks serve one
graph fold; card moves are claims (human provenance, verdict-confirmed like any other);
tracker view refreshes in-request on checkpoint card writes; DTOs carry kind/stageId/confirmed
additively (no Face edits needed, goldens hold). Battery 938/938 · go green · ratchet OK.
Owner criteria 1+2 (plan in → Kanban out; everything stays in sync) now demonstrably hold at
the wire level — final proof deferred to W5 per the brief.

**W2.1 + W2.2 + W2.3 DONE 2026-07-28 — stage W2 CLOSED 3/3.** The claim path now works on the
REAL provider, the board is live mid-session, and the prompt is honest. Battery 949/949 · go
green · ratchet OK (tests 863≥550, pragmas 38≤38, archdebt 0).

The W2.1 real-provider gate earned its keep: with every wire test green, a live claude session
still could not reach a single conductor tool. Three separate defects, none visible to a test
client we wrote ourselves — a JSON-RPC envelope carrying `"error":null` next to `result` (the
spec forbids both; the server sat at `status:"pending"`), a hardcoded 2024-11-05 protocol
version instead of negotiating, and `tools/call` returning a bare payload with no MCP `content`
envelope, so tools that "succeeded" rendered as *(completed with no output)*. Fixed, then
re-proven live: `status:"connected"`, `task_list` returning readable JSON, and an in-worker
`conductor task --done` landing as `newly DONE [R1.1]`. Three runs, ~$0.08 total, cheap tier.
**Lesson for W5: our own MCP test client is too lenient to be evidence.** Anything asserting
agent-visible behaviour needs the real CLI or a spec-strict fixture.

Two test-suite notes for whoever runs the battery next. `HostLoggingTests.DryRunWrites…` — the
known W1.1 flake — is FIXED: Serilog's file sink is process-global, so a parallel host
interleaves its lines, and the test asserted the *first* runId-bearing line was its own instead
of scanning for it. `B12_3Tests.MutatingLane_WorktreeCleanedUp_AfterCompletion` failed once in
~8 full runs and passes in isolation; it is a pre-existing sibling-test temp-dir race its own
comment anticipates, untouched here — worth closing before W6.2 makes CI depend on it.

**Next = W3.1** (independent watchdog timer). W3 is independent of W1/W2; W4 depends on both.

Driving mode: Claude Code drives W1–W4 + W6 directly (owner directive 2026-07-16) — per
checkpoint: pre-session ritual (tracker + brief stage section + cited docs, gate battery first,
never build on red) → deliver → gate battery → fill the checkpoint row (Status DONE + Commit +
Evidence) → overwrite this Handoff block → commit + push. W5.1 = conductor drives a toy plan
(credential-free); W5.2 = `HUMAN:` owner-started real-model run.

Owner decisions pending (needed no earlier than the stage that names them):
- `HUMAN:` W5.2 — owner starts + pays for the real-model proof run.
- `HUMAN:` W6.1 — ~~license choice~~ **MIT (decided 2026-07-28, delivered `51911f9`)**;
  `publish/` un-tracked from HEAD same commit. Still open: whether to also purge `publish/`
  from git *history* (`git filter-repo` + force-push — rewrites remote history).

## Checkpoints

| Checkpoint | Title | Status | Commit | Evidence |
|---|---|---|---|---|
| W1.1 | Unify checkpoints + tasks into one event-sourced graph (kind, provenance; checkpoints table → projection; ADR-0002 amended) | DONE | cac48c7 | TaskAdded +kind/stageId, TaskStatusChanged +commit/evidence/source, CheckpointConfirmed folds; checkpoints table DROPPED (v8) — GetCheckpoints folds the log, write methods emit events (all callers unchanged); persist-time seq allocation kills the two-writer PK collision; single seed path (G4 gone); 8 truth-gate tests in W1WorkGraphTests incl. byte-for-byte replay + second-writer safety; battery 926/926 · go green · ratchet OK |
| W1.2 | WorkGraphSync at every boundary (start, reload, plan edit/import, add-stage); coverage validation in CollectErrors + doctor | DONE | 9907715 | One WorkGraphSync (Core/Planning): add/refresh-title/retire-as-archived/revive + zero-item-stage scaffold ({stage}.1), upsert-never-clobber, torn-tracker safety rail; wired at run start, ApplyPlanReload, /plan/edit, /plan/import apply, plan add-stage ("don't forget" printout deleted); archived status folds + excluded from GetCheckpoints and /tasks; G13: CollectErrors validates inline progress.checkpoints coverage, doctor gains work-coverage check (orphan=fail, uncovered=warn); truth gate GREEN — live paused run + real HTTP /plan/edit stage add → scaffolded card on GET /tasks + regenerated tracker, no restart (W1WorkGraphSyncTests, 7 tests); battery 933/933 · go green · ratchet OK |
| W1.3 | Claims from the graph; tracker demoted to generated view; bug #6 fixed (verify consumes PendingVerify.StageId) | DONE | f94528d | newlyDone = graph claims between SessionStarted seq and verdict (VerdictEngine.Claims.cs); tracker diff demoted to flagged fallback (log WARNING + legacy-claim ledger); M4.1 veto gone; GraphStageDone joins completeness; bug #6 fixed at dispatch root (SessionRunner swaps to PendingVerify.StageId stage — prompt, SessionStarted, record, score all name the delivered stage); 3 live truth gates in W1ClaimPathTests incl. the U-series newly-DONE-[] incident inverted; battery 936/936 · go green · ratchet OK |
| W1.4 | One projection for all views (sidebar/chips + Kanban); card moves = claims through legal transitions | DONE | a34b00d | /state folds the graph (GraphTrackerSnapshot) — same projection as GET /tasks, G11 impossible; checkpoint card moves/edits refresh the tracker view in-request (idle drag = real claim, human provenance, unconfirmed — G6 both ways); TaskDto +kind/stageId/confirmed (additive, zero Go changes, goldens hold); 2 live wire truth gates in W1OneProjectionTests (verdict flip on board+sidebar while parked before next session; idle drag lands claim + tracker regen); battery 938/938 · go green · ratchet OK |
| W2.1 | Claude-shaped MCP config + CONDUCTOR_PLAN in child env; in-worker task/bug/note verbs work | DONE | | WireMcpServer emits BOTH dialects (opencode `{mcp:…}` via OPENCODE_CONFIG + claude `{mcpServers:{type:stdio,command,args}}`), `--mcp-config`/`--strict-mcp-config` appended for claude-provider agents only and never when the plan wires MCP itself; `CONDUCTOR_PLAN` in the child env (kills the U-series "Multiple plan files found" crash). **The real-provider gate found three bugs the wire tests could not:** JSON-RPC responses carried `"error":null` beside `result` (spec forbids both → server stuck `status:"pending"`); `initialize` hardcoded protocolVersion 2024-11-05 instead of negotiating; and `tools/call` returned a BARE payload with no MCP `content` envelope, so every tool rendered as "(completed with no output)" — wired and unreadable. All three fixed. Live claude-haiku-4.5 run (`$0.02`): `mcp_servers:[{name:conductor-tasks,status:"connected"}]`, agent called `mcp__conductor-tasks__task_list` and READ `{"tasks":[…],"count":1}`, then `conductor task --done R1.1` in-worker → `newly DONE [R1.1]`. 5 wire tests (W2McpWiringTests) + envelope-shape gate in McpTaskServerTests |
| W2.2 | Live board mid-session (journal folded on read or direct events; single id allocator) | DONE | | MCP task/note writes go straight into run.db when a store is wired (`WriteEvent` + flush) — the journal stays the sink only for storeless standalone `mcp-serve`; `McpServeCommand` stamps `SetRunId` first (unset = events persisted under `""`, i.e. invisible); `RefreshGraph()` re-folds the authoritative log before every task read/write, so `task_list` sees cards the owner added mid-session and `TaskWrites.BuildAdd` allocates ids against every writer instead of a start-of-process snapshot (G10's two allocators → one); a note now survives a killed session instead of dying with an unfolded journal. 3 tests (W2LiveBoardTests): MCP writes visible on `GET /tasks` with `SessionStarted` present and `SessionFinished` absent (i.e. provably mid-session), HTTP+MCP double-add keeps BOTH cards with distinct ids, storeless fallback still journals |
| W2.3 | One prompt composition (PromptBuilder renders PromptComposer blocks); ToolContract rewritten to one claim path | DONE | | New `PromptBlockRenderer` (Conductor.Planning) is the ONE place blocks become prompt text: the session prompt's task-scoped section and `GET /prompt/blocks` both render through it, and the DTO gains `promptSection` (additive — zero Go changes, goldens hold). Card TITLE now reaches the prompt, not just owner context — a titled card with no note used to be invisible to the session delivering it; the checkpoint card itself is skipped unless it carries context (the prompt already names it). ToolContract + `session.md`/`resume.md` built-ins + `plans/baton-templates/*` rewritten to exactly one claim path (G7/G8): the verb is the only channel, tracker checkpoint rows are generated, and the handoff block stays explicitly the agent's to write (RunLoop.Plumbing reads it back). Truth gate: live wire test byte-compares the card detail's `promptSection` against `session-001.prompt.md` on disk |
| W3.1 | Independent watchdog timer (hard timeout + stall, bg-only liveness, clock-jump check, hung-session notification) | TODO | | |
| W3.2 | Auth failure first-class (401 classified → auth-park; doctor/preflight auth smoke test) | TODO | | |
| W3.3 | Process rails (CTRL_CLOSE graceful stop; pid-reuse guard; unbounded-spend warning; bg log pump fix) | TODO | | |
| W4.1 | Import carries checkpoints end-to-end; imported plans drivable immediately; deterministic default gates | TODO | | |
| W4.2 | conductor init --from-idea + advisor block scaffold (one command: idea → drivable plan) | TODO | | |
| W4.3 | AI split-into-subtasks on a card; stage-level rough-card add (schedulable) | TODO | | |
| W4.4 | Per-item QA dial (qa: inherit/verify/off on the card, honored by QaPolicy) | TODO | | |
| W5.1 | Credential-free dress rehearsal: imported toy plan driven end-to-end, all in-flight levers exercised, first RunFinished | TODO | | |
| W5.2 | HUMAN: real-model unattended proof run start → RunFinished; five criteria audited in docs/workgraph/W5-AUDIT.md | TODO | | |
| W6.1 | HUMAN: LICENSE; un-commit publish/ (± history purge); .gitignore/.gitattributes hardening | DONE (HEAD only) | 51911f9 | MIT chosen by owner 2026-07-28; publish/ (81 files) un-tracked from HEAD; .gitignore + .gitattributes hardened. History purge NOT done — needs explicit owner go-ahead (rewrites remote history). |
| W6.2 | CI: .github/workflows/ci.yml (windows full battery + ubuntu dotnet/go), born green | TODO | | |
| W6.3 | README overhaul (prereqs, platform, badges, VHS demo GIF); quickstart fixed; docs index | TODO | | |
| W6.4 | Repo hygiene (archive trackers, scrub foreign refs, CONTRIBUTING/SECURITY); merge to master | TODO | | |
