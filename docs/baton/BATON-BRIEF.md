# Baton — Conductor v2 Master Brief

**Status:** PLAN WRITTEN (not executed) — 2026-07-08
**Author:** design pass over the whole `conductor` repo + the two live mega-plans it drives
(DevContext2-ui/Loom, Shamshir/parity-pipeline). Code + docs verified, not assumed.
**Base branch:** `master` @ `56a28e8` → this plan lives on `feat/baton` in the worktree
`C:\Code\conductor-baton`.
**Driven by:** the **stable published `bin\conductor.exe`** (built from `master`). The tool that
improves Conductor is never the tool under edit.
**Audience:** the implementing agent(s), one autonomous session at a time, launched by Conductor.

> This file is the **design authority**. Read it first, then `CONDUCTOR-START.md` (the tracker),
> then your stage file in `docs/baton/stages/`. The stage files own the phased tasks and
> machine-checkable gates; this file owns the vision, the target architecture, the locked
> decisions, the cross-cutting standards, and the trust model that every stage must preserve.

---

## 0. What Conductor is (the reframe)

Conductor is a **stateful execution environment for days-long AI software engineering**. Most agent
tools optimise a single prompt; Conductor optimises *execution* of a very large, multi-stage plan,
one verifiable session at a time, while the owner is AFK or supervising via a live TUI.

```
        Human (TUI · CLI · Telegram)
                 │ steering · approvals
                 ▼
        Execution Engine (Orchestrator)     ← the kernel/scheduler
                 │
   Provider Adapter (opencode/claude/…)     ← the CPU
                 │
        Shell · Git · MCP                    ← the syscalls
                 │
            Repository                        ← the disk
```

The **CPU is swappable** (opencode/deepseek today; Claude, others tomorrow — Baton decouples this).
Conductor is the OS: session state, checkpoints, plans, resume, retries, orchestration,
observability, human steering.

### 0.1 The trust model (why the whole thing is shaped like this — NEVER weaken)

Conductor **never trusts the agent's claims.** After every session it independently verifies:

1. **Gate battery** — real PowerShell/shell exit codes, re-run by Conductor itself.
2. **New git commits** — the agent actually committed.
3. **Tracker checkpoint diff** — a row actually flipped to DONE.

A checkpoint counts only when **all three** agree. "All DONE" is confirmed with one more full
battery before the plan closes. Failures loop back as *fix* sessions whose prompt embeds the actual
failing evidence. Everything is resumable from persisted state.

**Every stage in this plan preserves that invariant.** The one feature that could undermine it —
parallelism (B12) — is explicitly designed so the *only* new verification surface is a **merge
gate**: a parallel lane's mutation is never accepted until the full battery passes on the integrated
tree. Read-only lanes produce artifacts only and touch no working tree.

---

## 1. Why Baton exists — verified findings

Each finding was confirmed by reading the code on `master` and the live run state, not the docs.

### F-1 — Conductor is welded to Loom's tracker shape
`TrackerParser` (`Core/TrackerParser.cs:38`) hard-codes a regex for `| L0.1 | … | DONE | commit |
evidence |` rows and a `## Handoff` block. Shamshir's progress lives across `PROGRESS.md` +
`PLAN.md` + an `AGENTS.md` RESUME block with irregular ids (`P-0`, `P3.4b`, `F5`). Conductor cannot
drive Shamshir today. **Decoupling the progress/handoff/stage model is the spine of Baton.**

### F-2 — Provider coupling (opencode/deepseek) leaks into the engine
`AgentSession.cs` hard-branches `"opencode-json"` / `"stream-json"` / `"text"` parsing; the Loom plan
files bake `opencode run -m deepseek/deepseek-v4-pro …`. There is no provider abstraction — adding a
new agent means editing the session core. Baton introduces `IAgentProvider`.

### F-3 — Live token/cost is invisible off-dashboard (confirmed live this session)
Token/cost are written to `SessionRecord` only at session **end** (`Orchestrator.cs:277-283`). The
live dashboard shows a *cost* that includes the running session but a *token* line that lags a whole
session (`DashboardRenderer.cs:146` vs `:155`). AFK on your phone you can't see current burn. On the
live L3 run: session #12 had accrued ~$0.05 / 20k output tokens that never reached `state.json` or
`REPORT.md`.

