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

**W4.1 + W4.2 + W4.3 + W4.4 DONE 2026-07-28 — stage W4 CLOSED 4/4.** The AI-native chain is joined
end to end and pipeline control reaches the individual card. Battery 1023/1023 · go green · ratchet
OK (tests 924≥550, pragmas 37≤38, archdebt 0).

The severed middle is joined: `MarkdownPlanParser` parsed every checkpoint in a plan document and
`ToImportResult` threw them away, so every imported plan was undrivable until a human hand-wrote
the tracker table (F7.1 promised this in `CONDUCTOR-VNEXT-PLAN.md` and never shipped it). Both
import paths now carry the work, and it lands in the plan's own declared-work channel — which is
the W1 model said plainly: the plan declares the work, the tracker is the generated view of it. The
first mile got an entrance too (`init --from-idea`, plus the advisor block init never wrote), and
"add work in flight" got its second half: a stage-level card the engine schedules, and an AI split
that proposes children the owner confirms one at a time.

One design point worth carrying forward: a card that lives only in the graph is not just
unschedulable — W1.2's sync sees its stage re-declared without it and ARCHIVES it. Anything that
invents a checkpoint mid-run must write it back to the declaration, which both W4.3's stage-level
add and W4.1's import apply now do.

Credential-free throughout: W4.2 and W4.3 prove the advisor paths against a FAKE advisor CLI — a
script printing the import/split contract's JSON, wired exactly as a real model would be. The same
trick is what W5.1 will lean on.

**W5.1 DONE 2026-07-28 — the rehearsal passes, and it earned its keep.** One `conductor.exe`, started
once, took a markdown document to a finished run: 10 sessions, 6/6 checkpoints, exit 0, and
`RunFinished` in `run.db` — the event no conductor run had ever emitted. Write-up:
`docs/workgraph/W5-REHEARSAL.md`; driver: `powershell -File tools/w5/rehearsal.ps1 -Keep` (~90s, no
credentials). Battery 1028/1028 · go green · ratchet OK.

The rehearsal was built out of process on purpose — it drives the shipped binary, moves every lever
over the real HTTP control plane, claims through `conductor task --done` inside the worker, and reads
its verdict back with `conductor report --query`. That is W2.1's lesson applied: a harness we wrote
ourselves is too lenient to be evidence. It paid immediately. Three defects, none of them visible to
any test written before it:

- **The engine was the last reader still scheduling on the DECLARATION rather than the graph.** W1
  settled that the graph is the runtime truth; every reader moved except the run loop. On the
  markdown-table path that is invisible, because the tracker is regenerated from the graph after each
  session and agrees a moment later. An inline (`plan-checkpoints`) plan — what *every* W4.1 import
  produces — has no write-back at all: it declares `TODO` forever. So the assignment policy re-picked
  cards the graph already had as delivered, the prompt's card section rendered EMPTY (it reads the
  graph, where a done card is history), the circuit breaker correctly called that no progress, and the
  run parked `NEEDS HUMAN` at `0/5 done` with work actually delivered. `AllEffectivelyDone` could
  never be true, so a plan imported from a document could not reach `RunFinished` at all. W4.1's own
  live test missed it by running `Once: true` — one session is exactly the horizon at which the two
  sources still agree.
- **The plan reload skipped the control plane**, so a plan edit reached the engine and the tracker
  while every Face surface served the pre-edit plan for the rest of the run. No in-process test could
  see it: they all read `_ctx.Plan` directly.
- **Completion closed the run over the verification it had just queued** — uncovered by the first fix,
  because done-ness used to lag a tracker regeneration behind the claim and the queued verify always
  got its turn first. The blast radius is the plan's LAST checkpoint in every run: the only card
  nobody independently checked.

Two things worth carrying to W5.2 rather than fixing blind (both in the write-up): the prompt names
the STAGE and tells the agent to read the tracker, but never names the checkpoint the engine will judge
the session against — so the agent re-derives by parsing markdown what `assignment.Items` already
knows, and a disagreement is silent. And `report --query`'s `events.type` column holds the CLR event
name (`RunFinished`), not the JSON discriminator (`runFinished`); querying the wire name returns "no
rows", which reads exactly like a defect.

