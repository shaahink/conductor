# Conductor v4 — "Maestro"

**Design authority.** The tracker is `MAESTRO-TRACKER.md`. The executable plan is
`plans/conductor-maestro.plan.json`. Where this document and a stage note disagree, this document wins.

**Status:** M0 (bootstrap) is DONE — landed by hand before this plan starts, because until it landed the
tool could not drive itself. M1–M9 are executed BY Conductor, one stage per session, deepseek-v4-pro via
opencode.

**No backward compatibility.** This is a personal tool with one user. Nothing is preserved for the sake of
being preserved. Delete freely; the git history is the archive.

---

## 0. Why there is a v4 — read this before you touch anything

Three eras (Conductor → Baton → Foreman) delivered 67 + 40 checkpoints, ~700 tests, and a design doc that
says all the right things. And yet, on 2026-07-11, an audit found:

| # | What was actually true | Evidence |
|---|---|---|
| V1 | **The engine had never once driven a run.** The driver binary is built from `master` (Jul 7), which contains no `RunDb`, no `ProcessSupervisor`, no control plane, no `Verifier`, no `GateOrchestrator`. Every feature of F0–F9 was built, unit-tested, marked DONE — and never executed. | `git cat-file -e master:src/Conductor/Core/RunDb.cs` fails |
| V2 | **The first real run crashed in two seconds.** One agent stdout line with no `part` key threw `InvalidOperationException` on the process callback thread and killed the whole orchestrator. | fixed in `e93a0be` |
| V3 | **The knowledge ledger held zero rows, forever.** The MCP server was wired into every session and *no prompt ever mentioned it*. Same for `conductor bg` (so long commands blocked the foreground and got killed as stalls) and `conductor task` (so the task graph stayed empty). | `select count(*) from ledger` = 0 |
| V4 | **The prompt templates on disk were dead files.** `templatesDir` was accepted in plan JSON and silently ignored; `PromptBuilder` only looked in the plan directory. Every prompt came from a hardcoded C# string. The verifier was told its pass mark was literally `≥{plan.VerifierThreshold}`. | fixed in `b19bb08` |
| V5 | **`run.db` — "the single source of truth" — is a shadow store.** Truth actually lives in `state.json`, and is smeared across `events.jsonl` and a hand-edited `TRACKER.md`. Its schema is defined twice (fresh-create and migrate paths), which is how the ledger's `created_at` column went missing. | 4 sessions in `run.db` vs 32 in `state.json` |
| V6 | **The god classes never got smaller.** `Commands.cs` 2,574 lines / 54 types. `Orchestrator.cs` 2,334 lines. Both were called out in the previous era's design doc as the thing to fix. | `architecture-baseline.json` |
| V7 | **You could not run it.** `conductor run` booted the old Spectre TUI; the new Ink TUI needed a second flag, a second terminal, and a manual `npm run build`. | fixed in `b19bb08` |

**The lesson, and the organising principle of v4:** *a capability that is not exercised does not exist.*
Not the code, not the test, not the doc — the exercise. Every stage below therefore ends in a **truth
gate**: a command Conductor runs itself that proves the feature works on a real run, not a mocked one.
"Tests pass" is necessary and nowhere near sufficient. F0–F9 had green tests the entire time.

---

## 1. What Maestro IS

> A personal dashboard TUI for running long, autonomous plans: it owns the processes, owns the truth,
> scores every delivery, never loses knowledge, and is a pleasure to watch.