### F-4 — Heartbeat commits pollute branch history (confirmed live)
`report.heartbeatMinutes:10` rewrites+commits `REPORT.md` every ~10 min with a message that changes
each time (`chore(conductor): s12 L3 working ▸L3.2 @ 01:12`). The no-op dedup (`Reporter.cs:172`)
only strips the `_Updated` line, so ~6-8 heartbeat commits per long session interleave with real
feature commits on `feat/loom-l3`.

### F-5 — The sub-plan tree regressed in the v2 dashboard
`PlanTable` (`DashboardRenderer.cs:189`) renders only top-level stages. Sub-checkpoints show only for
the *current* stage in a separate table. You can no longer see the hierarchy (L3 ▸ L3.1 done, L3.2
active) in one place.

### F-6 — Destructive keys fire on a single keystroke (finger-slip risk)
`A` (abort), `K` (kill), `S` (skip) fire immediately (`LiveDashboard.cs:222-226`); the out-of-process
`conductor abort/kill/skip` verbs write `control.json` with zero confirmation.

### F-7 — The audit captures "what was hard" but never transfers it forward
Audit sessions write an honest handover to `.conductor/handovers/<stage>.md` (`audit.md` template),
listing weak/deferred/bugs. That file is **only linked in REPORT.md — never injected into the next
session's prompt.** Knowledge is captured, not transferred.

### F-8 — No mid-session rollover for bloated contexts
The live session #9 (L2.3/L2.4) ran 181 turns / 28.5M cache-read tokens in one context. Conductor
can only start a fresh context at a checkpoint boundary; there is no cooperative "finish this
sub-task, hand off, start fresh" mechanism.

### F-9 — Everything is reactive; there is no execution history/replay
Observability is the terminal *now*. There is no event log, no timeline, no replay, no execution
health (loop/repetition/oscillation detection). `RunState.History` is a coarse per-session summary.

### F-10 — Tooling gaps for a SOLID .NET codebase
`net9.0`, no `.editorconfig`, no `Directory.Build.props`, no analyzers, no solution file, no
warnings-as-errors. The code is clean but unguarded against drift.

---

## 2. Locked decisions (owner-confirmed 2026-07-08)

| # | Decision | Choice | Consequence |
|---|----------|--------|-------------|
| D-1 | Self-hosting | **Conductor drives its own next iteration** | `plans/conductor.self.plan.json` + `CONDUCTOR-START.md`; the deliverable and the acceptance test for decoupling are the same thing. |
| D-2 | Tracker generality | **Pluggable `IProgressProvider`; strict `TRACKER.md` per plan is the default & recommended format** | `MarkdownTableProvider` (default), `ScriptProvider` (legacy/edge), `PlanCheckpointProvider`. Consolidate docs to one format most of the time; providers are the escape hatch. |
| D-3 | Telegram | **Two-way: read + intervene** via long-poll `getUpdates` + inline-keyboard `callback_query` → `control.json`. | Phone-grade AFK control (status + pause/resume/skip/inject/approve). Chat-id allowlist. |
| D-4 | TUI | **Full alt-screen rewrite** on Spectre `Layout` | Real-terminal-app feel, clean restore, hierarchical tree. |
| D-5 | Event sourcing | **Full event-sourced backbone; `RunState` becomes a projection** | `events.jsonl` is the single source for state, replay, metrics, health, report, Telegram. Delivered additively; cutover only after StateCompat parity proven. |
| D-6 | Session branching | **Deferred** (not in Baton) | B10 keeps `dependsOn` + hierarchical stages + collapse-double-battery. Fork/compare revisited post-backbone. |
| D-7 | Personas | **Land at B7** | Daily-driver + Shamshir-headless first; specialists (planner/reviewer/architect/qa/docs) after. |
| D-8 | Task graph | **Layer beneath the verified checkpoint table; event-sourced; MCP surface for the agent** | Persists opencode's todo list across sessions. Checkpoint table stays the verified contract. |
| D-9 | Session breaking | **Cooperative soft-break + hard token-ceiling fallback** | Finish sub-task → clean handoff → fresh session at next sub-task; hard kill+fresh-start if the ceiling is blown. |
| D-10 | Parallelism | **Read-only analysis lanes first; isolated-worktree mutating lanes later behind full-battery merge gates** | B12, last by design. Bounded worker pool, concurrency cap, brain-scheduled, opt-in per task-type. |
| D-11 | Provider decoupling | **`IAgentProvider` abstraction; opencode/deepseek becomes one adapter** | Claude/others are config, not code. Loom/Shamshir plans stay working. |
| D-12 | Runtime | **.NET 10, modern C#, SOLID, warnings-as-errors, Meziantou.Analyzer** | `net10.0`, `.editorconfig`, `Directory.Build.props`, analyzer ruleset. .NET BCL "batteries" (Hosting/DI/Options/Logging/Channels) where they help. |

