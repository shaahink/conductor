# Conductor Gap Analysis — why the loop broke, and the road back (2026-07-27)

**Commissioned by the owner:** "Plans populate tasks on a Kanban; tasks stay in sync when the plan
changes; the conductor drives through the tasks autonomously; and the app is AI-native — type a
rough task with details, the AI plans it and breaks it into tasks, and the conductor goes ahead.
These concepts feel scattered across iterations; I stopped using it. Find the gaps."

Four independent deep-dives (data model & sync · run-loop autonomy · AI-native chain · repo
readiness) were run against the tree at `feat/foreman` (HEAD `2860d63`). This document is the
synthesis. Every claim carries a file/line or run-artifact pointer.

---

## Verdict

The owner's diagnosis is correct, and it is one root cause wearing four costumes:

> **There is no single source of truth for "what work exists and what is done."** Six eras each
> added a partial answer (tracker markdown → run.db checkpoints → task events/Kanban → plan
> import), none was retired, and no sync was ever built between them. Everything else — the dead
> Kanban, the `newly DONE []` incident, the undrivable imported plans, the severed AI-native
> chain — falls out of that.

Secondary, independent finding: even with perfect sync, the engine cannot yet run unattended on a
real provider — the agent's claim path is broken on the claude CLI, the stall watchdog is dead
code, the hard timeout has no independent timer, and expired auth reads as a generic error.
**The engine has never completed a plan with a real model: `run.db` contains no `Completed` run
and no `RunFinished` event has ever been emitted** (`VerdictEngine.CompletePlan` has never
executed). The single real attempt (U-series, $139.68, 13 sessions) confirmed 3 of 4 stages and
died in the last one.

---

## 1 · The work-item model: three stores, zero syncs

### What exists

| Store | Contains | Written by | Synced to others? |
|---|---|---|---|
| `plans/*.plan.json` | stages + gates — **no checkpoints** (`StageConfig` has no such field) | owner, `/plan/edit`, `/plan/import` | **No** |
| Tracker markdown (e.g. `CONDUCTOR-UX-START.md`) | the checkpoint table — the actual work items | agent hand-edits **and** `TrackerGenerator` (overwrites from run.db each session end) | one closed loop, see below |
| `run.db` `checkpoints` table | checkpoint status/commit/evidence — **mutable rows, not event-sourced** (violates ADR-0002) | seeding, `conductor task --done`, verdicts | veto-only input to verdicts |
| `run.db` events → `TaskGraph` | **the Kanban board** | seeded **once at process start** from the tracker; then only humans + agent MCP journal | **No** |

- The stage↔checkpoint link is a string convention (`TrackerParser.cs:24` — split id on first
  dot). Nothing validates a stage has checkpoints or a checkpoint has a stage — not
  `PlanConfig.CollectErrors`, not `doctor` (sync gap **G13**). A stage with zero rows becomes a
  runtime `NeedsHuman`, not an authoring-time error (`RunLoop.cs:159-164`).

### The gaps (sync agent's G1–G13, condensed)

- **Plan edits never touch tasks.** `/plan/edit`, `/plan/import`, and `ApplyPlanReload`
  (`RunLoop.cs:399-443`) save the plan and swap it into the loop — no checkpoint write, no
  `TaskAdded`, no tracker row. `PlanAddStageCommand.cs:59` even prints *"Don't forget to add
  checkpoint rows to the tracker."* The intended "tweak the plan → tasks update" sync **does not
  exist in any direction**.
- **Kanban seeds once per process** (`RunLoop.cs:80`), never on reload; a mid-run tracker edit
  that adds a checkpoint is **destroyed** by the session-end tracker regeneration
  (`RunLoop.Plumbing.cs:228`) because the row was never seeded into run.db.
- **Checkpoint-done and card-done are unconnected both ways.** The engine emits
  `TaskStatusChanged` only during seeding; dragging a card to Done changes no checkpoint, no
  tracker row, no verdict — even though since `7f2b88b` the card id *is* the checkpoint id.
- **Agent MCP task writes are invisible until session end** — journaled to
  `mcp-journal.jsonl`, folded into run.db only at `SessionRunner.cs:322`; `GET /tasks` never
  reads the journal. The "live board" lags a full session.
