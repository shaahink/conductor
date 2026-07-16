# Conductor Dynamic Planner — TRACKER (P-series)

A decoupled, rules-driven planner: author a pipeline from a doc or by hand, see it as a live Kanban +
pipeline, and tweak the *rules* dynamically (who works what, who audits, when QA runs, per-task prompt
building-blocks). Read order: this file (handoff + rows) → `docs/CONDUCTOR-PLANNER.md` (design brief) →
the stage notes in `plans/conductor-planner.plan.json`. Status ∈ TODO / IN PROGRESS / DONE / BLOCKED.
The delivering agent adds sub-task rows via `task_add`; the rows below are the confirmable checkpoints.

**Prerequisite: G3 (live plan reload) from the G-series must land first** — the dynamic-edit story rests
on `ControlAction.ReloadPlan` (`docs/CONDUCTOR-AI-NATIVE.md` §G3). Without it every edit here degrades to
takes-effect-on-restart.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| P0 | New `Conductor.Planning` library (one-way dep, arch-test enforced) + agnostic `pipeline` rules block on PlanConfig + `IWorkflowResolver` seam; move the pure WorkflowEngine across; delete the dead `agent.tokenCeiling`. Behavior unchanged (defaults reproduce today). | TODO | | |
| P1 | `IAssignmentPolicy`: role→agent map (deliver/verify/audit/fix → model+persona) + multi-item session claim (conflict-free via PathClaims); engine asks the policy instead of hard-picking the first checkpoint. | TODO | | |
| P2 | `IQaPolicy` dial (off / every-session / phase-gate + threshold) resolving onto the existing workflows/overrides; Face Settings edit + live via G3 reload; demo mirror + golden + contract test. | TODO | | |
| P3 | Kanban card detail: pure `PromptComposition` (labeled building-blocks) at `GET /tasks/{id}/prompt`; Face panel with editable task-scoped context; advisor-refine + hand-to-Claude (reuse G1 advisor + /inject); goldens + composition unit test. | TODO | | |
| P4 | Finish decoupling: move remaining workflow/assignment/QA decision logic behind the seam; a standalone consumer referencing ONLY `Conductor.Planning` resolves decisions from a plan file. No planning type left in the engine assembly. | TODO | | |
| P5 | Session-token rollover + limits surfaced honestly in the Face (labeled OFF by default), editable with a session-scoped this-run override; extend ApplyPlanEdit; ride G3 reload. | TODO | | |

## Handoff
_Seed — no session has run yet. **Land G3 (G-series) first.** Then P0 is the keystone: it creates the
decoupled `Conductor.Planning` home and the seam so P1–P3 build into the right place, and it clears the
dead-`tokenCeiling` trap the audit found. P4 finishes the extraction and proves the library stands alone;
P5 is independent. The whole point is code quality: a **pure**, **one-way-dependent**, **standalone-usable**
planning library with **declarative, agnostic rules** — read the "Design principles" section of
`docs/CONDUCTOR-PLANNER.md` before writing anything, and keep the architecture test (one-way dependency)
green from P0 on. Reuse, don't fork: `WorkflowEngine`, the workflow/override model, the task graph, the
Kanban tab, and G1's advisor plumbing already exist — surface and decouple them, don't rebuild them._