**Non-negotiables** (these are the acceptance criteria, in the owner's words):

1. **Observable.** Both the plan *and the conductor itself*. Timeline view. Live plan view. The actual
   console output of what the agent is running. One-command "where are we".
2. **Clean.** No god classes, no tight coupling, a real database layer. Deep surgery is authorised.
3. **Fast.** The build must stay quick enough to iterate. (It is: 19s build, ~25s for 682 tests. This is
   not the bottleneck and the test suite is NOT to be trimmed.)
4. **Templates as content.** Markdown on disk, editable outside the app, with batteries-included domain
   packs. You can see the *compiled* prompt that was actually sent.
5. **Workflows that bend.** Drop QA for a session. Change the model for one step. Big-dev-then-big-audit
   as a first-class shape. Tweaking a workflow must not require an agent to do it for you.
6. **Knowledge compounds.** Agents hand findings to their successors. Bugs are tracked, not re-found.
7. **One user, two terminals.** Two plans at once, no collisions.
8. **AFK.** It runs while you sleep; it tells you when it truly needs you.

**Kill list.** Delete on sight: the Spectre TUI (`Ui/*.cs`, 2,021 lines), `state.json` as a source of
truth, `events.jsonl` as a separate store, the 9-persona system, `PreviewCommand`, and every dead config
key. Deleting code is a deliverable, not a chore.

---

## 2. Architecture

```
   Face (TypeScript + Ink)        the ONLY UI. Disposable: kill it, the run continues.
        |  HTTP + SSE  (127.0.0.1:auto-port, published to .conductor/control-plane.json)
   Engine (C#)                    authoritative. Never depends on a UI or on the CLI.
     Core/Orchestration           RunLoop, SessionRunner, VerdictEngine, GateOrchestrator, WorkflowEngine
     Core/Store                   IRunStore + SqliteRunStore. The ONLY place SQL exists.
     Core/Prompt                  PromptBuilder, ToolContract, packs. Renders to a file you can read.
     Core/Process                 ProcessSupervisor, Job Objects, bg primitives.
   Store (SQLite, .conductor/run.db)   ONE truth. Everything else is a projection of it.
```

**AD-1 — The engine is authoritative and UI-free.** Enforced by a test
(`ArchitectureTests.CoreDoesNotDependOnTheCliOrAnyUi`), not by good intentions.

**AD-2 — The Face is disposable.** It is spawned as a child of `conductor run` and inherits the terminal.
If it dies, the run does not. `conductor face` reattaches. This is why the engine keeps its console sink
off when a Face is attached.

**AD-3 — `run.db` is the single truth, and this time we mean it.** `state.json` and `events.jsonl` are
DELETED, not "kept alongside". An `events` table inside `run.db` is the append-only spine; every other
table is a projection folded from it. `TRACKER.md` is a generated view — read-only for agents.
Resumability is proven by killing the process mid-session and restarting from `run.db` alone.

**AD-4 — Claims vs. confirmations.** An agent may CLAIM a checkpoint (`conductor task --done`). Only the
engine CONFIRMS one, and only after (a) the gate battery is green at this SHA and (b) the Verifier scores
it ≥ threshold. Hand-editing the tracker does nothing, because the tracker is regenerated from the
database. **This is the mechanism that makes gates inescapable** — there is no path from "agent says so"
to "checkpoint is done".

**AD-5 — Gates interlock.** `ArchitectureTests` enforce the design and run in every battery.
`tools/gates/ratchet.ps1` forbids deleting them, lowering their floors, raising their ceilings,
suppressing the analyzer, softening a gate command, or editing the gate script. Each denies the other's
escape hatch. Verified adversarially — see the commit message on `e2a24aa`.

---

## 3. Stage map

Dependencies: `M1 → M2 → M3 → M4 → M5 → M6 → M7 → M8 → M9`. Mostly linear on purpose: this is a
deconstruction, and parallel lanes over a moving foundation is how the last three eras produced code
nobody ran.

### M0 — Bootstrap (DONE, by hand, before this plan)
Landed in `e93a0be`, `b19bb08`, `e2a24aa`. The crash fix; `conductor run` as one command (engine +
control plane + Face, one process tree); port auto-scan + `control-plane.json` discovery so two plans can
run in two terminals; the prompt contract (`{tools}`) so agents can finally see the ledger/bg/task verbs;
`templatesDir` honoured and unresolved placeholders made a hard error; the two flaky logging tests fixed;
and the interlocking gates above. **Read `Core/ToolContract.cs` — it explains why each verb exists.**

### M1 — Deconstruction
Delete the old face and break the god classes. Nothing else is buildable on this foundation.

- **M1.1** Delete `src/Conductor/Ui/**` (2,021 lines) and every test that only exists to test it. The Face
  is the only UI. Delete `PreviewCommand`/`DashboardPreview`.
  *Truth gate:* `grep -r "Conductor.Ui" src/ tests/` returns nothing, and `conductor run` still drives a
  toy plan to completion.
- **M1.2** Split `Commands.cs` (2,574 lines, 54 types) into one file per command under `Commands/`.
  *Truth gate:* no file in `Commands/` over 250 lines; `architecture-baseline.json` no longer lists it.
- **M1.3** Split `Orchestrator.cs` (2,334 lines) into `RunLoop` (the state machine, thin), `SessionRunner`
  (spawn/stream/stall), and `VerdictEngine` (session outcome → decision), joining the existing
  `GateOrchestrator` / `LaneCoordinator` / `ControlDispatcher`.
  *Truth gate:* `Orchestrator.cs` gone or ≤ 400 lines; baseline no longer lists it; toy run still green.
- **M1.4** Split the remaining offenders: `PlanConfig.cs` (818/22 types), `McpTaskServer.cs`,
  `TelegramService.cs`, `RunDb.cs`, `ControlPlaneServer.cs`.
  *Truth gate:* `architecture-baseline.json` is `{}` — every entry paid off. This is M1's real definition
  of done and the fitness tests enforce it forever after.

### M2 — One truth: the database
- **M2.1** Schema defined **once**: versioned `.sql` migration files, embedded. Kill the duplicated
  `checkpoints`/`pids` DDL that exists in both the create and migrate paths.
  *Truth gate:* a test that builds a fresh DB and a migrated-from-v1 DB and asserts the schemas are
  byte-identical.
- **M2.2** `IRunStore` + `SqliteRunStore`. No SQL anywhere else (already enforced by a fitness test).
  Writes must not swallow failures silently: a write error surfaces as an event, loudly.
  *Truth gate:* every table has a writer and a test proving a row lands during a real toy run. Wire the
  dead `RecordAttempt` or delete it — no dead columns.
- **M2.3** `run.db` becomes authoritative. Delete `state.json` and `events.jsonl`. Resume reads from the
  database alone.
  *Truth gate:* `conductor run` a toy plan, `kill -9` mid-session, restart — the run resumes correctly with
  no `state.json` on disk. **This gate cannot be faked; it either resumes or it does not.**
- **M2.4** Session history on disk: `.conductor/sessions/<NNN>/` holding `prompt.md` (the exact compiled
  prompt), `transcript.md`, `verdict.md`, `handover.md`, `cost.json`, plus an `INDEX.md` linking them.
  *Truth gate:* after a 2-session toy run, both directories exist, are linked from the index, and
  `prompt.md` byte-matches what the agent actually received.
- **M2.5** Cost and tokens accurate per session and per plan, including gate/advisor overhead split.
  *Truth gate:* toy run's `costs` rows sum to the ticker's total; a `conductor report --query` answers
  "cost of stage X".

### M3 — Workflows that bend
Today the session shape is hardcoded (Deliver → Verify → Fix). Make it declarative.

- **M3.1** A workflow is a named list of steps in the plan: `{ id, role, model?, runIf?, skipIf?, deliver? }`.
  Ship built-ins as markdown + JSON: `deliver-verify` (default), `big-dev-then-big-audit` (the owner's
  preference: several delivery sessions, then one audit/QA/fix/handover sweep), `docs-only` (no dotnet
  gates), `spike` (no QA, no commit).
  *Truth gate:* a toy plan run under `deliver-verify` spawns 2 sessions; the same plan under a workflow
  with QA removed spawns 1. Asserted from `run.db`, not from a log line.
- **M3.2** Per-stage and per-session overrides, from the plan file **and** from the TUI: drop QA for this
  stage; use `fable` for deep analysis on that one; override any default.
  *Truth gate:* a stage with `"model": "fable"` shows that model in the spawned process's command line in
  the `pids` table.
- **M3.3** Safe parallelism: verify session N while delivering N+1 when they touch disjoint paths; keep the
  existing worktree lanes for independent stages. Collision avoidance by declared path claims, not hope.
  *Truth gate:* two lanes that touch the same file are serialised; two that do not run concurrently — both
  asserted from session timestamps in `run.db`.

### M4 — Gates that cannot be escaped
- **M4.1** Implement AD-4: agents CLAIM, the engine CONFIRMS. `TRACKER.md` becomes generated-only; a
  hand-edit is detected and discarded with a warning to the ledger.
  *Truth gate:* a rigged agent that marks every row DONE in the markdown and delivers nothing advances
  ZERO checkpoints.
- **M4.2** Truth-gate tier per stage: a product-level assertion the plan author writes. Gate results cache
  by `(gate, HEAD sha, tier)` — and prove the cache actually hits (today `gates` has zero rows under the
  real driver, so it never has).
  *Truth gate:* re-running an unchanged battery is refused by the engine, visible as `cached=1` in the
  `gates` table.
- **M4.3** The Verifier's findings become the retry prompt (already built — prove it fires).
  *Truth gate:* a rigged bad delivery scores < 80 and the retry prompt contains the findings verbatim; a
  rigged good delivery is not blocked (false-positive check).

### M5 — Observability and the Face
This is the "enjoy looking at it" stage. Budget real design time. The data all exists; almost none of it is
on screen.

- **M5.1 Timeline pane.** `Core/Events/Timeline.cs` exists and only the *old* TUI ever consumed it. A
  visual, scrollable timeline of the run: sessions as bars, gates, stalls, verdicts, cost accruing.
- **M5.2 Live plan pane.** Per-stage state / score / cost / attempts, current stage highlighted,
  dependencies visible, no truncation at any width.
- **M5.3 The native console.** Stream the agent's raw stdout (`rawLog` already captures it) over
  `GET /console/current` and give the Face a pane that shows *exactly what the CLI is printing* — with a
  toggle to the clean, folded view. This is the owner's "see what the agent is actually doing".
- **M5.4 Ticker.** Live cost/tokens *during* a session by folding `tokenDelta` events — today it reads
  zero until the session ends.
- **M5.5 Compiled-prompt preview.** `GET /prompt/preview?stage=&kind=` returns the exact prompt that
  would be sent. The Face shows it beside the template editor; edit the markdown, see the compiled result
  update. Prompts for live AND future sessions.
- **M5.6 One-verdict status.** `conductor status` answers "where are we, how did it go, what hurt" from
  the database in under a second, and the same view is a Face pane.
  *Truth gate for M5:* golden snapshot tests at 80×24 / 120×30 / 200×50; killing the Face mid-run leaves
  the run alive and `conductor face` reattaches to it.

### M6 — Plan authoring
- **M6.1** `conductor plan import <file|paste>` — a model of your choosing turns a mega plan into the task
  graph (stages, sessions, checkpoints, dependencies, gates), rendered as a table you confirm or edit.
- **M6.2** Re-import **diffs**, never clobbers. Mid-plan changes are first-class.
- **M6.3** Edit the plan from the TUI: stages, models, workflows, gates. No more "spin up an agent to
  tweak my workflow".
  *Truth gate:* import *this document* → a graph whose stage ids match M1…M9.

### M7 — Knowledge that compounds
- **M7.1** The ledger is mandated (M0 did the prompt half). Surface it: injected into the next session's
  prompt, visible in the Face, queryable.
- **M7.2** `conductor bug new|list|fix` + MCP: a found bug becomes a tracked row that survives the session
  that found it and feeds the audit phase. Agents stop re-finding the same bug.
- **M7.3** Structured handovers: rows in the database, rendered to markdown for the next agent, carrying
  what the last session *struggled* with — not just what it did.
  *Truth gate:* in a 2-session toy run, session 1 writes a note and files a bug; session 2's compiled
  `prompt.md` on disk contains both. Asserted against the file, so it cannot be faked.

### M8 — AFK and smart setup
- **M8.1** `conductor doctor` (< 2s): agent CLI present, model reachable, node + Face built, git clean,
  disk, DNS, budget. It tells you exactly what is missing and how to fix it.
- **M8.2** `conductor init`: scaffold a plan, templates, and packs; detect the repo type.
- **M8.3** Telegram v2 exists on paper — drive it end to end and fix what bleeds. Session-end one-liner
  with score; NeedsHuman with inline buttons; reply-to-inject; `/status` from the database.
  *Truth gate:* a toy run driven to completion from the phone, laptop lid closed.

### M9 — Dogfood close
- **M9.1** Run a real plan end-to-end under Maestro. Fix what bleeds.
- **M9.2** Final audit: every checkpoint in this document rated CONFORMS / DEVIATES with evidence.
  *Truth gate:* the audit is written by a Verifier session that re-runs each stage's truth gate itself.

---

## 4. The rules every session obeys

These are in the prompt already (`Core/ToolContract.cs`). They are repeated here because this document is
what a confused agent re-reads.

1. **`conductor note` as you learn, not at the end.** A session killed with an empty ledger has failed part
   of its job. This is not bureaucracy: it is the direct fix for the failure that cost this project eleven
   sessions.
2. **`conductor bg` for anything over ~3 minutes.** Blocking the foreground makes you look stalled, and you
   will be killed while doing good work.
3. **`conductor task --done <id> --evidence <path>` to claim.** You claim; the engine confirms. Editing the
   tracker markdown does nothing.
4. **Evidence or it did not happen.** A code path is not evidence. A passing test you wrote is weak
   evidence. A truth gate that Conductor ran itself is evidence.
5. **Never weaken the measurement.** Do not delete a test, suppress an analyzer, raise a ceiling, or soften
   a gate. All six of those are mechanically detected and fail the session. If the bar is genuinely wrong,
   write `HUMAN:` in the handoff and stop.
6. **Deleting code counts as delivery.** M1 is mostly subtraction. Do not be shy.

---

## 5. What the owner will be watching for

The system is working when:
- Starting a plan is one command, and you can see it running in a way you enjoy looking at.
- You can ask "how is it going" and get a real answer in a second, from the database.
- You can pause, read one verdict file, fix a thing, and resume.
- Two plans run in two terminals and never notice each other.
- An agent that tries to cheat gets caught by a machine, not by you.
- Knowledge from session 9 is in session 10's prompt.
- The next iteration of this tool can be run by this tool.
