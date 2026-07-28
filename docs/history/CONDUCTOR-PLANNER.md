# Conductor Dynamic Planner — design brief (P-series)

**Written:** 2026-07-16. **Scope:** a decoupled, rules-driven planner — author a pipeline from a doc or
by hand, see it as a live Kanban + pipeline, and tweak the *rules* (who works what, who audits, when QA
runs, prompt building-blocks per task) dynamically. **Not** implementation — this is the brief the
tracker (`CONDUCTOR-PLANNER.md`) and plan (`plans/conductor-planner.plan.json`) execute. Read this → the
tracker → the stage notes.

**Prerequisite: G3 (live plan reload) from the G-series must land first.** The whole "tweak it live"
story rests on `ControlAction.ReloadPlan` swapping the running plan at the session boundary
(`docs/history/CONDUCTOR-AI-NATIVE.md` §G3). Without G3, every planner edit here degrades to
takes-effect-on-restart. Land G3, then this.

**The one-line thesis.** Most of the pipeline machinery the owner wants *already exists as plan config*
(`WorkflowEngine` with `deliver-verify` / `big-dev-then-big-audit` / `docs-only` / `spike`, per-stage
`workflow` / `agent` / `persona` / `overrides`, `RunIf`/`SkipIf` conditionals, `verifierThreshold`). It
is just **invisible, non-editable, static, and welded to the engine.** So the P-series is: **surface it,
make it editable, make it dynamic, and decouple it into a library** — *reuse, do not rebuild.* If you
find yourself writing a second workflow engine or a second task store, stop.

---

## Design principles (code-quality contract — the ratchet enforces these)

- **Dependency direction is one-way.** The new `Conductor.Planning` assembly must **not** reference the
  engine (`Conductor` orchestration / store / http / Spectre). The engine references
  `Conductor.Planning`, never the reverse. **P0 adds an architecture test asserting this** (see
  `ArchitectureTests` / the ratchet); a reverse reference must fail CI, not just review.
- **Planning is pure.** The library takes *data in* (a plan + declarative rules + a POCO of runtime
  facts — reuse `WorkflowRuntimeVars`) and returns *decisions out* (next step, assignment, QA action).
  No `RunContext`, no `SqliteRunStore`, no IO, no `DateTime.UtcNow` reached for internally. This is what
  makes it unit-testable in isolation and usable standalone — a first-class goal, not a side effect.
- **Rules are declarative and agnostic.** The pipeline rules are a plain JSON block (POCOs with sane
  defaults) that a non-Conductor consumer could author. The policy *interprets* rules; it never hard-codes
  them. Rules can be overridden from outside without recompiling.
- **Interfaces at the seam.** The engine calls the planner through small interfaces
  (`IWorkflowResolver`, `IAssignmentPolicy`, `IQaPolicy` — or one `IPipelinePolicy` façade). Default
  implementations live in `Conductor.Planning`; the engine wires them via DI (like `IPlanner` already is).
- **Model ownership is the key design call (P0 + P4).** The planning-domain types
  (`WorkflowDefinition`/`WorkflowStep`/`WorkflowOverrides`, the new `PipelineRules`/`Assignment` types,
  `WorkflowRuntimeVars`) belong *in* `Conductor.Planning`. `SessionKind` is shared vocabulary — move it
  (or a planning-local twin that maps 1:1) so the library needs no engine reference. Decide this deliberately
  in P0; do not leave planning types stranded in the engine assembly.
- **CQE layering holds** (AGENTS.md §"Command/Query/Event layering"): assignment and task writes **emit
  events**; reads **fold projections**. New control-plane writes go through the existing services and the
  per-run token (G-series hardening). Don't reach into `Orchestrator` internals.
- **Determinism by default.** Assignment and QA decisions are a deterministic function of the rules +
  facts. A model/advisor is consulted **only** where the owner explicitly invokes it (prompt-refine,
  freeform import) — never silently inside a scheduling decision.
- **Face parity.** Every new interactive surface gets a golden + a key-driving unit test, and a demo-source
  mirror so it's reviewable at zero spend (the repo's bar — see the G2 Kanban / G1 Prompt work).
- **Ratchet & file size.** New endpoint handlers in their own partial past ~500 lines; no analyzer
  suppressions added to pass; `sealed`/`record`/primary-ctors/file-scoped namespaces per the house C# style.

