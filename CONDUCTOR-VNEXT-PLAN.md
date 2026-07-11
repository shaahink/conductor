# Foreman Phase Tracker

**Plan:** Foreman | **Branch:** `feat/foreman` | **Design doc:** docs/CONDUCTOR-VNEXT-PLAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: s32 (opencode direct) — F8 DELIVERED (first pass). Plan import (F7.1), conductor chat (F8.1), Telegram v2 (F8.2-F8.3). F8.4 (acceptance: phone-only toy run) still TODO.
stage: F8 IN PROGRESS — 3 of 4 checkpoints DONE, 1 TODO (F8.4 acceptance test). F7.2 (re-import diff) TODO.
commits: c51b7eb (F8: chat + Telegram v2 + plan import).
gate: dotnet 647/647 pass 0w/0e. face/ 20/23 pass (3 pre-existing golden snapshots — ProcessPane elapsed-time).
branch: feat/foreman.
next: F8.4 acceptance test (phone-only drive of a toy run, laptop lid closed). F7.2 re-import diff (mid-plan changes). F9 dogfood close.
qa: static audit clean. All F8 changes additive + backward-compatible. New MCP tools (run_query, ledger_list, session_detail, inject_instruction) extend existing surface. TelegramService.RunDb is optional (null-safe). ChatCommand uses existing Advisor/AgentSession infrastructure.
struggle: ProcessPane golden snapshots — 3/23 face tests fail on elapsed-time. Needs component-level `now` prop override.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 40 |
| Done | 31 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path produced by a run this
phase (a code path is not evidence).

### F0 — Foundations — kill list, async engine, integration harness

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F0.1 | Kill list executed — delete replay/time-travel, persona bloat (keep 3 roles), confidence pane, heartbeat commits to feature branch, hierarchical template system | DONE | 47798ee | 531 tests pass, 0w/0e, 23 files changed, -687 net LOC across 5 commits |
| F0.2 | Async control loop — Orchestrator run loop async (B4.7 debt); no blocking .Result/.Wait() | DONE | 09dc2ec | 533 tests pass, 0w/0e, 9 private methods converted to async, 6 Thread.Sleep→Task.Delay, 3 .GetAwaiter()/.Result→await |
| F0.3 | Integration harness — fake agent + temp repo, full cycle asserted (B4.8); gate: 0w/0e, harness cycle green | DONE | b6e5d8b | HarnessTests.cs — 2 tests (full cycle + dry-run), fake cmd agent writes opencode JSON, 533/533 pass |

### F1 — run.db task store + tracker-as-view + task/note verbs

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F1.1 | run.db schema — tables: runs, stages, sessions, attempts, gates, scores, ledger, handovers, injections, costs; telemetry per D8 | DONE | 6330c60 | RunDbTests.cs — 12 tests pass, schema auto-creates (idempotent), session/gate/cost round-trip, parameterised query, 11 tables |
| F1.2 | Tracker-as-view — conductor writes TRACKER.md FROM run.db (generated view for humans/agents); regenerates byte-stable | DONE | 1c8c888 | TrackerGenerator.cs — generates TRACKER.md from run.db checkpoints table; idempotent seed; wired in Orchestrator at InitializeRun + EmitSessionFinished + handover write; 15 RunDbTests pass including 3 new checkpoint tests |
| F1.3 | conductor task/note verbs — task CRUD + note (writes ledger); MCP surface; agents report progress via verbs instead of hand-editing markdown | DONE | 1c8c888 | NoteCommand + TaskCommand CLI verbs; McpTaskServer conductor_note tool; McpServeCommand wires RunDb; 548/548 tests pass |
| F1.4 | conductor report --query — ad-hoc SQL/DSL against run.db ("cost of stage R3?", "which gates fail most?") | DONE | 1c8c888 | ReportCommand --query <SQL> option; runs parameterised SQL against run.db; renders results as Spectre table |

