# Conductor AI-native — design brief (G-series)

**Written:** 2026-07-16. **Scope:** two owner-requested features, planned as conductor-deliverable
stages so the engine drives them under the Claude/Opus-Sonnet setup. **Not** implementation — this is
the brief the tracker (`CONDUCTOR-AI-NATIVE.md`) and plan (`plans/conductor-ai-native.plan.json`)
execute. Read this → the tracker → the stage notes.

Both features are *extensions of surfaces that already exist* — the point is reuse, not a rebuild.

---

## G1 — AI-native plan editing: prompt → diff → confirm → apply

**Goal.** Change the plan/pipeline by typing an instruction in plain English ("split S1 into two
stages", "add a lint gate", "make S4 depend on S2"), see the exact diff, confirm, apply. Today you can
only edit fields, add/delete a stage-or-gate by hand, or import a *structured* doc.

**What already exists (reuse it):**
- `PlanImportService.ImportAsync(plan, text, advisorModel, …)` — routes **freeform prose through the
  advisor model** and returns a parsed plan. `ParseStructured` handles the structured case. The CLI
  `conductor plan import "<free text>" --model X` already does prose→advisor→diff→confirm→apply.
- `PlanDiff.Compute(plan, incoming)` → `PlanDiff.Apply(writablePlan)` — the diff + atomic
  validate-then-save path (`ControlPlaneServer.Plan.cs` `HandlePlanImportAsync`).
- The Face **Import** section (`plan.go` `renderPlanImport` / `renderImportDiff`) already renders a
  `PlanDiffDto` and applies on `a`.
- `POST /plan/import` exists but **rejects freeform prose** ("needs the CLI advisor path") — that
  rejection is the only thing standing between today and this feature.

**Net-new (small):**
1. **Backend** — teach `POST /plan/import` (or a sibling `POST /plan/prompt`) to accept a freeform
   prompt: when `ParseStructured` returns null, call `PlanImportService.ImportAsync` with the plan's
   advisor model (Fable) instead of 400-ing. Same diff/confirm/apply contract as structured import
   (preview when `apply:false`, atomic apply when `apply:true`). The advisor model already lives in
   the plan (`advisor` block) — surface *which* model interpreted the prompt in the result.
2. **Face** — a **Prompt** entry in the Plan tab (next to Import): a multi-line prompt box → on submit,
   POST the prompt → render the returned `PlanDiffDto` with the existing `renderImportDiff` → `a` apply
   / `esc` cancel. This is the import view with a prompt source instead of a path.

**Checkpoints:** G1.1 backend (prose→advisor→diff→confirm→apply over the control plane, contract
tests). G1.2 Face prompt box reusing the import-diff view (goldens). Model routing/edge cases
(no advisor configured → clear message; long prompt; apply revalidates) fold into whichever session
has room; the agent adds sub-tasks via `task_add`.

**Gate:** a curl posting `{"source":"add a lint gate that runs dotnet format","apply":false}` returns a
non-empty diff with the added gate; `apply:true` persists it and bumps `planVersion`; an invalid result
is rejected without writing (same guarantee the structured path already has).

---

## G2 — Kanban board: live task graph, with actions

**Goal.** A live board of the run's task graph — columns **TODO · In Progress · Done** — where a card
is a checkpoint sub-task, and you can move a card (change status) or add one, right from the Face.
"Consistent with the internal planner" = it is a view of the same task graph the engine and the MCP
task tools already drive.

**What already exists (reuse it):**
- `GET /tasks` → `TasksDto` of `TaskDto{TaskId, CheckpointId, Title, Status, Source, Order}`, folded
  from `TaskAdded`/`TaskStatusChanged` events by `TaskGraph`. Status ∈ todo/in_progress/done/skipped.
- The **MCP** task tools (`task_update`, `task_add`) already emit those events
  (`McpTaskServer.Handlers`) — the write semantics exist; they're just not on the HTTP control plane.
- The Face tab framework: `tabKey`/`tabNames`/`tabCount` (`model.go`), `handleTabKey` routing, the
  `tabHandlesAllKeys` pattern for pane-owned input, and the golden harness.

**Net-new:**
1. **Backend** — `POST /tasks/update {taskId, status}` and `POST /tasks/add {checkpointId, title,
   order}` on the control plane, emitting `TaskStatusChanged`/`TaskAdded` exactly like the MCP handlers
   (factor the shared write so MCP and HTTP can't drift). Contract tests.
2. **Face** — a **Kanban tab** (11th tab, mnemonic `b` for "board"; reachable by `b` and tab-cycle —
   note there's no spare digit past `0`, so the tab strip/help must not imply one). Three columns from
   `GET /tasks` grouped by status; `↑↓` select a card, `←→` move it across columns (`POST /tasks/update`
   with the mapped status), `n` add a card under the selected checkpoint (`POST /tasks/add`); re-fetch
   after a write so the board reflects the change. Demo-source mirror + goldens at the standard sizes.

**Checkpoints:** G2.1 backend task update/add endpoints (shared write, contract tests). G2.2 Face
Kanban tab: render + move + add + live refresh (demo mirror, goldens).

**Gate:** `POST /tasks/add` then `GET /tasks` shows the new card in TODO; `POST /tasks/update` to
`done` moves it; a Face golden shows three populated columns and the move/add affordances.

---

## G3 — Live & dynamic: author before you start, and re-plan without a restart

**Why.** G1/G2 gave prompt→plan and a live board, but three gaps keep the loop from feeling live
(owner review, 2026-07-16):
1. **No "up but idle" start.** `conductor run` begins spawning sessions the instant it launches, so you
   can't bring the dashboard up, look at the pipeline, and author *before* it does anything.
2. **Edits aren't dynamic.** The running orchestrator holds a **fixed** plan instance
   (`RunContext.Plan` is get-only, set once from `PlanConfig.Load` in `RunCommand`). A Face `/plan/edit`
   or `/plan/import` rewrites the plan **file**, but the live run never re-reads it — `conductor plan
   reload` today only *validates and prints* (`PlanReloadCommand`), and there is **no `ControlAction`
   for reload**. So every "live" edit really only takes effect on a full process restart. The doc
   comments that say "picks up at the next session boundary" are aspirational, not wired.
3. **Next-session limits are static.** You can't tighten or extend a run in flight — `limits`
   (maxRunCost/Tokens, stall/timeout) and the session budget are read from the fixed plan; there's no
   way to cap a run that's overspending or extend one that's going well without editing JSON + restart.

**Scope: one focused delivery stage, three checkpoints. Reuse the existing seams — do not fork a
second plan store or a second control path.**

- **G3.1 — `conductor run --paused` (author-before-run).** Add a `--paused` flag to `RunCommand`
  (extend `RunOptions` — the record in `Orchestrator.cs`) that sets `state.Status = RunStatus.Paused`
  **before** the run loop starts. The loop **already idles on `Paused`** (`RunLoop.cs` ~L87:
  `PushIdleSnapshot` + delay), so this is a small, safe change: the engine + control plane + Face come
  up, the engine waits at the gate, and `:`→**resume** (the existing `ResumeRun` verb) starts session 1.
  Because the control plane is up while paused, this also unlocks **pre-seeding the Kanban board**
  (`POST /tasks/add` works) and authoring the pipeline before any spend.
  **Gate:** `run --paused` comes up, `conductor status` shows `Paused`, **zero** sessions spawned;
  a resume (CLI or Face palette) starts session 1. Unit-test the flag→status wiring.

- **G3.2 — live plan reload (the dynamicity core).** Add `ControlAction.ReloadPlan`
  (`Progress.Control.cs`) mapped in `ControlFile.Parse` (which single-sources the `POST /control`
  whitelist — so all three ingresses get it free, same as `heartbeat` did). Dispatch it in
  `ControlDispatcher` to flag the context; at the **next session boundary** the run loop re-reads the
  plan file (`PlanConfig.Load` / the control plane's existing `LoadPlanFresh`) and swaps the live plan
  the loop reads — so stages/gates/limits changed via the Face take effect **in the current run**, no
  restart. Make `RunContext.Plan` reassignable at that one safe point only (never mid-session — an
  agent is running against the old stage graph). Then have the control plane's `/plan/edit` and an
  applied `/plan/import` **enqueue a ReloadPlan** after a successful save, so a Face edit is dynamic by
  default. Update `PlanReloadCommand`'s "next run" wording once the reload is real, and add a
  `conductor plan reload` path that enqueues the verb against a live run.
  **Gate:** with a paused/idle run up, edit a stage's `sessions` (or add a gate) via the Face, resume,
  and the **next** session honours the new value — proven by a contract test that edits then reads the
  live plan back, plus a golden showing the reloaded plan. A mid-session reload request is deferred to
  the boundary, never applied under a running agent.

- **G3.3 — live "next-session" limits from Settings.** Extend the Plan-tab **Settings** section and
  `ApplyPlanEdit` (`ControlPlaneServer.Plan.cs`) to edit `limits` — `maxRunCostUsd`, `maxRunTokens`,
  `stallMinutes`, `sessionTimeoutMinutes` — and a live **session cap** (the `--max-sessions`
  equivalent) for the current run. Riding on G3.2's reload, the loop consults the updated limits at the
  next boundary: lower the cap below the sessions already run and the run **parks** (`Paused`/idle,
  never a hard crash); raise it and it continues. Face parity: fields + a golden + a key-driving test.
  **Gate:** set `maxSessions` below the current count on a live run → it parks at the boundary with a
  clear reason; raise it → it resumes. A contract test drives limit-down-parks / limit-up-continues.

**Cross-cutting for G3:** the swap point is the **session boundary and only there** — reassigning the
live plan while an agent runs against it is the one thing that must never happen (validate + defer).
Everything else reuses what exists: the `Paused` idle path, `LoadPlanFresh`, the `ControlFile.Parse`
whitelist, `ApplyPlanEdit`, and the Plan-tab Settings surface. "One more session" — the delivering
agent adds sub-tasks via `task_add` and can split G3.2/G3.3 across a resume if the budget runs short.

---

## Cross-cutting notes for the delivering agent
- **Reuse, don't fork.** G1 is the import path with a prompt source; G2 is the task graph with two
  write endpoints + a view. If you find yourself writing a second diff engine or a second task store,
  stop.
- **Control plane stays decoupled** (AGENTS.md §"Command/Query/Event layering"): new writes emit events
  / go through the existing services; GET views read the event-sourced projections. Don't reach into
  Orchestrator internals.
- **Face parity:** every new interactive surface needs a golden and a unit test driving the keys
  (the repo's bar — see the add/delete and process-kill work, 2026-07-15/16).
- **Ratchet:** keep new endpoint handlers in their own partial if a file nears 500 lines (see
  `ControlPlaneServer.Processes.cs`).
