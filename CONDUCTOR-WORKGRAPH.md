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

**W3.1 + W3.2 + W3.3 DONE 2026-07-28 — stage W3 CLOSED 3/3.** The autonomy rails hold. Battery
987/987 · go green · ratchet OK (tests 893≥550, pragmas 37≤38, archdebt 0).

Every W3 defect was a rail that existed on paper and had never once fired. The hard timeout and
the stall detector lived inside the agent poll loop, so they could only act when the loop got
around to them — bug #8's 90-minute limit firing at 337. They now run on a dedicated thread, and
they reconcile two clocks: monotonic time stops while the machine sleeps, wall-clock time does
not, so their per-tick divergence IS the suspend and is excluded from both budgets. Separately,
the stall detector counted *any* live tracked pid as "bg work in progress" — including the agent
being judged and the Face — which is the whole reason no engine log ever written contains a
`stall:` line. An expired credential was classified as a generic agent error and retried; it is
now checked before the usage limit and parks naming its own fix. And `bg start`'s log pump was
killed by the launcher returning, so the sanctioned way to run anything slow produced empty logs
that looked healthy.

Two notes for whoever runs the battery next. `HostLoggingTests.DryRunWritesStructuredLog…` failed
once under full-suite load and passed alone: its 10s Serilog-flush deadline was tight, and W3's
new live runs (real child processes, in parallel) starved it past. Deadline widened — it returns
the instant the content lands, so it costs nothing when healthy. `B12_3Tests.MutatingLane_…`
(the pre-existing sibling temp-dir race noted in W2) did NOT recur in ~6 full runs here, but is
still worth closing before W6.2 makes CI depend on it.

Two W3 items are deliberately proven one level down from the brief's wording, and neither is a
gap in the fix — only in what a test process may do to itself:
- **Window close.** `GenerateConsoleCtrlEvent` cannot synthesise CTRL_CLOSE (the API only emits
  C and BREAK), so the OS-delivery half is unautomatable here. The handler body is gated directly
  (close/logoff/shutdown all stop the run; the handler provably does not return before the save
  signals; Ctrl+C is left to CancelKeyPress), and the "clean park + resumable run.db" half is a
  live gate on the same cancellation path. **`HUMAN:` worth one manual ✕ on a live run before W5.2.**
- **Auth smoke test.** Proven on classification and on scope (a fake agent is never spawned, so
  no test invents a verdict). The paid one-token ping against a real CLI rides W5.2.