### F2 — ProcessSupervisor + Job Objects + bg primitives

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F2.1 | ProcessSupervisor + Job Objects — every child spawned into Windows Job Object; kill-by-tree, no orphans | DONE | 65c63c9 | ProcessSupervisor.cs — run-level JobObject with KILL_ON_JOB_CLOSE, ProcessRunner + AgentSession integrate via DI singleton, 9 tests prove track/untrack/JobObject assignment |
| F2.2 | PID registry in run.db + orphan reaper at startup | DONE | 65c63c9 | RunDb v3 schema (pids table, 8 columns), GetOrphanPids/TrackPid/MarkPidExited, ReapOrphans() at startup kills leftover PIDs + marks exited |
| F2.3 | conductor bg start / status / logs / stop — sanctioned background-run primitive; prompts mandate it for anything >3 min | DONE | 1db847a | BgCommand.cs — 4 sub-commands (start/status/logs/stop), spawns detached with log capture to .conductor/bg-logs/, queries run.db pids table for status, tails log files, kill-by-PID. 3 new RunDb.GetAllPids tests pass. Smoke-tested all 4 verbs. |
| F2.4 | MCP bg surface + harness proof — kill-by-tree, orphan reap, bg liveness feeds stall detector | DONE | eb1fa35 | McpTaskServer — 4 bg tools (bg_start/status/logs/stop); Orchestrator wires stateDir+repo; ProcessSupervisorHarnessTests — 5 tests prove JobObject kill-on-close, orphan reap e2e, liveness feed pipeline; 565/565 pass |

### F3 — Stall v2 + same-failure breaker + pre-flight

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F3.1 | Stall detection v2 — watches (a) agent stdout, (b) tool-call events from JSON stream, (c) liveness of supervised bg children | DONE | 0f0d67c | docs/baton/evidence/F3.1-gate/gate.txt |
| F3.2 | Soft-kill debrief — on stall: inject "wrap up, write ledger + handoff, 3 min grace", kill only after grace window | DONE | 0f0d67c | docs/baton/evidence/F3.1-gate/test.txt (575/575, +10 StallDetectorTests) |
| F3.3 | Same-failure circuit breaker — 2 consecutive attempts with identical failure signature → Advisor session (not another Deliver) | TODO | - | - |
| F3.4 | Pre-flight health check — DNS/API reachability, disk, git clean, budget remaining; fail → park + Telegram + auto-recheck with exponential backoff | TODO | - | - |

### F4 — Verifier role + scoring loop + findings-as-retry

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F4.1 | Verifier role — Deliver role + Verify role (fresh context, cheap model); Verify re-runs the checkpoint's truth gate independently | TODO | - | - |
| F4.2 | Score output — JSON {score 0-100, findings[], verdict}; ≥ threshold (default 80) → DONE, findings become follow-up tasks | TODO | - | - |
| F4.3 | Retry-with-findings — score < threshold → findings injected into Retry of Deliver (same model); QA-fix merged into retry (no separate fix session) | TODO | - | - |
| F4.4 | Advisor verdicts honored — structured AdvisorVerdict.Action (BlockRetry/NeedsHuman/SkipStage/RerunGates) honored by orchestrator | TODO | - | - |
| F4.5 | Handoff fact-check — Advisor fact-checks handoffs and human injections against git/log/artifacts; contradictions flagged in prompt | TODO | - | - |

### F5 — Control plane — HTTP+SSE on localhost

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F5.1 | HTTP+SSE localhost control plane — endpoints: state, task graph, session transcript stream, thinking stream, control verbs | DONE | 5370cec | 9 HTTP endpoints on 127.0.0.1:4317 via HttpListener; opt-in (--control-plane); 9 contract tests |
| F5.2 | control.json verbs exposed over HTTP; event stream same as events.jsonl, served live | DONE | 4c3aa00 | POST /control routes through ControlDispatcher; GET /events SSE streams events.jsonl live |
| F5.3 | Headless mode unchanged; curl-level contract tests for all endpoints | DONE | 4c3aa00 | 17 control-plane tests pass; headless unchanged (control plane off by default, bind failure logged non-fatally) |