---

## 3. Target architecture

```
                     Human (TUI · CLI · Telegram)
                              │  steering · approvals · injects
                              ▼
   ┌───────────────── Execution Engine (Orchestrator) ──────────────────┐
   │  emits →  EVENT LOG  (.conductor/events.jsonl, append-only)         │
   │              │                                                      │
   │  projections │  RunState · TaskGraph · Timeline · Metrics · Health  │
   │              │  · Report                                            │
   └──────────────┼──────────────────────────────────────────────────────┘
        │                    │                    │              │
  Planning Adapter    Provider Adapter      Scheduler/Brain   Renderer
  IProgressProvider   IAgentProvider        (decompose·       (alt-screen
  · conventions       (opencode·claude·…)    dispatch·cap·     Spectre Layout
  · task graph        · MCP task surface     merge-gate)       · tree · replay)
```

### 3.1 Layer separation (formalise what is partly there)

- `Conductor.Core.Execution/` — Orchestrator, session loop, gate battery, verdict logic.
- `Conductor.Core.Planning/` — `IProgressProvider`, tracker parsing, conventions, task graph.
- `Conductor.Core.Providers/` — `IAgentProvider`, event stream parsing per provider.
- `Conductor.Core.Events/` — `ConductorEvent`, the append-only log, projections.
- `Conductor.Core.Integrations/` — Telegram, notify, MCP task server.
- `Conductor.Ui/` — renderer, modals, replay viewer (already isolated).
- `Conductor.Cli/` — command handlers (already isolated).

Composition via `Microsoft.Extensions.Hosting` + DI + Options pattern (B2). Structured logging via
`Microsoft.Extensions.Logging` + Serilog sinks.

### 3.2 The event log (the spine — B2)

Append-only NDJSON at `.conductor/events.jsonl`. Every meaningful transition is an event:

```
RunStarted        { plan, repo, branch, driverVersion }
StageEntered      { stageId, startHead }
SessionStarted    { number, kind, stageId, attempt, sessionId, persona }
TaskAdded         { taskId, checkpointId, title, source }      (B9)
TaskStatusChanged { taskId, status }                           (B9)
Thought           { text }                                     (from provider stream)
ToolCalled        { tool, argsDigest }
CommandStarted    { command, cwd } / CommandFinished { exitCode, ms }
GateStarted       { name } / GateFinished { name, passed, exitCode, ms }
TokenDelta        { input, output, reasoning, cacheRead, costUsd }   (per step_finish — fixes F-3)
CheckpointConfirmed { id, evidence }
Retry / Resume / RolledOver                                    (B8/B9)
HumanInput        { text } / OwnerApprovalRequested / OwnerApprovalGranted   (B3)
StageFinished     { stageId, outcome } / RunFinished { status }
```

Rules: **append-only, never mutate**; each event carries `runId`, `sessionId`, `ts`. `RunState`,
`TaskGraph`, `Timeline`, `Metrics`, `Health`, and `REPORT.md` are all **projections** rebuilt by
folding the log. Crash recovery = replay the log. Delivered additively (emit alongside `state.json`
until projections match under `StateCompatTests`), then `state.json` becomes a cache.

### 3.3 The task graph (B9 — layer beneath the checkpoint contract)

- The **checkpoint table stays the verified contract** (D-8). Sub-tasks are advisory guidance +
  break-points, deliberately lightweight (over-planning is an anti-pattern — see §7).