---

## P0 — Planner core: the `Conductor.Planning` library + rules model

**Goal.** Stand up the decoupled home *first*, so every later stage builds into it instead of bolting
decoupling on at the end. Define the agnostic rules schema and the policy seam, move the already-pure
workflow logic across, and clear one piece of debt found in the audit.

**What exists (reuse it):**
- `WorkflowEngine` (`Core/Orchestration/WorkflowEngine.cs`) is *already nearly pure* — it resolves a
  `WorkflowDefinition` for a stage, walks steps with `RunIf`/`SkipIf`, and records the step index. It
  references only Models types + `SessionRecord` (via `BuildRuntimeVars`, which produces the POCO
  `WorkflowRuntimeVars`).
- `WorkflowDefinition` / `WorkflowStep` / `WorkflowOverrides` / `WorkflowRuntimeVars` (`Models/`) are the
  rule vocabulary. `IPlanner` + `CheckpointPlanner` already establish the DI-injected planner pattern.

**Net-new:**
1. **New project `src/Conductor.Planning/Conductor.Planning.csproj`** — no reference to `Conductor`.
   Move the planning-domain model types and the pure `WorkflowEngine` into it. `BuildRuntimeVars` (the one
   method that touches `SessionRecord`) stays engine-side as a thin adapter that constructs the POCO; the
   library only ever sees `WorkflowRuntimeVars`.
2. **A declarative `pipeline` block on `PlanConfig`** (`PipelineRules` POCO): the agnostic schema that P1–P2
   populate — role→agent assignment map, QA policy, multi-item policy — with defaults that reproduce
   today's behavior exactly (so an existing plan with no `pipeline` block is byte-for-byte unchanged).
3. **The seam interfaces** (`IWorkflowResolver` at minimum; `IAssignmentPolicy`/`IQaPolicy` land as P1/P2
   fill them) with default impls in `Conductor.Planning`, wired via `ConductorHost` DI. The engine calls
   the interface, not `WorkflowEngine` directly.
4. **Delete the dead `AgentConfig.TokenCeiling`.** Audit finding: it is defined and merged but **enforced
   nowhere** — the ai-native plan's `"tokenCeiling": 64000` is a no-op that *looks* active. Remove it (the
   real per-session limit is `limits.maxSessionTokens`, handled in P5). Update any plan JSON that sets it.

**Design constraints:** the architecture test for one-way dependency is part of P0's deliverable, not P4's.
Keep `WorkflowEngine`'s public API intact so the engine's call sites change to an interface, not a rewrite.

**Gate:** solution builds with `Conductor.Planning` as its own assembly; the engine depends on it and not
vice versa (asserted by a new architecture test); the full existing suite stays green (behavior unchanged —
defaults reproduce it); `tokenCeiling` is gone and grep-clean.

---

## P1 — Role→agent assignment + multi-item sessions

**Goal.** Decouple agent-from-session-from-task: rules say *which agent* delivers, verifies, audits, and
fixes, and a single session may work *more than one* ready item.

**What exists (reuse it):**
- Per-stage `agent`/`model`/`persona` overrides and `WorkflowStep.Model`; `PersonaRegistry` for roles.
- The task graph (`TaskGraph`, `TaskWrites`, `GET /tasks`) and the active-checkpoint selection in
  `SessionRunner` (`preTrack.ForStage(stage.Id).FirstOrDefault(c => !c.IsDone)`).

**Net-new:**
1. **`IAssignmentPolicy`** in `Conductor.Planning`: given the pipeline rules + the ready task graph +
   the session kind, return a `SessionAssignment` — the resolved agent (command/model/persona) and the
   **set of task ids** this session claims. Default rule reproduces today (one agent, the active
   checkpoint). The rules add a **role→agent map** (`deliver`/`verify`/`audit`/`fix` → model + persona)
   so "who audits" is data, not code.
2. **Multi-item claim:** the policy may claim several ready cards under the active checkpoint (already the
   de-facto behavior) and — behind an explicit rule flag — conflict-free sibling checkpoints, using
   `StageConfig.PathClaims` to refuse overlapping file claims. The engine executes the assignment; the
   *decision* stays in the library. The prompt lists every claimed item.
3. The engine's session-start path asks `IAssignmentPolicy` instead of hard-picking the first checkpoint;
   the run loop stays "dumb executor," the policy stays "smart decider."

