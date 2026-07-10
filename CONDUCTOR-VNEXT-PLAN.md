# Conductor v-Next — "The Foreman" Phase Tracker

**Read order:** this file → `docs/CONDUCTOR-VNEXT-PLAN.md` (design authority, MANDATORY) →
your stage deliverable from the plan JSON.
**Branch:** `feat/foreman` (to be created from current). **Driver:** `C:\Code\conductor\bin\conductor.exe` (stable from master).
**Design doc:** `docs/CONDUCTOR-VNEXT-PLAN.md` — 10 cataloged failures, architecture, locked decisions, addenda D7-D12.

## Handoff (overwrite this block, ≤12 lines, no history)
last: s9 — F1 third audit complete. Stage HARDENED and FINISHED. No new bugs found; prior fixes verified.
stage: F1 — run.db task store + tracker-as-view + task/note verbs + report --query. GENUINELY CORRECT.
commits: 542422c (audit r2 fixes), 58f3ba4 (audit r1 fixes), 1c8c888 (F1.2-4), 6330c60 (F1.1). Total F0: 9, F1: 4 (2 delivery + 2 audit).
gate: 0w/0e, 548/548 tests pass. Full static audit clean — no correctness bugs, no race conditions, no resource leaks, no TODOs. 6 deferred items tracked in followups.md (FU-F1-01 through -03, D4-D6).
trap: HarnessTests creates temp git repos — ensure git is on PATH. 1 pre-existing flaky Serilog file-lock test (HostLoggingTests) — passes in isolation.
branch: feat/foreman.

## Baseline numbers (pre-Foreman, re-measure at each phase)

| Metric | Value |
|---|---|
| Target framework | net10.0 |
| Tests | 548 pass (0 warn, 0 err) |
| Source files | ~45 .cs under src/Conductor |
| Branches | master (stable), feat/foreman |
| Versions | Conductor v2 (Baton) + Era v3 enhancements; 91 sessions, ~$4.78 total |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Scope changes get a `> scope change:` line under the row —
never silent renumbering.

### F0 — Foundations (kill list, async engine, integration harness)

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F0.1 | Kill list executed — delete replay/time-travel, persona bloat (keep 3 roles), confidence pane, heartbeat commits to feature branch, hierarchical template system | DONE | 47798ee | 531 tests pass, 0w/0e, 23 files changed, -687 net LOC across 5 commits |
| F0.2 | Async control loop — Orchestrator run loop async (B4.7 debt); no blocking .Result/.Wait() | DONE | 09dc2ec | 533 tests pass, 0w/0e, 9 private methods converted to async, 6 Thread.Sleep→Task.Delay, 3 .GetAwaiter()/.Result→await |
| F0.3 | Integration harness — fake agent + temp repo, full cycle asserted (B4.8); gate: 0w/0e, harness cycle green | DONE | b6e5d8b | HarnessTests.cs — 2 tests (full cycle + dry-run), fake cmd agent writes opencode JSON, 533/533 pass |

### F1 — SQLite run.db task store + tracker-as-view

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F1.1 | run.db schema — tables: runs, stages, sessions, attempts, gates, scores, ledger, handovers, injections, costs; telemetry per D8 | DONE | 6330c60 | RunDbTests.cs — 12 tests pass, schema auto-creates (idempotent), session/gate/cost round-trip, parameterised query, 11 tables |
| F1.2 | Tracker-as-view — conductor writes TRACKER.md FROM run.db (generated view for humans/agents); regenerates byte-stable | DONE | 1c8c888 | TrackerGenerator.cs — generates TRACKER.md from run.db checkpoints table; idempotent seed; wired in Orchestrator at InitializeRun + EmitSessionFinished + handover write; 15 RunDbTests pass including 3 new checkpoint tests |
| F1.3 | conductor task/note verbs — task CRUD + note (writes ledger); MCP surface; agents report progress via verbs instead of hand-editing markdown | DONE | 1c8c888 | NoteCommand + TaskCommand CLI verbs; McpTaskServer conductor_note tool; McpServeCommand wires RunDb; 548/548 tests pass |
| F1.4 | conductor report --query — ad-hoc SQL/DSL against run.db ("cost of stage R3?", "which gates fail most?") | DONE | 1c8c888 | ReportCommand --query <SQL> option; runs parameterised SQL against run.db; renders results as Spectre table |

### F2 — Process ownership (supervisor, orphans, bg primitives)

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F2.1 | ProcessSupervisor + Job Objects — every child spawned into Windows Job Object; kill-by-tree, no orphans | TODO | - | - |
| F2.2 | PID registry in run.db + orphan reaper at startup | TODO | - | - |
| F2.3 | conductor bg start / status / logs / stop — sanctioned background-run primitive; prompts mandate it for anything >3 min | TODO | - | - |
| F2.4 | MCP bg surface + harness proof — kill-by-tree, orphan reap, bg liveness feeds stall detector | TODO | - | - |