- Produced by the **planner persona** decomposing the active checkpoint into ordered sub-tasks.
- **Event-sourced** (`TaskAdded`/`TaskStatusChanged`) → a `TaskGraph` projection.
- **MCP surface:** a small MCP server exposes `task_list` / `task_update` / `task_add` so the agent
  updates the shared graph live. This **persists opencode's in-session todo list across sessions** —
  the key unlock for "continue at the next sub-bullet."
- Consumed by: orchestrator (seed prompts, pick break-points), agent (MCP), human (CLI/TUI/Telegram).

### 3.4 Controlled parallelism (B12 — last, read-only-first)

- **Tier A — read-only analysis lanes:** architecture review, design proposals, QA of committed
  work, research. Run like today's `StatusAgent` (read-only by construction, scratch cwd). Never
  touch the working tree → no git contention, no merge gate. Outputs feed the next prompt + handover.
- **Tier B — isolated-worktree mutating lanes:** a delivery/fix task in its **own `git worktree`**
  on a scratch branch, merged back at an explicit **merge gate** (full battery on the merged tree
  before acceptance). Bounded worker pool, low concurrency cap, brain-scheduled, opt-in per task-type.
- **fix-lanes** consume `.conductor/followups.md` (blend-in debt fixing).

---

## 4. Provider decoupling (D-11, delivered in B2/B7)

`IAgentProvider` abstracts session spawn + event stream parsing:

```csharp
public interface IAgentProvider
{
    string Name { get; }
    AgentLaunch BuildLaunch(AgentLaunchRequest req);          // command, args, resume args
    IAsyncEnumerable<AgentEvent> ParseStream(                  // text/thinking/tool/token/cost/result
        TextReader stdout, TextReader stderr, CancellationToken ct);
    bool DetectsUsageLimit(string evidence);                   // per-backend rate-limit phrases
}
```