**Design constraints:** the run loop must never work two items that path-conflict; validate in the policy
(pure, testable) and assert with a unit test. Full "work-queue" scheduling (tasks as the only unit) is the
**north star, explicitly out of scope** — this is a bounded step, not a run-loop rewrite.

**Gate:** a plan whose rules assign `audit`→a different model than `deliver` spawns the audit session with
that model; a session claims two conflict-free cards and the prompt names both; a path-conflicting claim is
refused by the policy (unit test). Existing single-item plans are unchanged.

---

## P2 — QA policy dial: off / every-session / phase-gate

**Goal.** One clear, editable, dynamic dial for QA frequency that maps onto the existing workflow +
override machinery — "turn QA off," "only at phase gates (audit over all prior sessions + sub-items),"
or "every session."

**What exists (reuse it):**
- The workflows already *are* these modes: `every-session` = `deliver-verify`; `phase-gate` =
  `big-dev-then-big-audit` (deliver repeatedly → one consolidated audit over accumulated work → fix-sweep);
  `off` = `spike`/`docs-only` or `overrides.skipVerification`. `verifierThreshold` + `WorkflowOverrides`
  (`skipVerification`/`skipGates`/`skipCommit`) + the session-scoped `SkipVerificationThisStage` flag.

**Net-new:**
1. **`IQaPolicy`** in `Conductor.Planning`: a `QaPolicy` rule (`off` | `everySession` | `phaseGate`,
   plus threshold + "audit covers all prior sessions") that **resolves to** an effective
   `WorkflowDefinition` + overrides — no new engine concept, just a friendly dial over what exists. Settable
   plan-wide and per-stage.
2. **Face:** the Plan-tab **Settings** section (and per-stage) gains the QA dial + `verifierThreshold`;
   edits ride G3's live reload so they apply at the next session boundary. Golden + key test + demo mirror.

**Design constraints:** the dial is a *projection onto workflows*, not a parallel scheduler — resolving a
dial value must produce exactly the same run as selecting the corresponding workflow by hand today
(pin this with a unit test comparing resolved definitions).

**Gate:** setting QA=`off` on a stage runs deliver-only; QA=`phaseGate` runs deliver×N then one audit+fix
over the accumulated checkpoints; the dial round-trips through `/plan/edit` and is honored live after reload.

---

## P3 — Kanban card detail: prompt building-blocks + edit + AI-assist

**Goal.** Click a Kanban card → see the *building blocks* of that item's prompt (not the whole compiled
wall of text), add information to the task-scoped block, and ask an AI to refine it — the advisor (Fable)
or hand it to Claude.

**What exists (reuse it):**
- `PromptBuilder` composes the prompt from persona + stage notes + task + injected batteries (ledger/bugs)
  + tool contract; `GET /prompt/preview?stage=&kind=` returns the *whole* compiled prompt. G1's
  `/plan/import` advisor plumbing (`Advisor.AskTextAsync`, preview→confirm) is the pattern for AI-assist.
- The Kanban tab (`tab_kanban.go`), the task graph, `ApplyPlanEdit`.

**Net-new:**
1. **A `PromptComposition` view** — a pure decomposition of `PromptBuilder`'s output into an ordered list of
   labeled `PromptBlock { Kind, Label, Content, Editable }` (persona · stage notes · task title · per-task
   context · injected knowledge · tool contract). Pure function of state ⇒ previewable + testable. Serve it
   at `GET /tasks/{id}/prompt` (or `/prompt/blocks?task=`).
2. **Face card detail:** selecting a card opens a panel showing the blocks; the **task-scoped** blocks are
   editable (task title, a structured per-task "extra context" field) and persist as task data — **not**
   free-form prompt splicing.
3. **AI-assist actions:** "ask the advisor to refine this item" (reuses G1's advisor call, scoped to the
   task, returns a proposed edit you confirm) and "hand to Claude / another brain" (writes an injection /
   handoff via the existing `/inject`). Both are explicit, owner-invoked, token-gated.

**Design constraints:** editing writes *structured task data* (per-task notes/context), never raw prompt
strings — the composition stays a deterministic function of structured state. The advisor-refine reuses the
preview→confirm→apply contract; nothing mutates without confirmation.