### F3 — Stall v2 + resilience

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F3.1 | Stall detection v2 — watches (a) agent stdout, (b) tool-call events from JSON stream, (c) liveness of supervised bg children | TODO | - | - |
| F3.2 | Soft-kill debrief — on stall: inject "wrap up, write ledger + handoff, 3 min grace", kill only after grace window | TODO | - | - |
| F3.3 | Same-failure circuit breaker — 2 consecutive attempts with identical failure signature → Advisor session (not another Deliver) | TODO | - | - |
| F3.4 | Pre-flight health check — DNS/API reachability, disk, git clean, budget remaining; fail → park + Telegram + auto-recheck with exponential backoff | TODO | - | - |

### F4 — Verifier + scoring loop

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F4.1 | Verifier role — Deliver role + Verify role (fresh context, cheap model); Verify re-runs the checkpoint's truth gate independently | TODO | - | - |
| F4.2 | Score output — JSON {score 0-100, findings[], verdict}; ≥ threshold (default 80) → DONE, findings become follow-up tasks | TODO | - | - |
| F4.3 | Retry-with-findings — score < threshold → findings injected into Retry of Deliver (same model); QA-fix merged into retry (no separate fix session) | TODO | - | - |
| F4.4 | Advisor verdicts honored — structured AdvisorVerdict.Action (BlockRetry/NeedsHuman/SkipStage/RerunGates) honored by orchestrator | TODO | - | - |
| F4.5 | Handoff fact-check — Advisor fact-checks handoffs and human injections against git/log/artifacts; contradictions flagged in prompt | TODO | - | - |

### F5 — Control plane (HTTP+SSE)

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F5.1 | HTTP+SSE localhost control plane — endpoints: state, task graph, session transcript stream, thinking stream, control verbs | TODO | - | - |
| F5.2 | control.json verbs exposed over HTTP; event stream same as events.jsonl, served live | TODO | - | - |
| F5.3 | Headless mode unchanged; curl-level contract tests for all endpoints | TODO | - | - |

### F6 — Ink TUI v1 (TypeScript)

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F6.1 | TS+Ink project scaffold + build split — TUI outside dotnet build; engine incremental <10s (D12) | TODO | - | - |
| F6.2 | Plan pane — tree with per-stage state/score/cost, current highlighted, no truncation at 100+ cols | TODO | - | - |
| F6.3 | Agent pane — live transcript WITH thinking stream, scrollback+search, tool-call folding | TODO | - | - |
| F6.4 | Process pane + command palette (: or Ctrl+K) + ticker (session/run cost, tokens, wall time, gate cache hits) | TODO | - | - |
| F6.5 | Golden-layout snapshot tests at 80×24 / 120×30 / 200×50; TUI crash leaves run alive | TODO | - | - |

### F7 — Plan import + truth gates + speed

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F7.1 | Plan import — LLM pass (advisor model) converts mega plan → task graph: stages, sessions, checkpoints, dependencies, truth gates | TODO | - | - |
| F7.2 | Re-import diff — mid-plan changes are a first-class operation (diff, not clobber); interactive confirm/edit table | TODO | - | - |
| F7.3 | Truth-gate tier — per-stage product-level assertions; per-stage gate selection (docs-only stage runs 0 dotnet gates) | TODO | - | - |
| F7.4 | Gate caching by SHA — result = fn(gate, HEAD sha, tier); re-running unchanged battery forbidden by engine, not convention; agents told which gates are already green | TODO | - | - |
| F7.5 | Speed program — solution-filter builds, skipIfFresh attribute, parallel test lanes; target: fast tier ≤60s wall | TODO | - | - |

### F8 — conductor chat + Telegram v2

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F8.1 | conductor chat — spawns agent wired (MCP) to run.db+ledger+logs+control verbs: "how did s9 die?", "update task A2", "inject X into retry" | TODO | - | - |
| F8.2 | Telegram v2 — session-end one-liner with score; NeedsHuman ping with inline buttons [Retry] [Skip] [Inject…] [Chat] | TODO | - | - |
| F8.3 | Reply-to-inject + /status from run.db + daily digest; host-free (long-poll getUpdates, works behind NAT) | TODO | - | - |
| F8.4 | Acceptance — full phone-only drive of a toy run; laptop lid closed | TODO | - | - |

### F9 — Dogfood close

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F9.1 | Run real Shamshir stage A2 end-to-end under v-next Foreman | TODO | - | - |
| F9.2 | Fix what bleeds from dogfood run | TODO | - | - |
| F9.3 | Final audit + checklist rated CONFORMS/DEVIATES against design doc | TODO | - | - |

## Dependencies

```
F0 → F1 → (F2, F3) → F4
F1 → F7
F5 → F6
F5 → F8
F9 last (requires F0–F8 complete)
```

F2/F3 and F5 can run as parallel lanes if run is healthy.

## Quick commands

```powershell
# build + test (from the worktree)
dotnet build Conductor.slnx
dotnet test  Conductor.slnx

# dry-run the foreman plan with the STABLE driver
C:\Code\conductor\bin\conductor.exe run --dry-run -p plans\conductor-foreman.plan.json
# one supervised session
C:\Code\conductor\bin\conductor.exe run --once   -p plans\conductor-foreman.plan.json
# full run
C:\Code\conductor\bin\conductor.exe run          -p plans\conductor-foreman.plan.json
```
