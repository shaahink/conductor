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
| P3 | Kanban card detail: pure `PromptComposition` (labeled building-blocks) at `GET /tasks/{id}/prompt`; Face panel with editable task-scoped context; advisor-refine + hand-to-Claude (reuse G1 advisor + /inject); goldens + composition unit test. | DONE | see git | Library: PromptBlockKind/PromptBlock/PromptCompositionInputs/PromptComposition/PromptComposer (pure; fixed order persona→stageNotes→taskTitle→taskContext→knowledge→tools; only task-scoped blocks editable; empty read-only blocks omitted, editable ones always present); PIN test: a context edit changes EXACTLY that block. Engine: TaskItem.Context + TaskDetailEdited event (null=unchanged, blank-title refused at write AND fold) + TaskWrites.BuildDetailEdit; `GET /prompt/blocks?task=` via TaskPromptComposition adapter (stage from Conventions.DeriveStageId; knowledge = batteries + queued instructions); `POST /tasks/edit`; `POST /tasks/refine` = advisor PROPOSES {title,context} JSON (parse tolerates prose/fences; task fields framed untrusted; 400 without advisor; mutates nothing); owner context reaches the REAL session prompt (SessionRunner.BuildTaskContextSection — open cards of the claimed checkpoints only, absent section = byte-identical prompts) and MCP task_list. Face: enter on a card → block panel (✎ = editable), `t` title / `c` context (TextArea, ctrl+s) → /tasks/edit, `a` refine preview→confirm (enter applies via the same /tasks/edit, esc discards), `h` hand-off → /inject after y/n; demo mirror; goldens kanban_detail{,_ctx_edit,_proposal}; 7 board-detail unit tests over the demo source. Contract tests behind the token: edit round-trip, blocks recomposition (only taskContext changes on the wire), 401, refine-without-advisor. Suite 816 C# + Go green. |
| P4 | Finish decoupling: move remaining workflow/assignment/QA decision logic behind the seam; a standalone consumer referencing ONLY `Conductor.Planning` resolves decisions from a plan file. No planning type left in the engine assembly. | DONE | see git | Library: `IWorkflowResolver.Advance` (the full post-session walk — conditionals, repeat wrap, AND the skip-verification collapse the engine did by recursion; returns `WorkflowAdvance` with per-hop `WorkflowHop` records + `ExhaustedFromIndex`) and `ResolveStartKind` (recorded index consumed WITHOUT advancing; first resolution from -1; verify→deliver under skip; exhausted→deliver). Engine: `VerdictEngine.AdvanceWorkflowStep` and `SessionRunner.ResolveSessionKind` are now effect-only — facts in (vars via WorkflowVarsFactory, effective skip via IQaPolicy), decisions out (log hops, ConfirmPendingCheckpoints per skipped verify, populate Pending*). Standalone proof: `tools/plan-lint` (in the slnx) references ONLY `Conductor.Planning`, deserializes a plan file with the library's own rule types (PipelineRules/QaRule/WorkflowOverrides/WorkflowDefinition), and prints per-stage workflow+steps, QA projection, start kind, and role assignments — verified on `plans/conductor-planner.plan.json` (classic) and a rules fixture (roles/qa=phaseGate/multiItem + stage dial off + overrides skip). Arch tests: `PlanLintReferencesOnlyThePlanningLibrary` (single ProjectReference + no engine usings + no PackageReference) and `NoPlanningDomainTypeRemainsInTheEngineAssembly` (no Conductor.Planning-namespace type in conductor.dll). 7 Advance/StartKind unit tests. Bonus: retired a bare MA0045 pragma (Telegram WriteControlFile → async) — ratchet.ps1 back to a passing 38/38 (it had been silently 39 since G3.3). Suite 825 green; behavior unchanged (zero existing tests touched). |
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

_**P3 DONE (2026-07-16).** The card detail is live end-to-end: `Conductor.Planning` gained the pure
`PromptComposer` (facts in → ordered labeled `PromptBlock` list out; only the task-scoped title +
extra-context blocks are editable — pinned by a test proving a context edit changes exactly that
block). Task data grew a `Context` field via the new `TaskDetailEdited` event (TaskWrites validates;
the fold refuses blank titles so a replay can't blank a card). The wire: `GET /prompt/blocks?task=`,
`POST /tasks/edit` (the single confirm step — manual edits AND advisor proposals land through it),
`POST /tasks/refine` (proposal only, 400 without an advisor). Owner context genuinely reaches the
delivering session (`BuildTaskContextSection` — open cards of claimed checkpoints; no context = the
prompt is byte-identical to before P3) and the agent's `task_list`. The Face opens a card with
enter: labeled blocks, `t`/`c` structured editors, `a` advisor preview→confirm, `h` hand-off via
/inject after y/n. Demo-mirrored, golden'd (kanban_detail ×3), contract-tested behind the token._

_**P4 DONE (same session).** The decoupling is finished and PROVEN standalone. The last two
decisions living inline in the engine moved behind the seam: `IWorkflowResolver.Advance` now owns
the whole post-session walk (including the skip-verification collapse that was a recursion in
`VerdictEngine.Workflow.cs` — each skipped verify re-evaluates with verifier.passed=true; the
engine gets back a hop list and only EFFECTS it: logs, confirms checkpoints, populates Pending*),
and `ResolveStartKind` owns the session-start kind (consume-recorded-index-without-advancing, the
P2 bug's invariant, now pinned in the library). `tools/plan-lint` is the standalone consumer: one
ProjectReference (Conductor.Planning), zero packages, parses a plan file with the library's own
rule types and prints workflow/QA/assignment/start-kind per stage — run it with
`dotnet run --project tools/plan-lint -- plans/conductor-planner.plan.json [stage]`. Two new arch
tests keep it honest (consumer purity + no planning-namespace type in the engine assembly).
Also: ratchet.ps1's pragma ceiling had been silently breached since G3.3 (39>38 — prior sessions
ran the test-filter ratchet, not the script); fixed by making Telegram's control-file write async
and deleting its bare MA0045, not by raising the ceiling._

_**NEXT: P5 (session-token rollover surfaced)** — independent of P1–P4, rides G3 reload. Surface
`limits.maxSessionTokens` + `softBreakRatio` in the Face Plan-tab limits as an honestly-labeled
"Session token rollover (DeepSeek-style): OFF" control; extend `/plan/edit` limits target; add a
session-scoped this-run-only override (new `*ThisRun` flag in the `SkipVerificationThisStage`
pattern) flipped via a control verb that NEVER writes the plan file. Off stays the default; gate:
crossing the cap ends the session `RolledOver` with a handoff and no burned attempt.
Read `docs/CONDUCTOR-PLANNER.md` §P5 before starting._