- **The done-ness signal contradicts the instructions.** `VerdictEngine.cs:281-303` computes
  `newlyDone` from the **tracker-markdown diff** (run.db is only a hand-edit veto). But
  `ToolContract.cs:61-63` tells the agent *"editing its rows by hand achieves nothing… report
  through the verb"*, and `conductor task --done` writes **only** run.db — structurally invisible
  to the verdict. An agent that follows the contract literally produces `newly DONE []`.
  **This is exactly the U-series incident:** session #11 delivered 81 minutes and 4 commits,
  reported via `task --done`, and the engine logged `newly DONE []`
  (`.conductor/logs/conductor-20260717.log:217`).
- Assorted: seeding disabled B9.2 sub-task decomposition on resumed runs
  (`RunLoop.Control.cs:156-161`); two task writers allocate ids independently and
  `TaskGraph.Fold` first-write-wins drops collisions; Face renders two truths side by side
  (sidebar = tracker via `/state`, Kanban = events) — the reported "Kanban empty while the
  sidebar shows a full plan".

**The owner's intended model was actually written down and deferred:** `.conductor/handovers/F1.md:49`
(FU-F1-03) — *"Migrating to pure DB-first seeding (checkpoints defined in plan JSON) would
eliminate the parse round-trip. Design decision for F2."* Never done.

---

## 2 · The autonomous loop: why the U-series run died, and would die again

Ranked by the run-loop agent; all confirmed against the actual U-series artifacts.

1. **BLOCKER — the worker cannot record a claim on the real provider.**
   MCP wiring is opencode-shaped only (`SessionRunner.Mcp.cs:79-95` writes `OPENCODE_CONFIG`);
   the claude CLI ignored it — `session-013.jsonl` reports `"mcp_servers":[]`. The CLI fallback
   (`conductor task --done`) then **crashes inside the worker**: with 7 plans in `plans/` and no
   `CONDUCTOR_PLAN`/`--plan` passed to the child env (`AgentSession.cs:113-116`),
   `PlanSettings.cs:43-47` throws "Multiple plan files found" — four `crash-*.log` files from
   sessions #11/#12 prove it. And even a successful `task --done` is invisible to `newlyDone`
   (§1). Three independent breaks stacked on the claim path.
2. **BLOCKER — the 15-minute stall watchdog is dead code.** `StallDetector` treats *any* live
   tracked pid as activity (`StallDetector.cs:52-56`, no purpose filter) — and the agent's own
   pid and the face's pid are always tracked. Zero `stall:` lines exist in any engine log ever
   written.
3. **BLOCKER — bug #8: the 90-min hard timeout has no independent watchdog.** It's a wall-clock
   check inside the poll loop (`SessionRunner.cs:286-291`); session #12 hung at ~2 minutes and
   the timeout fired at **337 minutes**. No notification fires for a hung session (Telegram only
   on NeedsHuman/session-end).
4. **HIGH — bug #6 (open): post-advance Verify sessions are told the wrong stage.**
   `SessionRunner.Kinds.cs:55` passes the loop's *current* stage; `PendingVerify.StageId` is
   written and read by nothing. All three U-series verifies produced nothing usable → three Fix
   sessions → 50% retry rate. **Will misfire on every stage of the next run.**
5. **HIGH — expired OAuth reads as generic `AgentError`.** The wire classifier matches
   quota/429 but not 401/`authentication_failed` (`ProviderText.cs:8-13`); session #13's raw log
   contains the 401 verbatim. `doctor` has no auth smoke test; and the advisor is the *same*
   claude CLI, so escalation judgement dies with the credential.
6. **HIGH — window close kills the run** (`Console.CancelKeyPress` only; `SetConsoleCtrlHandler`
   unwired — §7.5 of OPERATING-CONDUCTOR); recovery exists (`RecoverFromCrash`) but only when a
   human retypes `conductor run`.
