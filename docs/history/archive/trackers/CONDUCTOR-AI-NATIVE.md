# Conductor AI-native — TRACKER (G-series)

Two owner-requested features, driven by conductor under the Claude/Opus-Sonnet setup. Read order:
this file (handoff + rows) → `docs/history/CONDUCTOR-AI-NATIVE.md` (design brief) → the stage notes in
`plans/conductor-ai-native.plan.json`. Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. The delivering
agent adds sub-task rows via `task_add` as it works; the rows below are the confirmable checkpoints.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| G1.1 | Backend: freeform prompt → advisor(Fable) → PlanDiff → confirm/apply over the control plane; contract tests | DONE | 5bffd0c | ControlPlaneServerPromptTests (fake-advisor subprocess); live curl: prose→diff (apply:false, interpreter surfaced), apply:true persists + bumps planVersion, invalid rejected without writing |
| G1.2 | Face: Plan-tab Prompt box → renders the diff (reuse import-diff view) → a apply / esc cancel; goldens | DONE | 11d77db | plan.go Prompt section (TextArea, ctrl+s); TestPlanPromptSendsProseAndAppliesTheDiff; goldens plan_prompt / plan_prompt_diff |
| G2.1 | Backend: POST /tasks/update + /tasks/add emit TaskStatusChanged/TaskAdded (shared write with MCP); contract tests | DONE | 5bffd0c | TaskWrites shared with McpTaskServer; ControlPlaneServerTaskTests; live curl: add→GET shows card in TODO, update→done moves it |
| G2.2 | Face: Kanban tab (b) — TODO/In-Progress/Done columns, ↑↓ select, ←→ move, n add, live refresh; demo mirror + goldens | DONE | 11d77db | tab_kanban.go; kanban_test.go (move/reopen/add round-trips); goldens kanban / kanban_add (three populated columns) |
| G3.1 | Backend: `conductor run --paused` — dashboard + control plane up, engine idle at the gate; resume starts it (author-before-run, pre-seed kanban) | DONE | see git | `RunOptions.StartPaused` + `RunLoop.ApplyStartPause` (pure, never masks NeedsHuman/Aborted, dry-run ignores); RunLoopStartPauseTests (4); HarnessTests.FullCycle_StartPaused_IdlesUntilResume_ThenRunsSessionOne — live paused idle (0 sessions, task still running) → inbox ResumeRun → session 1 runs, exit 0 |
| G3.2 | Backend: live plan reload — `ControlAction.ReloadPlan` re-reads the plan at the next session boundary and swaps the live plan; Face `/plan/edit` + applied `/plan/import` auto-enqueue it; contract test | DONE | see git | 12th verb `reload-plan` (ControlFile.Parse → all 3 ingresses); dispatcher always defers (`ConsumeReloadPending`), swap ONLY at loop top = session boundary (`ApplyPlanReload`: SwapPlan on ctx+prompts/gates/lanes/dispatcher, `PlanReloaded` event, invalid file = loud no-op); `/plan/edit`+applied import enqueue it; CLI `plan reload` queues it; Face palette entry + golden; G3LiveReloadTests (live: paused run, file edit, reload+resume → StageEntered carries new title), 3 contract tests (edit enqueues / reject doesn't / apply-import does, preview doesn't); 764 C# green |
| G3.3 | Face+backend: live "next-session" limits — edit `limits` + session cap from the Plan-tab Settings; loop honours them at the boundary (cap-down parks, cap-up continues); golden + contract test | DONE | see git | New `limits.maxSessions` (run-total cap; parks, never stops) + `limits` target on /plan/edit (5 fields, empty clears a cap, invalid rejected atomically); park sets Paused+ParkedBySessionCap+reason, reload that raises/clears cap auto-resumes exactly that park; Face Settings gains 5 limits rows (target:"limits"), plan_settings golden, TestPlanSettingsLimitsFieldRoundTrips (set 5→clear); live proof G3LiveReloadTests.LiveSessionCap_ParksAtBoundary_AndRaisingItResumes; contract PostPlanEdit_LimitsFields_PersistClearAndValidate; C# 766 + Go green |

## Handoff
_**ALL G-SERIES STAGES DONE (G1+G2+G3), 2026-07-16 — this tracker is CLOSED.** G3 (live & dynamic)
landed in three checkpoints, each verified live with zero model spend:_

- _**G3.1** `conductor run --paused`: engine + control plane + Face come up parked; `resume` (any
  ingress) starts session 1. `RunLoop.ApplyStartPause` is pure + unit-tested; the harness proves the
  full cycle (paused idles with 0 sessions → inbox ResumeRun → exactly one session runs)._
- _**G3.2** live plan reload: 12th control verb `reload-plan` (via `ControlFile.Parse`, so all three
  ingresses accept it). Always deferred — the run loop consumes it ONLY at the top of its iteration
  (the session boundary; paused/idle iterations included, so an edit made while parked is live before
  resume). `ApplyPlanReload` re-reads the plan file, swaps `RunContext.Plan` + PromptBuilder and the
  plan-caching satellites (`GateOrchestrator`/`LaneCoordinator`/`ControlDispatcher` `SwapPlan`), emits
  `PlanReloaded` (timeline/SSE). `/plan/edit` + applied `/plan/import` auto-enqueue it; `conductor
  plan reload` queues it too. Invalid/missing file = loud no-op, old plan stays._
- _**G3.3** live limits: new `limits.maxSessions` (run-total session cap) + a `limits` target on
  `/plan/edit` (maxSessions/maxRunCostUsd/maxRunTokens/stallMinutes/sessionTimeoutMinutes; empty
  clears a nullable cap). Cap reached → run PARKS at the boundary (`Paused` + `ParkedBySessionCap` +
  clear reason) — never a stop/crash; a reload that raises/clears the cap auto-resumes exactly that
  park (an operator pause is never auto-resumed). Face Plan-tab Settings gained the five rows._

_Suites at close: **C# 766**, Go all packages, ratchet green (RunLoop split into `RunLoop.Control.cs`,
`PlanReloaded` in its own file). Key tests: `G3LiveReloadTests` (live reload + live cap park/resume),
`HarnessTests.FullCycle_StartPaused_*`, `ControlPlaneServerPlanTests` (limits + reload-enqueue
contracts), `TestPlanSettingsLimitsFieldRoundTrips` + `plan_settings` golden (Go)._

_**NEXT: the P-series** — `plans/conductor-planner.plan.json`, tracker `CONDUCTOR-PLANNER.md`, brief
`docs/history/CONDUCTOR-PLANNER.md`. G3 was its prerequisite (live reload makes dynamic planning real);
start at P0 (the `Conductor.Planning` library keystone + delete the dead `agent.tokenCeiling`)._
