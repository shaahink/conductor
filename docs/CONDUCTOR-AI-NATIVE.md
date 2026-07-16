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
