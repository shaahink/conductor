# Conductor AI-native — TRACKER (G-series)

Two owner-requested features, driven by conductor under the Claude/Opus-Sonnet setup. Read order:
this file (handoff + rows) → `docs/CONDUCTOR-AI-NATIVE.md` (design brief) → the stage notes in
`plans/conductor-ai-native.plan.json`. Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. The delivering
agent adds sub-task rows via `task_add` as it works; the rows below are the confirmable checkpoints.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| G1.1 | Backend: freeform prompt → advisor(Fable) → PlanDiff → confirm/apply over the control plane; contract tests | DONE | 5bffd0c | ControlPlaneServerPromptTests (fake-advisor subprocess); live curl: prose→diff (apply:false, interpreter surfaced), apply:true persists + bumps planVersion, invalid rejected without writing |
| G1.2 | Face: Plan-tab Prompt box → renders the diff (reuse import-diff view) → a apply / esc cancel; goldens | DONE | 11d77db | plan.go Prompt section (TextArea, ctrl+s); TestPlanPromptSendsProseAndAppliesTheDiff; goldens plan_prompt / plan_prompt_diff |
| G2.1 | Backend: POST /tasks/update + /tasks/add emit TaskStatusChanged/TaskAdded (shared write with MCP); contract tests | DONE | 5bffd0c | TaskWrites shared with McpTaskServer; ControlPlaneServerTaskTests; live curl: add→GET shows card in TODO, update→done moves it |
| G2.2 | Face: Kanban tab (b) — TODO/In-Progress/Done columns, ↑↓ select, ←→ move, n add, live refresh; demo mirror + goldens | DONE | 11d77db | tab_kanban.go; kanban_test.go (move/reopen/add round-trips); goldens kanban / kanban_add (three populated columns) |
| G3.1 | Backend: `conductor run --paused` — dashboard + control plane up, engine idle at the gate; resume starts it (author-before-run, pre-seed kanban) | DONE | see git | `RunOptions.StartPaused` + `RunLoop.ApplyStartPause` (pure, never masks NeedsHuman/Aborted, dry-run ignores); RunLoopStartPauseTests (4); HarnessTests.FullCycle_StartPaused_IdlesUntilResume_ThenRunsSessionOne — live paused idle (0 sessions, task still running) → inbox ResumeRun → session 1 runs, exit 0 |
| G3.2 | Backend: live plan reload — `ControlAction.ReloadPlan` re-reads the plan at the next session boundary and swaps the live plan; Face `/plan/edit` + applied `/plan/import` auto-enqueue it; contract test | TODO | | |
| G3.3 | Face+backend: live "next-session" limits — edit `limits` + session cap from the Plan-tab Settings; loop honours them at the boundary (cap-down parks, cap-up continues); golden + contract test | TODO | | |

## Handoff
_**G1 + G2 DONE** and verified live (credential-free: a fake file-cat advisor subprocess plays the
model). **G1** — `POST /plan/import` routes freeform prose through the plan's advisor model
(`Advisor.AskTextAsync` fixed a latent bug where the import prompt's plan JSON could never satisfy the
verdict regex); the Face's Plan tab gained a **Prompt** section beside Import, both landing on the
shared import-diff view. **G2** — `POST /tasks/update|add` share one `TaskWrites` service with the MCP
task tools (can't drift); the Face's **Kanban** tab (`b`, the 11th tab) is a live board with ←→ move /
`n` add. **Hardening** (commit `4c96bd0`): the control plane now requires a **per-run write token**
(`X-Conductor-Token`, from `control-plane.json`) on every POST — a CSRF guard, since `/inject` and a
prompt-driven `/plan/import` are prompt-injection vectors; freeform apply must be previewed first.
Suites at G2 close: C# 750, Go all packages._

_**NEXT: G3 (live & dynamic) — the one gap left.** Today the running orchestrator holds a **fixed**
plan (`RunContext.Plan` is get-only, loaded once); Face edits only apply on a full **restart**, and
`conductor run` starts working immediately with no "author-first" idle. G3 closes that in one stage:
**G3.1** `run --paused` (dashboard up, engine idle → resume to start — the `Paused` idle path already
exists in `RunLoop.cs`); **G3.2** a real `ControlAction.ReloadPlan` that swaps the live plan at the
**next session boundary** (never mid-session) and is auto-enqueued by `/plan/edit` + applied
`/plan/import`; **G3.3** live `limits` + session cap from the Plan-tab Settings. Read the G3 section of
`docs/CONDUCTOR-AI-NATIVE.md` before starting — it names every seam to reuse. Independent of G1/G2;
this is the whole of the next session's work._