**W6.2 + W6.3 + W6.4 DONE 2026-07-28 — the repo is GitHub-ready; W6.4 is PARTIAL by two deliberate
calls, both `HUMAN:`.** Battery 1028/1028 · go green · ratchet OK. **CI is green on both legs**
(run `30352861283`), which is the only evidence that counts for "born green" — the badge is live.

CI paid for itself on its first run, and in the same currency W5.1 did: two defects that no local run
could see. The self-plan test loaded a committed plan through `PlanConfig.Load`, which *validates*,
and validation asserts `plan.repo` exists — so a test about one boolean field failed on every clone
that is not the owner's directory. And a W2 truth gate waited on `File.Exists`, which is true the
moment the engine *creates* the prompt file and still holds the handle; a local SSD closes that
window, a 4-core runner does not. Neither is a flake. Both fixed, then re-proven green on the runner.

The pattern is now three for three: **W2.1 (real provider), W5.1 (real binary, out of process),
W6.2 (a machine that is not ours) each found defects a fully green suite could not.** Anything that
asserts behaviour we control the shape of is worth less than the same assertion made from outside.

The demo under the README's H1 is built from face-go's **committed golden frames** rather than a
screen recording — the exact bytes `View()` produced, diffed on every CI run — so it cannot drift
from the real Face without a test going red first (`tools/demo/make-demo-gif.ps1`; the VHS tape for
a full-colour recording is committed too, but ttyd has no Windows build). Two documentation defects
turned up while fixing quickstart: it taught a `conductor new-plan --template` flag that **does not
exist**, and its Face keybinding table still described the Ink face retired in M7.

W6.4's two open items are the honest ones. Moving `plans/shamshir-p0.plan.json` to `examples/` —
where the repo's own convention puts project plans — trips the ratchet, which reads a relocation as
removing that file's gate commands and says so: *"Gates are the contract; changing one is a human
decision."* The move was reverted, not worked around. And the 56 `docs/baton/evidence/*-gate.txt`
MSBuild logs were kept rather than scrubbed: 194 KB is not weight worth trading receipts for, in a
repo whose thesis is evidence-or-it-didn't-happen.

**Known intermittent, unresolved:** `HostLoggingTests.DryRunWritesStructuredLogWithRunIdCorrelation`
timed out twice in ~12 full local runs (never in CI yet, never reproducibly, always under load; it
passes in ~700 ms when it passes). Each test uses its own temp `StateDir`, so the long-standing
"Serilog's sink is process-global" explanation in the sibling test's comment does not account for
it. Not fixed — instead the timeout now prints the log directory's contents and sizes, so the next
occurrence distinguishes "the sink never opened a file" from "the file is there but short of the
marker", which are different bugs. Whoever sees it next: that message is the lead.

**Next = W5.2** (`HUMAN:` — the owner starts and pays for the real-model unattended proof run, then
`docs/workgraph/W5-AUDIT.md`). **After W5.2, the merge** of `feat/foreman` → `master`: owner-decided
2026-07-28 to hold it until then, so the first public tip is a proven one. The shamshir relocation
is closed (leave in place, owner-decided same day). Still open from W6.1: the `publish/` history
purge. And still wanting one manual ✕ on a live run: the W3.3 window-close rail, whose OS-delivery
half cannot be synthesised in-process.

Driving mode: Claude Code drives W1–W4 + W6 directly (owner directive 2026-07-16) — per
checkpoint: pre-session ritual (tracker + brief stage section + cited docs, gate battery first,
never build on red) → deliver → gate battery → fill the checkpoint row (Status DONE + Commit +
Evidence) → overwrite this Handoff block → commit + push. W5.1 = conductor drives a toy plan
(credential-free); W5.2 = `HUMAN:` owner-started real-model run.

Owner decisions pending (needed no earlier than the stage that names them):
- `HUMAN:` W5.2 — owner starts + pays for the real-model proof run.
- W6.4 — merge `feat/foreman` → `master` + point branch protection at `master`:
  **owner decided 2026-07-28 — HOLD until after W5.2**, so the first public tip is one that passed
  the real proof run and any defects W5.2 finds are fixed on the branch. The CI badge reads
  "no status" on `master` until the merge lands; that is expected, not a broken badge.