**Gate:** a card detail shows the labeled blocks; editing the task's extra-context changes exactly that
block in the recomposed prompt (unit test on the pure composition); an advisor-refine returns a proposed
edit that applies only on confirm; goldens for the panel.

---

## P4 — Finish the decoupling: `Conductor.Planning` stands alone

**Goal.** Complete the extraction started in P0 — move the remaining decision logic entangled with the
engine behind the seam, and *prove* the library is usable on its own.

**What exists (reuse it):**
- P0's assembly + interfaces; `VerdictEngine`'s workflow-driven "what next" logic
  (`VerdictEngine.Workflow.cs`) that currently reaches into `RunContext`/`SessionRecord`.

**Net-new:**
1. Move the workflow/assignment/QA *decision* logic fully behind `IWorkflowResolver`/`IAssignmentPolicy`/
   `IQaPolicy`; the engine passes POCOs (facts) in and executes decisions out. `VerdictEngine` keeps
   *effecting* verdicts (state, events) but *delegates the decision* to the library.
2. **Standalone-usage proof:** a tiny consumer that references **only** `Conductor.Planning` (a test
   project, or `tools/plan-lint`) loads a plan file and prints the resolved workflow + assignment + QA
   decision for a stage — no engine, no store. This is the gate that the decoupling is real.

**Design constraints:** no behavior change to real runs — this is a structural move guarded by the existing
suite. The standalone consumer must compile and run with zero transitive dependency on `Conductor`.

**Gate:** the standalone consumer resolves decisions from a plan file using only `Conductor.Planning`; the
architecture test still passes; full engine suite green; no planning-domain type remains in the engine
assembly.

---

## P5 — Session-token rollover + limits, honestly surfaced and session-scoped

**Goal.** Make the "kill the agent past a token count and continue next session" mechanism (the audit's
`RolledOver` path) visible, off-by-default, and one-click toggleable — including a live, this-run-only
override.

**What exists (reuse it):**
- `limits.maxSessionTokens` (**null = off by default**; when crossed, the session ends `RolledOver` — a
  handoff is written, the next session starts fresh, **no attempt burned**) + `limits.softBreakRatio` (the
  cooperative 80% wind-down nudge, active only when `maxSessionTokens` is set). The session-scoped
  `SkipVerificationThisStage`/`SkipGatesThisStage` flags are the pattern for "this run only" toggles.

**Net-new:**
1. **Face:** surface `maxSessionTokens` + `softBreakRatio` in the Plan-tab limits as an honestly-labeled
   **"Session token rollover (DeepSeek-style): OFF"** control — editable, with a **session-scoped**
   "this run only" override (new `*ThisRun` flag in the existing pattern) so it flips live via a control
   verb without touching the plan file. Off remains the default.
2. Extend `ApplyPlanEdit` + the live-limits Settings (built in P2) to cover these fields; ride G3 reload.

**Design constraints:** off-by-default must stay true (an absent `pipeline`/limits block changes nothing).
The live override is session-scoped state (like `SkipVerificationThisStage`), not a plan-file write, so it
evaporates at run end by design.

**Gate:** rollover is visibly OFF by default in the Face; toggling it on (plan or this-run) makes a session
past the token count end `RolledOver` with a handoff and no burned attempt; the this-run override never
writes the plan file.

---

## Cross-cutting notes for the delivering agent
- **Reuse, don't fork.** P-series surfaces + decouples the existing `WorkflowEngine`/overrides/task-graph;
  it does not build a second of any of them. Two new *concepts* only: the agnostic `pipeline` rules block
  and the `Conductor.Planning` seam.
- **The library is the product.** Purity + one-way dependency + a standalone consumer are the code-quality
  gates that matter most here — they're what make the planner "usable separately" and the planning logic
  "not coupled within application logic," exactly as the owner asked.
- **Dynamic rests on G3.** Land G3 (live reload) first; every editable knob here applies at the session
  boundary through `ControlAction.ReloadPlan`.
- **Zero-spend review.** Everything Face-side must be exercisable in `--demo` with a mirrored demo source
  (goldens at 80×24 / 120×30 / 200×50), and every new control-plane write needs a contract test behind the
  per-run token.
- **Order the work by dependency, not by number.** P0 (library + seam) unblocks P1/P2/P3; P4 finishes what
  P0 starts; P5 is independent Face+config. Sizing (sessions per stage) is the delivering agent's call.