7. **MEDIUM** — self-driving runs execute stale engine code (bug #4; burned sessions 3/6/7 on an
   already-fixed defect); unbounded spend by default (U-series plan set no cost cap → $139.68);
   pid-reuse hazard in `ReapOrphans` (3 stale unexited pids in run.db right now); `conductor bg
   start` log pump broken (bug #2) — inverts the "use bg for >3 min" instruction.

The repo's own `watch-run` skill (a second agent babysitting the log) exists precisely because
these rails don't hold.

---

## 3 · The AI-native chain: strong at both ends, severed in the middle

What the owner wants: type a rough idea in the app → AI plans it → broken into tasks → drive.

- **The last mile is genuinely good** (live editing, reload-plan, QA dial, card detail with
  advisor refine, prompt→diff→apply in the Face) — built, tested, golden'd.
- **The first mile doesn't exist.** The Face only attaches to a *running* control plane, which
  only exists inside `conductor run`, which requires an already-valid plan + tracker. **The app
  you'd type the idea into cannot exist until the plan already does.**
- **The middle is severed in one specific place:** `MarkdownPlanParser` parses full checkpoint
  lists and `ToImportResult` (`MarkdownPlanParser.cs:117-135`) **discards them**, keeping only a
  session-count estimate. `ImportResult` carries stages+gates only; the advisor's JSON contract
  (`PlanImportService.cs:167-186`) has no checkpoint key either; nothing writes tracker rows for
  imported stages. Net: **every imported plan is undrivable** until a human hand-authors the
  checkpoint table. (F7.1 in `CONDUCTOR-VNEXT-PLAN.md:95` promised exactly this and it was never
  delivered.)
- Supporting breaks: `conductor init` writes no `advisor` block, and both prose-import ingresses
  hard-refuse without one — so the documented bootstrap (`init` → `plan import`) can't use the
  AI path; the deterministic path emits zero gates; "break this task into subtasks" doesn't
  exist anywhere (`CheckpointPlanner.Decompose` is a literal split on `→ + — ;`); `planDoc`
  briefs are unreachable from the app and nothing generates them; a rough task can't seed the
  graph (cards require an existing checkpoint parent).

---

## 4 · GitHub readiness

Engineering is presentable (analyzers-as-errors, ~890 C# tests + Go suite, ADRs, ratchet
anti-cheat gate, `PackAsTool` wiring, CI-deterministic builds pre-wired). The repo presentation
is not:

- **No LICENSE** (hardest blocker), no CONTRIBUTING/SECURITY, no `.github/` on any branch ever,
  **zero CI**, zero images repo-wide.
- **72 MB of accidentally committed build output** in `publish/` (81 binaries, incl. iOS static
  libs, from commit `a558d56`) — needs `git rm --cached` + ideally history purge.
- **Public `master` is 944 commits behind** `feat/foreman` (tip dated 2026-07-08); its tip
  commit message names other private projects.
- Root has 11 era tracker docs (2 live); `docs/quickstart.md` contradicts the README (.NET 9 vs
  10); 92 tracked files embed `C:\Code\...` paths; 75 name private projects
  (Shamshir/Loom/DevContext2), incl. `plans/shamshir-p0.plan.json` and `FUSION.md` local paths.
- Secrets hygiene is **clean** (verified: `.conductor/` deny-all gitignore holds, no tokens in
  tracked files, `secrets.local.json` untracked).
- CI matrix is ready-made: build/test/face-build/face-test are portable; ratchet + driver
  scripts are PowerShell (ratchet likely fine under `pwsh` on Linux, untested). Needs
  `dotnet 10.x`, `go 1.26`. Go golden files need `.gitattributes` (line endings).
- README: accurate and well-written but no prerequisites, no platform statement, no badges, no
  screenshot. The face resists piping — use `charmbracelet/vhs` against `--demo` for a GIF.

---

## 5 · Root cause, in one paragraph

Each era answered "what is the unit of work?" for its own needs and moved on: Baton made the
tracker markdown authoritative; Foreman added run.db checkpoints as an anti-cheat veto; the
G-series added a task event graph for the Kanban; the P-series enriched cards (context/paths)
without connecting them to verdicts; plan import grew stages-only because trackers were assumed
hand-authored. Every pairwise bridge was someone else's stage, so none were built. The result is
a system whose *display* (Face) got steadily better while its *spine* (plan → work → claim →
verdict) quietly forked into three unsynchronized copies — and the first real-model run drove
straight into the fork.

---

## 6 · Proposed fix roadmap — the W-series ("one Work graph")

Design keystone (recommended, and consistent with the owner's own deferred FU-F1-03 decision):

> **The event-sourced work graph in run.db becomes the single runtime truth for work items.**
> The plan (or an import) *declares* work; loading/reloading/importing a plan **syncs the graph**
> (upsert by id, provenance-tagged, statuses preserved); the tracker markdown and the Kanban both
> become pure generated views of the graph; verdicts read claims from the graph first-class.
> Checkpoints and Kanban cards unify into one graph (a card *is* a work item; sub-tasks are
> children).

### W1 — One work graph (keystone)
Event-source checkpoints (per ADR-0002); unify checkpoint + task into the one graph; plan
load/reload/import syncs it (add/update/retire, never clobbering status); tracker becomes
view-only output; `newlyDone` = graph claims (tracker diff demoted to legacy input or deleted);
stage↔checkpoint coverage validated in `CollectErrors` + `doctor`. *Closes sync gaps G1–G13 and
the §1 contradiction; bug #6's data model (PendingVerify.StageId) gets consumed here too.*

### W2 — The claim path works on the real provider
Claude-shaped `--mcp-config` (keep opencode support); inject `CONDUCTOR_PLAN` (+ `--plan`) into
the child env; fold the MCP journal live (or emit events directly); rewrite the
PromptBuilder/ToolContract contract so there is exactly one instructed claim path. *Closes
driving gap 1; live board becomes actually live.*

### W3 — Autonomy rails
Independent watchdog timer for hard timeout + stall (StallDetector filtered to `bg:*` purposes;
monotonic-clock jump detection); 401/auth classified → park immediately with re-auth
instructions + `doctor`/preflight auth smoke test; `SetConsoleCtrlHandler` graceful close;
default budget caps (warn at start when unbounded); pid-reuse guard in `ReapOrphans`; hung-session
notification. *Closes driving gaps 2/3/5/6 + the medium tail.*

### W4 — AI-native bootstrap
`ImportResult` carries checkpoints end-to-end (parser already has them; advisor contract gains
the third key; apply writes them into the work graph → tracker view regenerates itself);
`conductor init` gains an advisor block + `--from-idea <prose>` (init → advisor plan → seeded
graph, drivable immediately); deterministic path proposes default gates from repo detection;
task-level "split into subtasks" via the advisor on a card. *Closes the AI-native chain; the
"serverless authoring mode" (Face before a run exists) is deliberately deferred — CLI + Face
Prompt tab cover authoring once W4 lands.*

### W5 — Proof run (the gate)
One real-model, multi-stage plan driven end-to-end **unattended** — the first `RunFinished`
event in the project's history. Budget-capped, watch-run armed but expected idle. Fix what
bleeds. *This is the acceptance test for W1–W4; without it the series isn't closed.*

### W6 — GitHub-ready
LICENSE (owner picks MIT/Apache-2.0); un-commit `publish/` (+ optional history purge); merge to
`master`; `.github/workflows/ci.yml` (windows-latest full battery incl. ratchet under pwsh;
ubuntu-latest dotnet+go legs); README: prerequisites, platform note, badges, VHS demo GIF;
archive historical trackers to `docs/archive/trackers/`; scrub foreign-project refs + evidence
logs; CONTRIBUTING/SECURITY; `.gitignore`/`.gitattributes` hardening; fix `docs/quickstart.md`.

### Sequencing note
W1→W2 are ordered (W2's claim path lands on W1's graph). W3 is independent and can interleave.
W4 depends on W1. W5 gates the series. W6 is independent but should land last so CI is born
green on the fixed engine.

---

## Appendix — primary evidence artifacts

- U-series run: `run.db` run `1a7c1714`, `.conductor/logs/conductor-20260717.log`,
  `.conductor/logs/session-011..013.jsonl`, `.conductor/logs/crash-20260717-*.log`,
  `.conductor/REPORT.md`, `CONDUCTOR-UX-START.md:7-9,24-26`.
- Known-gap ledgers: `docs/OPERATING-CONDUCTOR.md` §7, `docs/CONDUCTOR-UX.md` appendix,
  `.conductor/followups.md`, run.db `bugs` (ids 1–7; "bug #8" was never persisted — the filing
  crashed, see driving gap 1).
- Deferred design intent: `.conductor/handovers/F1.md:49` (FU-F1-03),
  `CONDUCTOR-VNEXT-PLAN.md:95` (F7.1), `docs/CONDUCTOR-PLANNER.md:48-49,120`,
  `docs/NEXT-FEATURES.md:77-80`.