### F6 — Ink TUI v1 — TypeScript rebuild

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F6.1 | TS+Ink project scaffold + build split — TUI outside dotnet build; engine incremental <10s (D12) | DONE | f3dde7c | face/ dir: TypeScript + Ink TUI, tsup build, vitest tests, --demo flag, separate from dotnet build |
| F6.2 | Plan pane — tree with per-stage state/score/cost, current highlighted, no truncation at 100+ cols | DONE | f3dde7c | PlanTree.tsx — hierarchical rendering, per-stage scores, current stage highlight, 200-col golden test |
| F6.3 | Agent pane — live transcript WITH thinking stream, scrollback+search, tool-call folding | DONE | f3dde7c | AgentPane.tsx — thinking stream folded, tool call folding via /fold, search via /find |
| F6.4 | Process pane + command palette (: or Ctrl+K) + ticker (session/run cost, tokens, wall time, gate cache hits) | DONE | f3dde7c | ProcessPane.tsx — PID/purpose/runtime, command palette (11 verbs), ticker line with cost/tokens/time |
| F6.5 | Golden-layout snapshot tests at 80×24 / 120×30 / 200×50; TUI crash leaves run alive | DONE | f3dde7c | golden.test.tsx — 9 snapshot tests at 3 sizes, TUI is separate OS process, crash never kills engine |

### F7 — Plan import + truth gates + speed program

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F7.1 | Plan import — LLM pass (advisor model) converts mega plan → task graph: stages, sessions, checkpoints, dependencies, truth gates | DONE | c51b7eb | PlanImportService.cs + PlanImportCommand — builds LLM prompt, parses JSON output (stages + gates), adds/merges into plan with interactive confirm table. |
| F7.2 | Re-import diff — mid-plan changes are a first-class operation (diff, not clobber); interactive confirm/edit table | TODO | - | Post-F7.1 — depends on plan import infrastructure. |
| F7.3 | Truth-gate tier — per-stage product-level assertions; per-stage gate selection (docs-only stage runs 0 dotnet gates) | DONE | 294f69a | GateConfig.Tier="truth" + IsTruth + IsFullOrTruth; StageKinds filter on GateConfig; GateRunner filter excludes truth from fast-only batteries; AppliesToStageKind method. 646/647 tests pass. |
| F7.4 | Gate caching by SHA — result = fn(gate, HEAD sha, tier); re-running unchanged battery forbidden by engine, not convention; agents told which gates are already green | DONE | 294f69a | RunDb.GetLastPassingGateResult query; GateRunner.RunTrackedAsync caches green results per (name, tier, sha); Cached property on GateResult; AllRequiredPassed uses IsGreen. 646/647 tests pass. |
| F7.5 | Speed program — solution-filter builds, skipIfFresh attribute, parallel test lanes; target: fast tier ≤60s wall | DONE | 294f69a | GateConfig.SkipIfFresh file-timestamp check in RunOneAsync; Git.MostRecentCommitTime helper; per-stage gate selection by Kind via StageKinds filter. 646/647 tests pass. |

### F8 — conductor chat + Telegram v2

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F8.1 | conductor chat — spawns agent wired (MCP) to run.db+ledger+logs+control verbs: "how did s9 die?", "update task A2", "inject X into retry" | DONE | c51b7eb | ChatCommand.cs — spawns advisor-model agent; McpTaskServer extended with run_query, ledger_list, session_detail, inject_instruction MCP tools; chat.md template in PromptBuilder; wired in Program.cs. |
| F8.2 | Telegram v2 — session-end one-liner with score; NeedsHuman ping with inline buttons [Retry] [Skip] [Inject…] [Chat] | DONE | c51b7eb | PushSessionEndAsync in ITelegramService/TelegramService; NeedsHuman buttons enhanced; RunDb wired into TelegramService via ConductorHost; callback handler for inject:/chat: button actions. |
| F8.3 | Reply-to-inject + /status from run.db + daily digest; host-free (long-poll getUpdates, works behind NAT) | DONE | c51b7eb | /inject command + reply-to-inject flow (pending-injection state); /daily command + 24h automatic digest timer; /chat command; enhanced /status with run.db data (costs, gate failures); EscapeHtml helper. |
| F8.4 | Acceptance — full phone-only drive of a toy run; laptop lid closed | TODO | - | - |

### F9 — Dogfood close — real Shamshir A2 under v-next

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F9.1 | Run real Shamshir stage A2 end-to-end under v-next Foreman | TODO | - | - |
| F9.2 | Fix what bleeds from dogfood run | TODO | - | - |
| F9.3 | Final audit + checklist rated CONFORMS/DEVIATES against design doc | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
