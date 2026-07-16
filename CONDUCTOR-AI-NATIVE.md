# Conductor AI-native — TRACKER (G-series)

Two owner-requested features, driven by conductor under the Claude/Opus-Sonnet setup. Read order:
this file (handoff + rows) → `docs/CONDUCTOR-AI-NATIVE.md` (design brief) → the stage notes in
`plans/conductor-ai-native.plan.json`. Status ∈ TODO / IN PROGRESS / DONE / BLOCKED. The delivering
agent adds sub-task rows via `task_add` as it works; the rows below are the confirmable checkpoints.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| G1.1 | Backend: freeform prompt → advisor(Fable) → PlanDiff → confirm/apply over the control plane; contract tests | TODO | | |
| G1.2 | Face: Plan-tab Prompt box → renders the diff (reuse import-diff view) → a apply / esc cancel; goldens | TODO | | |
| G2.1 | Backend: POST /tasks/update + /tasks/add emit TaskStatusChanged/TaskAdded (shared write with MCP); contract tests | TODO | | |
| G2.2 | Face: Kanban tab (b) — TODO/In-Progress/Done columns, ↑↓ select, ←→ move, n add, live refresh; demo mirror + goldens | TODO | | |

## Handoff
_Seed — no session has run yet. G1 (prompt→plan, Opus) and G2 (kanban, Sonnet) are independent; either
can go first. Both reuse existing surfaces (plan import for G1, the task graph + MCP task writes for
G2) — see the design brief before writing anything new._