- W6.4 — relocate `plans/shamshir-p0.plan.json`: **owner decided 2026-07-28 — LEAVE IN PLACE.**
  The ratchet's refusal stands; the exception is documented in `examples/README.md`. Closed.
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
| W4.1 | Import carries checkpoints end-to-end; imported plans drivable immediately; deterministic default gates | DONE | c07a882 | `ImportResult.Checkpoints` from BOTH paths — the deterministic parser emits what it already parsed (statuses included, so a re-imported tracker keeps its DONEs), and the advisor contract gains a `checkpoints` key per stage, deserialised through a wire DTO so a plan stage still does not own a checkpoint list; apply lands them in the plan's declared-work channel (inline `progress.checkpoints`), migrating a markdown-table plan with its existing rows folded in FIRST (nothing lost — the W1 model stated plainly: the plan declares, the tracker displays), `script` providers untouched; `RepoKindDetector` moved to Core so `plan import` proposes init's build+test pair instead of zero gates. 8 tests (W4ImportTests): init scaffold + import + run with no hand edits (doctor work-coverage `ok`, board populated), and the real `docs/MAESTRO-PLAN.md` parsed with every checkpoint attached to a declared stage. Battery 995/995 · go green · ratchet OK |
| W4.2 | conductor init --from-idea + advisor block scaffold (one command: idea → drivable plan) | DONE | 580c114 | `init` writes a commented advisor block naming what the advisor is for (prose→plan, refine, split, judge) and what it is never used for (scheduling); `--from-idea "<prose>"`/`<file>` scaffolds then routes the idea through the W4.1 import path — free for a structured doc, advisor-interpreted for prose — and the scaffold's "rename me" stage steps aside once real stages arrive (unless something was delivered against it); prose with no advisor keeps the scaffold intact and names the two ways forward. 6 tests (W4FromIdeaTests) on a FAKE advisor CLI (a script printing the import contract's JSON, wired exactly as a real model would be): idea in → `conductor run --paused` → board is the idea, zero sessions, zero spend. Battery 1001/1001 · go green · ratchet OK |
| W4.3 | AI split-into-subtasks on a card; stage-level rough-card add (schedulable) | DONE | 7994ac6 | `TaskWrites.BuildAdd` gains the stage-level add — a checkpoint-kind item numbered `{stage}.{n}`, ALSO written back into the plan's declared work (without which W1.2's sync would archive it at the next boundary, not merely fail to schedule it); `POST /tasks/split` asks the advisor for children — proposal only, card text framed as untrusted data, count bounded, parser takes the shapes models actually emit (object, fenced, bare array) — and each child lands through the ordinary `/tasks/add`; Face gains `s` (split, enter adds children one at a time) and `N` (stage-level card, which also gives the empty board an answer). 13 tests (W4SplitAndStageCardTests) incl. the live gate: add a stage card mid-run over HTTP → split → confirm both children → next session claims the card the owner invented. Also fixed the pre-existing B12_3 worktree flake for real (scoped by lane id, not creation time). Battery 1014/1014 · go green · ratchet OK |
| W4.4 | Per-item QA dial (qa: inherit/verify/off on the card, honored by QaPolicy) | DONE | 9196fa8 | Work items carry `qa: inherit \| verify \| off` (`TaskDetailEdited.Qa` → `TaskItem.Qa`, `/tasks/edit`, TaskDto — all additive); the item's dial sits above the stage's above the plan's, with a deliberately smaller vocabulary (an item says whether it wants verification, not how to shape the stage) so it maps onto the existing modes and inherits the threshold/audit shape it did not set; the engine consults the CLAIMED item — the first not-done checkpoint of the pre-session snapshot — at dispatch AND at the verdict, both pre-session so a session's own claim cannot move its answer; Face `q` cycles it. 9 tests (W4ItemQaTests) incl. the live gate: one stage, plan dial `everySession`, H0.1 `off` → no verification at all, H0.2 `verify` → the very next session gets one. Battery 1023/1023 · go green · ratchet OK |
| W5.1 | Credential-free dress rehearsal: imported toy plan driven end-to-end, all in-flight levers exercised, first RunFinished | DONE | 2a2293a | `tools/w5/rehearsal.ps1` drives the REAL binary out of process (agent + advisor are token-free scripts): `TOY-PLAN.md` → `init --from-idea` → `doctor` → ONE `run --headless --paused` process → levers over the real HTTP control plane (card context · per-card QA dials · plan edit · stage-level card add · advisor split + confirm children · a QA dial and a card context flipped WHILE the engine runs) → the run finishes itself. **27/27 checks PASS**: 10 sessions, 6/6 checkpoints, exit 0, and `RunFinished{status:Completed, sessions:10, 6/6, seq:80}` as the last event — the event no run had ever emitted. Full write-up + criteria map in `docs/workgraph/W5-REHEARSAL.md`. **Three engine defects found, all fixed:** (1) the engine scheduled on the DECLARATION, not the graph — an inline (`plan-checkpoints`) plan, i.e. every W4.1 import, declares `TODO` for the life of the run, so the assignment policy re-picked delivered cards, the prompt's card section rendered empty (it reads the graph), the circuit breaker correctly called no progress, and the run parked at `0/5 done`; `AllEffectivelyDone` could never be true, so `RunFinished` was unreachable → new `Core/Planning/WorkSnapshot.cs` + `RunContext.ReadWork()`, the same projection `/state` and `/tasks` already served, now with ONE implementation. W4.1's live test missed it by running `Once: true` — one session is exactly the horizon where declaration and graph still agree. (2) `ApplyPlanReload` swapped the plan into the context/gates/lanes/dispatcher but NOT the control plane, so every Face surface served the pre-edit plan for the rest of the run (criterion 2 failing on the read side; invisible to in-process tests, which read `_ctx.Plan`) → `ControlPlaneServer.SwapPlan` + an `onPlanSwapped` hook; `SwapPlan` also rebuilds the progress provider, which captured the inline checkpoint list by value. (3) uncovered by (1): the completion guard named `PendingFix`/`PendingResume` but not `PendingVerify`/`PendingAudit` — harmless only while done-ness lagged a tracker regen behind the claim, so removing the lag let the run close over the verification it had just queued. Blast radius = the plan's LAST checkpoint, in every run, the one card nobody independently checked. 5 tests (W5RehearsalTests) incl. the completion gate (verified to FAIL on the pre-fix read) and the plan-edit-over-HTTP gate. Battery 1028/1028 · go green · ratchet OK (tests 929≥550, pragmas 37≤38, archdebt 0) |
| W5.2 | HUMAN: real-model unattended proof run start → RunFinished; five criteria audited in docs/workgraph/W5-AUDIT.md | TODO | | |
| W6.1 | HUMAN: LICENSE; un-commit publish/ (± history purge); .gitignore/.gitattributes hardening | DONE (HEAD only) | 51911f9 | MIT chosen by owner 2026-07-28; publish/ (81 files) un-tracked from HEAD; .gitignore + .gitattributes hardened. History purge NOT done — needs explicit owner go-ahead (rewrites remote history). |
| W6.2 | CI: .github/workflows/ci.yml (windows full battery + ubuntu dotnet/go), born green | DONE | 06e7b27 | Two legs: `windows-latest` runs the checkpoint battery byte for byte (`dotnet build` → `dotnet test` → face-go build/vet/test → `powershell -File tools/gates/ratchet.ps1`, the exact path), `fetch-depth: 0` because a shallow clone has no `origin/<branch>` and silently degrades the ratchet to absolute floors; `ubuntu-latest` proves the tree is not accidentally Windows-only at compile time + runs the Go suite, and deliberately does NOT run `dotnet test` (those tests spawn PowerShell gates and `.exe` children — a red there would only restate that Linux is not a supported host). **CI earned its keep on its first run: two defects, neither a flake, both invisible on the owner's machine.** (1) `SelfPlanHasBatteryCollapseEnabled` read the committed self-plan through `PlanConfig.Load`, which *validates*, and validation asserts `plan.repo` EXISTS — the self-plan names the owner's checkout by absolute path, so the test failed on every clone that is not `C:/Code/conductor-baton`, i.e. on any contributor's machine. Now deserializes (still proving the JSON binds) instead of loading. (2) `CardDetailBytes_AreTheSessionPromptBytes` waited on `File.Exists`, which flips the instant the engine *creates* `session-001.prompt.md` while it still holds the handle, then read with `File.ReadAllTextAsync` (`FileShare.Read`) and lost the race — a local SSD hides the window, the runner did not. Now reads shared and waits for the section heading that proves the write finished, the shape `HostLoggingTests.ReadLogWhenFlushedAsync` already used. Green on both legs: run `30352861283` |
| W6.3 | README overhaul (prereqs, platform, badges, VHS demo GIF); quickstart fixed; docs index | DONE | 06e7b27 | README gains badges (CI · MIT · .NET 10 · Go 1.26), a **Requirements** table, an honest **Platform** section (Windows is what is *tested*: PowerShell gate shell, Win32 process rails, `conductor-face.exe` — Linux/macOS compile and CI proves it, running the engine there is not yet proven), the credential-free rehearsal + the gate battery as copy-pasteable blocks, and Documentation/Contributing/License sections. **Demo under the H1:** `docs/assets/demo.gif` (7 screens, 218 KB) built by `tools/demo/make-demo-gif.ps1` from face-go's **committed golden frames** — the exact bytes `View()` produced, diffed by `go test -run TestGolden` on every CI run, so the tour cannot drift from the real Face without a test going red first. `docs/assets/demo.tape` is the VHS script for a full-colour recording of the live binary; VHS needs ttyd, which has no Windows build, hence the golden-frame path on the dev box (monochrome — goldens are ANSI-stripped). `docs/README.md` indexes the 138-file tree by era with a read-this-first path. `quickstart.md` fixed: .NET 10 + Go 1.26 (was "≥ 9.0"), `tools\install.ps1` as the build step, and its Face keybinding table — which still described the **retired Ink face** — replaced by a pointer to `face-go/STYLE.md` plus the keys verified against `cmdbar.go`. It also documented `conductor new-plan --template minimal\|dotnet\|node\|shamshir`; `NewPlanCommand.Settings` has **no `--template` option at all**, so all three call sites were rewritten around `conductor init` (which really does detect the repo type) |
| W6.4 | Repo hygiene (archive trackers, scrub foreign refs, CONTRIBUTING/SECURITY); merge to master | PARTIAL | 06e7b27 | Root markdown reduced 14 → 3 (`README.md`, `AGENTS.md`, the live tracker). Seven historical trackers → `docs/archive/trackers/` with all six plans' `tracker` fields repointed so an old era still loads; `NEXT-ERA.md`, `FUSION.md`, `conductor-DEBT.md` → `docs/archive/`; `conductor-CLEANUP.md` deleted. `docs/archive/README.md` maps era → tracker → design authority so nothing is orphaned. New `CONTRIBUTING.md` (the gate battery IS the review; what the ratchet forbids and why; the W2/W5 lesson that in-process tests are too lenient for anything wire-visible) and `SECURITY.md` (private advisory route + a real threat model: the agent runs unsandboxed by design, **the plan file is executable**, the loopback control plane's per-run `X-Conductor-Token` and why reads stay open, prompt injection, and exactly which files can leak secrets). **Two items NOT done, deliberately.** (a) Relocating `plans/shamshir-p0.plan.json` to `examples/` — where the repo's own convention says it belongs — makes the ratchet read the move as removing that file's gate commands: *"Gates are the contract; changing one is a human decision. Put HUMAN: in the handoff instead."* The move was reverted rather than worked around; the gate did exactly its job. `HUMAN:` owner call. (b) The `docs/baton/evidence/*-gate.txt` MSBuild logs (56 files, 194 KB) were kept: they are receipts, not documentation, and deleting evidence to tidy a repo whose whole thesis is evidence-or-it-didn't-happen is the wrong trade for 194 KB. `docs/README.md` now says what they are and why they quote absolute paths. **Merge to master not done** — see handoff |
