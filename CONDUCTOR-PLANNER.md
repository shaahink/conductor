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
| P1 | `IAssignmentPolicy`: role→agent map (deliver/verify/audit/fix → model+persona) + multi-item session claim (conflict-free via PathClaims); engine asks the policy instead of hard-picking the first checkpoint. | DONE | see git | Library: ReadyItem/SessionAssignment/IAssignmentPolicy/DefaultAssignmentPolicy (pure; role keys case-insensitive; Resume never role-overridden; multi-item deliver-only, first item unconditional, extras refused on declared path overlap incl. external lane claims, PathClaimTracker-compatible normalization); engine: SessionRunner asks the policy, applies model/command onto the merged agent + persona through PromptBuilder (personaOverride param) + SessionStarted, multi-item prompt section lists every claimed item; 10 policy unit tests + live harness proof FullCycle_RoleModelAndMultiItemClaim_ReachTheRealSession ({model} arg echoed by the real process = role-override-model; prompt.md names H0.1+H0.2); suite 778 green |
| P2 | `IQaPolicy` dial (off / every-session / phase-gate + threshold) resolving onto the existing workflows/overrides; Face Settings edit + live via G3 reload; demo mirror + golden + contract test. | DONE | see git | Library: QaProjection/IQaPolicy/DefaultQaPolicy (pure; off→deliver-verify+skip-verification, everySession→deliver-verify, phaseGate→big-dev-then-big-audit; stage rule replaces plan rule whole; unknown mode → classic AND CollectErrors rejects it); PIN test compares resolved definitions (Assert.Same vs hand-picked workflows). Engine: RunContext.Qa seam (DI); `Resolve(plan, stage, qa)` — no dial-blind overload; EffectiveSkipVerification/EffectiveVerifierThreshold at every former override/threshold site incl. PromptBuilder's {verifierThreshold}; StageConfig.Qa; auditCoversPriorSessions=false narrows the audit diff base to the triggering session (audit prompt now honors PendingAudit.StageStartHead). Fixed 2 real bugs the live proof exposed: session-start kind resolution double-advanced the workflow index onto a verify with no PendingVerify (NRE — latent since M3.1, deterministic on a live dial flip; recorded index now consumed without advancing + missing pending context synthesized for verify/audit, fix→deliver), and ApplyPlanReload now recomputes session-scoped stage flags from the fresh plan (else a dial edit silently waited for the next stage). /plan/edit: `qa` target (mode/verifierthreshold/auditcoverspriorsessions), stage `qamode`/`qathreshold`, limits `verifierthreshold`; empty mode clears whole; round-trip contract test. Face: Settings qa dial + verifierThreshold rows, per-stage qa row ("(workflow decides)" clears), demo mirror, goldens, 2 round-trip key tests. Live proof `P2QaDialLiveTests`: qa=off runs deliver-only (committing session, verify skipped-as-passed), plan-file flip to everySession + reload-plan makes the SAME run verify, no restart. Suite 793 green; ratchet passed properly (SessionRunner.Kinds.cs split, PlanQaDto own file). |
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

_**P1 DONE (same session).** `IAssignmentPolicy`/`DefaultAssignmentPolicy` live in the library —
pure, unit-tested (roles, case-insensitivity, Resume exemption, multi-item bounds, declared-path
conflict refusal with the engine's PathClaimTracker normalization). The engine's session-start asks
the policy: role model/command land on the merged `AgentConfig` (proven live — the fake agent echoed
the {model} arg), role persona threads through `PromptBuilder` (new optional `personaOverride`) and
the `SessionStarted` event, and a multi-item claim appends a "Claimed items this session" prompt
section naming each item (proven on the real prompt.md). No `pipeline` block = classic behavior._

_**P2 DONE (same session).** The QA dial lives in the library as a pure projection —
`DefaultQaPolicy.Project(planRule, stageRule)` → `QaProjection` (workflow name + skip-verification +
threshold + audit scope), pinned by a test asserting the projected workflow IS the hand-picked
definition. The engine consults it at one choke point (`Resolve(plan, stage, qa)` — the dial-blind
overload was deleted so no resolution can bypass the dial) plus `EffectiveSkipVerification`/
`EffectiveVerifierThreshold` extensions at the former override/threshold sites. Editable plan-wide
(`/plan/edit` target `qa`) and per-stage (`stage.qamode`/`qathreshold`), plus `limits.verifierthreshold`;
the Face Settings + stage editors gained the rows (goldens + round-trip tests; label column widened to
fit verifierThreshold). Live-proven end-to-end: `P2QaDialLiveTests` drives a real run — off = deliver-only,
flip-to-everySession + reload verifies mid-run. Two engine bugs found by that proof and fixed: the
session-start double-advance NRE (see the SessionRunner.Kinds.cs comment) and stale session-scoped
stage flags across a live reload (ApplyPlanReload now re-applies them)._

_**NEXT: P3 (Kanban card prompt building-blocks)** — pure `PromptComposition` decomposing
PromptBuilder's output into labeled blocks at `GET /tasks/{id}/prompt`, Face card-detail panel with
an editable task-scoped context block, advisor-refine + hand-to-Claude via existing `/inject` (reuse
G1 advisor plumbing). Then P4 (finish extraction + standalone consumer), P5 (rollover surfaced).
Read `docs/CONDUCTOR-PLANNER.md` §P3 before starting._