**Next = W4.1** (import carries checkpoints end-to-end). W4 depends on W1+W2, both closed.

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
| W2.1 | Claude-shaped MCP config + CONDUCTOR_PLAN in child env; in-worker task/bug/note verbs work | DONE | af74204 | WireMcpServer emits BOTH dialects (opencode `{mcp:…}` via OPENCODE_CONFIG + claude `{mcpServers:{type:stdio,command,args}}`), `--mcp-config`/`--strict-mcp-config` appended for claude-provider agents only and never when the plan wires MCP itself; `CONDUCTOR_PLAN` in the child env (kills the U-series "Multiple plan files found" crash). **The real-provider gate found three bugs the wire tests could not:** JSON-RPC responses carried `"error":null` beside `result` (spec forbids both → server stuck `status:"pending"`); `initialize` hardcoded protocolVersion 2024-11-05 instead of negotiating; and `tools/call` returned a BARE payload with no MCP `content` envelope, so every tool rendered as "(completed with no output)" — wired and unreadable. All three fixed. Live claude-haiku-4.5 run (`$0.02`): `mcp_servers:[{name:conductor-tasks,status:"connected"}]`, agent called `mcp__conductor-tasks__task_list` and READ `{"tasks":[…],"count":1}`, then `conductor task --done R1.1` in-worker → `newly DONE [R1.1]`. 5 wire tests (W2McpWiringTests) + envelope-shape gate in McpTaskServerTests |
| W2.2 | Live board mid-session (journal folded on read or direct events; single id allocator) | DONE | af74204 | MCP task/note writes go straight into run.db when a store is wired (`WriteEvent` + flush) — the journal stays the sink only for storeless standalone `mcp-serve`; `McpServeCommand` stamps `SetRunId` first (unset = events persisted under `""`, i.e. invisible); `RefreshGraph()` re-folds the authoritative log before every task read/write, so `task_list` sees cards the owner added mid-session and `TaskWrites.BuildAdd` allocates ids against every writer instead of a start-of-process snapshot (G10's two allocators → one); a note now survives a killed session instead of dying with an unfolded journal. 3 tests (W2LiveBoardTests): MCP writes visible on `GET /tasks` with `SessionStarted` present and `SessionFinished` absent (i.e. provably mid-session), HTTP+MCP double-add keeps BOTH cards with distinct ids, storeless fallback still journals |
| W2.3 | One prompt composition (PromptBuilder renders PromptComposer blocks); ToolContract rewritten to one claim path | DONE | af74204 | New `PromptBlockRenderer` (Conductor.Planning) is the ONE place blocks become prompt text: the session prompt's task-scoped section and `GET /prompt/blocks` both render through it, and the DTO gains `promptSection` (additive — zero Go changes, goldens hold). Card TITLE now reaches the prompt, not just owner context — a titled card with no note used to be invisible to the session delivering it; the checkpoint card itself is skipped unless it carries context (the prompt already names it). ToolContract + `session.md`/`resume.md` built-ins + `plans/baton-templates/*` rewritten to exactly one claim path (G7/G8): the verb is the only channel, tracker checkpoint rows are generated, and the handoff block stays explicitly the agent's to write (RunLoop.Plumbing reads it back). Truth gate: live wire test byte-compares the card detail's `promptSection` against `session-001.prompt.md` on disk |
| W3.1 | Independent watchdog timer (hard timeout + stall, bg-only liveness, clock-jump check, hung-session notification) | DONE | 6755772 | `SessionWatchdog` runs the hard timeout + stall rails on a dedicated background thread — the kill is no longer gated on the poll loop (bug #8: a 90m limit that fired at 337m); monotonic-vs-wall divergence per tick IS the machine's sleep and is excluded from BOTH budgets (a backwards NTP step is reported and ignored); `AnyBgProcessAlive` counts `bg:*` purposes only — it used to count the agent's own pid and the Face, which is why no engine log ever written contains a `stall:` line; both rails now notify (Telegram/webhook/notify command) instead of only NeedsHuman parks; nullable seconds-precision limit overrides (null = the existing minute fields) so a toy run — or a live test — can express a rail. 11 tests (W3WatchdogTests): timeout fires while the caller is hard-blocked in `Thread.Sleep`, a 4h wall jump kills neither budget, live silent-agent stall trip + live chatty-hang timeout, each asserting the notify command's output file. Battery 960/960 · go green · ratchet OK |
| W3.2 | Auth failure first-class (401 classified → auth-park; doctor/preflight auth smoke test) | DONE | a71bf83 | `DetectsAuthFailure` on every provider, checked BEFORE the usage limit (backoff cannot mint a token, and the advisor is the same CLI); outcome `AuthFailed` → park naming the fix (`claude setup-token`), no gate battery, no attempts burned; `ClaudeProvider` stops flattening the system envelope, so `error_status:401` reaches the transcript and sets a stream-level `AuthFailure` on the FIRST retry instead of inferring it from result text ten retries later; `AuthSmokeTest` asks the plan's own agent invocation for one token (~$0.001) at run start + from `doctor` (`limits.authPreflight` / `--no-auth-check`), probing recognised provider CLIs only. Truth gate: `tests/…/fixtures/session-013-auth-401.jsonl` is the real U-series session, replayed line for line as a live run's agent → one session, AuthFailed, parked, gate marker absent. 16 tests (W3AuthTests). Battery 976/976 · go green · ratchet OK |
| W3.3 | Process rails (CTRL_CLOSE graceful stop; pid-reuse guard; unbounded-spend warning; bg log pump fix) | DONE | c8f9b56 | `ConsoleCtrlRails` wires CTRL_CLOSE/LOGOFF/SHUTDOWN to the graceful stop and BLOCKS in the OS handler until the run has saved (Windows kills on return, so an early return makes the save decoration) — Ctrl+C stays with CancelKeyPress; `PidLiveness` settles pid identity by start time, so `ReapOrphans` kills only a verified match and a recycled/unverifiable id is logged and released (the stall rail reads the same answer); bug #2 fixed by deleting the pump — the bg child is spawned through the platform shell with the OS doing the redirect, so output survives the launcher's exit by design (same fix applied to MCP `bg_start`, whose pump died at session end); log names carry the start instant (the pid does not exist when the redirect target must) and the pids row stores the same instant, so pid→log stays exact, legacy names still resolve; an uncapped run says so at start and `doctor` warns. 11 tests (W3ProcessRailsTests) incl. live cancel→resumable run.db and a live bg log that keeps filling for 4s after the launcher returned. Battery 987/987 · go green · ratchet OK (pragmas 37≤38) |
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