Adapters: `OpencodeProvider` (today's `opencode-json` parser), `ClaudeProvider` (`stream-json`),
`GenericTextProvider`. Plan config selects by name; `{prompt}`/`{sessionId}`/`{resumeId}` stay the
substitution contract. The Orchestrator depends only on `IAgentProvider` — no provider `switch`.

---

## 5. .NET engineering standards (D-12 — applied in B0, enforced thereafter)

- **TFM:** `net10.0` (SDK 10.0.301 present). Migrate `Conductor.csproj` + tests.
- **Modern C#:** file-scoped namespaces, primary constructors, collection expressions, `required`
  members, pattern matching, `record`/`record struct`, `System.Threading.Lock` (.NET 9+) instead of
  `lock(object)` where hot, `TimeProvider` for testable time, `System.Text.Json` source-gen for
  hot-path (de)serialisation.
- **Async/threading:** no `async void` (except event handlers); `ConfigureAwait(false)` in library
  code; `CancellationToken` threaded through all long ops; `System.Threading.Channels` for the
  event bus + agent event queue (replaces hand-rolled `ConcurrentQueue` polling where it helps);
  `Task.Run` only at boundaries; no blocking `.Result`/`.Wait()` on async in the hot path;
  `PeriodicTimer` for heartbeats.
- **DI/host:** `Microsoft.Extensions.Hosting`, `Options` pattern with validation, `ILogger<T>`
  structured logging (Serilog file+console sinks), correlation properties (runId/sessionId/stage/gate).
- **Analyzers:** `Meziantou.Analyzer` (https://github.com/meziantou/Meziantou.Analyzer) +
  `Microsoft.CodeAnalysis.NetAnalyzers` (built-in), `TreatWarningsAsErrors=true`,
  `EnforceCodeStyleInBuild=true`, centrally in `Directory.Build.props`. `.editorconfig` sets style +
  analyzer severities (see `docs/baton/tooling/` drafts). A curated rule set (not the raw firehose)
  chosen in B0 with rationale in an ADR.
- **Packages:** central via `Directory.Packages.props` (`ManagePackageVersionsCentrally`).
- **Solution:** `Conductor.slnx` (modern XML solution) is the single build entry — it already exists
  on `master`; B0 verifies/adopts it.
- **Tests:** xUnit, `TimeProvider`-based determinism, property-style for the state machine, mock
  Telegram/provider servers for integrations. Ratchet-only: the suite never weakens to go green.
  **Value-only:** add a gate or test only when it protects a real behaviour, invariant, or a
  regression that has actually bitten. No coverage-theatre; a small load-bearing suite beats a large
  brittle one. A checkpoint proven by the build gate + one focused test + a real artifact is enough.

---

## 5.1 Delivery pipeline & gating philosophy (optimised for this plan)

The self-plan is tuned so each stage is delivered cheaply and finished honestly:

- **Gates are minimal and load-bearing.** The self-plan battery is just `build` (fast tier, per
  session) + `tests` (full tier, phase end). No pnpm/mcp/guards theatre — Conductor is a single .NET
  app. Warnings-as-errors (B0) means the build gate already enforces analyzer + style quality, so we
  don't need separate lint gates. Stage-specific gates are added ONLY where a stage introduces a real
  new invariant (e.g. B2 event-schema round-trip, B4 headless renderer) — named in the stage file,
  never generic.
- **The audit FIXES, it doesn't just document.** After a phase's battery is green, the audit session's
  mandate is to *fix* leftovers (shallow impls, TODOs, edge cases, dead params, async slips) within
  the diff budget — not merely list them. Only genuinely out-of-scope items are deferred, and those
  become tracked followups (`.conductor/followups.md`, B8).
- **Leftovers get a real fix path, not silent persistence.** Three mechanisms, in order of preference:
  1. the fix template's leftover-sweep (a fix session cleans prior-session debt while making claims
     true);
  2. the audit's active-fix mandate (above);
  3. B8 followups feeding the NEXT phase's opening, and — once B12 lands — dedicated **fix-lanes** that
     consume `followups.md` behind a full-battery merge gate. A phase-confirm can optionally block on
     an unacknowledged *critical* gap (B8.4) so risky debt can't sail past.
- **Token/session efficiency:** perPhase gates (fast build per session, full battery once per phase)
  + HEAD-sha cache skip re-runs on unchanged trees; B10 collapses the agent-ritual + conductor double
  battery to one source of truth; B8/B9 roll long sessions over cleanly instead of burning a bloated
  context. The goal is fewer, higher-value sessions — not more ceremony.

---


## 6. Session protocol (baked in — every Baton session follows this)

This is the contract Conductor enforces on itself, mirroring the Loom/Meridian/Shamshir rituals.

**Session start (mandatory):**
1. Read `CONDUCTOR-START.md` handoff + this brief + your stage file in `docs/baton/stages/`.
2. **QA the previous session:** re-run its stated gate; independently verify two of its claims (one
   against tests, one against the running app/artifact). Record the verdict in the handoff.
3. Only then continue the plan.

**During the session:**
- One sub-task/checkpoint = one commit; paste gate output in the body.
- A gate is a command + its output. "Should work" is not a gate.
- Evidence or it didn't happen: a checkpoint without a fresh artifact path is not DONE.
- Ratchet-only: never weaken analyzers, warnings-as-errors, tests, or truth files to go green.
- Diff budget: if `git diff --stat` exceeds ~15 files or touches files your checkpoint didn't name,
  split the commit or revert the extras (DeepSeek scope-creep guard).
- If genuinely blocked on a human decision: set the row BLOCKED, add a `HUMAN:` line to the handoff,
  commit, push, stop.

**Session end (mandatory):**
1. Overwrite the `## Handoff` block in `CONDUCTOR-START.md` (≤12 lines, no history).
2. Update the checkpoint rows you touched (Status, Commit, Evidence path).
3. Nothing uncommitted except WIP explicitly named in the handoff.
4. Print one paragraph starting `SESSION-RESULT:` — what landed, what is red, next step, **and what
   was hard** (the struggle note that B8 harvests into the lessons brief).

**Per-phase audit (audit=on in the self-plan):** after a stage's full battery is green, an audit
session reviews the phase diff, self-fixes bugs/shallow impls/missing edge cases, re-verifies, and
writes an honest `.conductor/handovers/B<n>.md`. Its weak/deferred bullets become tracked followups
(B8) fed into the next phase's prompt.

---

## 7. Anti-pattern catalog (banned — adapted from the Meridian playbook §4)

| # | Anti-pattern | Rule |
|---|--------------|------|
| A1 | Dead-parameter fix (plumb a param, never consume it) | After adding a param, cite the line where its value changes behaviour. |
| A2 | Silent checkpoint renumbering | Diff tracker rows vs the plan before writing DONE; announce scope changes. |
| A3 | Stub artifact (empty file cited as evidence) | Every artifact must be content-asserted; never cite one you didn't open. |
| A4 | Gate skipped, claimed run | Gates produce files/output; no output ⇒ not run. |
| A6 | Ship-without-launch | First test of any executable path: run it once for real. |
| A14 | TODO-as-delivery | A `// TODO` in your checkpoint's diff = the checkpoint is not done. |
| A15 | Catch-and-continue | Catch specific exceptions, log with context; failures surface in status (no silent `catch {}`). |
| A16 | Over-planning the task graph | Sub-tasks are lightweight break-points, not a rigid contract reality must match. |
| A17 | Weakening the guardrails | Never lower analyzer severity or `TreatWarningsAsErrors` to pass — fix the code. |

---

## 8. Stage map (dependency order — 13 stages)

```
FOUNDATIONS
  B0  Repo modernisation + self-hosting harness + baseline audit
  B1  Decouple Loom · IProgressProvider · conventions · read-order battery
SPINE
  B2  Event-sourced backbone (RunState = projection) · layer separation · IAgentProvider · Serilog/Host/DI
CONTROL
  B3  Safety · owner-gates (AwaitingOwner) · process control (retry/rollback/pause-after-stage/budgets/approval)
EXPERIENCE
  B4  TUI overhaul (alt-screen · Spectre Layout · hierarchical tree · severity · structured thinking · tool folding · filters/search · live-consistent tokens · doc-on-select)
  B5  Observability & health (timeline · replay/time-travel · AI-health · confidence · MCP metrics · live token/cost)
REACH
  B6  AFK + two-way Telegram · richer REPORT.md (clean heartbeat)   └─ acceptance: drive Shamshir P-0 + P0.1 headless
INTELLIGENCE
  B7  Specialist sub-agent personas (planner/reviewer/architect/qa/docs)
  B8  Brain layer (reflection · self-review kind · advisor · lessons→prompt · handover-gaps→followups · batteries · token rollover)
TASK SYSTEM
  B9  Task graph + smart session management (planner decomposition · event-sourced store · MCP surface · cooperative soft-break + hard fallback · CLI/TUI/Telegram views)
ADVANCED ORCHESTRATION
  B10 dependsOn graph · hierarchical stages · collapse double battery   [no branching]
CLOSE-OUT (core)
  B11 Cross-platform gates · dotnet tool packaging · ADRs · drive a full owner-gated Shamshir phase (P2.2)
PARALLELISM (v2.1-grade, last by design)
  B12 Controlled parallelism (Tier A read-only lanes → Tier B isolated-worktree mutation behind merge gates · fix-lanes consume followups)
```

**Critical paths:** B0→B1 unblock everything. **B2 precedes B5, B9, B12.** B7→B8→B9. **B12 last.**
Shamshir headless-drivable by end of **B6**; hard owner-gated+credentialed case at **B11**; parallel
fix-lanes at **B12**. Daily-driver pains (F-3…F-6) fixed early in **B3–B5**.

See `docs/baton/stages/B0.md` … `B12.md` for per-stage requirements, task breakdown, session
sizing, and gates. See `CONDUCTOR-START.md` for the live checkpoint tracker.

---

## 9. Definition of Done (whole program)

- Conductor drives **both** Loom (from `examples/loom/`) and a fresh Shamshir iteration (strict
  `TRACKER.md`) — providers + conventions + owner-gates proven on a real second plan.
- `net10.0`, warnings-as-errors, Meziantou ruleset green, `.editorconfig` + central packages.
- Event log is authoritative; a run recovers from it; timeline + replay work.
- Two-way Telegram: status + pause/resume/skip/inject/approve from the phone.
- Alt-screen TUI with hierarchical tree, severity, folded tool calls, live-consistent tokens.
- Task graph with MCP surface; cooperative session-breaking; lessons transferred forward;
  handover gaps tracked as followups.
- Controlled parallelism: read-only lanes in production; isolated-worktree mutation behind merge
  gates.
- The whole thing was **delivered by Conductor sessions under the §6 protocol** — the plan is its
  own proof.
