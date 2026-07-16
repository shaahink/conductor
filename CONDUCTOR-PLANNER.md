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
| P0 | New `Conductor.Planning` library (one-way dep, arch-test enforced) + agnostic `pipeline` rules block on PlanConfig + `IWorkflowResolver` seam; move the pure WorkflowEngine across; delete the dead `agent.tokenCeiling`. Behavior unchanged (defaults reproduce today). | DONE | see git | `src/Conductor.Planning/` (no engine ref): SessionKind + Workflow{Definition,Step,Overrides,RuntimeVars} + WorkflowEngine (agnostic Resolve) + IWorkflowResolver + PipelineRules/RoleAgentRule/QaRule/MultiItemRule (one type/file); engine adapters WorkflowVarsFactory + Resolve(plan,stage) extension; RunContext.Workflows is the interface, DI-wired in ConductorHost; `PlanConfig.Pipeline` (null=classic); TokenCeiling deleted (property+Merge+ai-native plan JSON, grep-clean); arch test PlanningLibraryDoesNotReferenceTheEngine (assembly refs + source usings); suite 767 green, behavior unchanged |
| P1 | `IAssignmentPolicy`: role→agent map (deliver/verify/audit/fix → model+persona) + multi-item session claim (conflict-free via PathClaims); engine asks the policy instead of hard-picking the first checkpoint. | TODO | | |
| P2 | `IQaPolicy` dial (off / every-session / phase-gate + threshold) resolving onto the existing workflows/overrides; Face Settings edit + live via G3 reload; demo mirror + golden + contract test. | TODO | | |
| P3 | Kanban card detail: pure `PromptComposition` (labeled building-blocks) at `GET /tasks/{id}/prompt`; Face panel with editable task-scoped context; advisor-refine + hand-to-Claude (reuse G1 advisor + /inject); goldens + composition unit test. | TODO | | |
| P4 | Finish decoupling: move remaining workflow/assignment/QA decision logic behind the seam; a standalone consumer referencing ONLY `Conductor.Planning` resolves decisions from a plan file. No planning type left in the engine assembly. | TODO | | |
| P5 | Session-token rollover + limits surfaced honestly in the Face (labeled OFF by default), editable with a session-scoped this-run override; extend ApplyPlanEdit; ride G3 reload. | TODO | | |

## Handoff
_**G3 landed (prerequisite met) and P0 is DONE, 2026-07-16.** The decoupled home exists:
`src/Conductor.Planning/` owns SessionKind, the Workflow* vocabulary, the (now-agnostic) WorkflowEngine,
the `IWorkflowResolver` seam, and the `PipelineRules` schema (`pipeline` block on PlanConfig — null =
classic behavior). The engine reaches it through DI + two thin adapters (`WorkflowVarsFactory`,
`Resolve(plan, stage)` extension); a global `Using Include="Conductor.Planning"` in the two csproj
files keeps the moved vocabulary available without churn. The one-way dependency is enforced by
`ArchitectureTests.PlanningLibraryDoesNotReferenceTheEngine` (compiled assembly refs AND source-level
using scan). `agent.tokenCeiling` is deleted and grep-clean. Suite 767 green — behavior unchanged._

_**NEXT: P1 (role→agent assignment + multi-item sessions).** Build `IAssignmentPolicy` into
`Conductor.Planning` (pure: rules + ready-task facts + kind in → `SessionAssignment` out); populate
`PipelineRules.Roles`/`MultiItem` semantics; the engine's session-start path
(`SessionRunner.ResolveSessionKind` + the active-checkpoint pick) asks the policy instead of
hard-picking. Path-conflict validation stays in the policy (unit-test it); `StageConfig.PathClaims` +
`PathClaimTracker` already exist. P2 (QA dial) can follow or interleave; P5 is independent._
