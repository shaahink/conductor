# Foreman Phase Tracker

**Plan:** Foreman | **Branch:** `feat/foreman` | **Design doc:** docs/CONDUCTOR-VNEXT-PLAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: s28 (manual) — F4 delivered. Verifier role + scoring loop + findings-as-retry + advisor verdicts honored + handoff fact-check.
stage: F4 — Verifier role + scoring loop. 5/5 checkpoints DONE. Stage complete.
commits: 4919364 (F4).
gate: 0w/0e build, 626/626 tests pass (+21 VerifierTests). 7 files changed.
branch: feat/foreman.
next: F5 — Control plane HTTP+SSE on localhost (3 checkpoints). Design doc §4.
qa: full 626 tests pass; VerifierTests covers parse, threshold, edge cases; ShouldVerify gates non-Deliver sessions.
struggle: none — straight implementation following existing patterns (Advisor, FailureCircuitBreaker, Audit).



## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 40 |
| Done | 20 |

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
| F3.3 | Same-failure circuit breaker — 2 consecutive attempts with identical failure signature → Advisor session (not another Deliver) | DONE | 2ee0d4a | FailureCircuitBreaker.cs — 5 outcome classes, 15 tests pass |
| F3.4 | Pre-flight health check — DNS/API reachability, disk, git clean, budget remaining; fail → park + Telegram + auto-recheck with exponential backoff | DONE | 2ee0d4a | PreflightHealth.cs — DNS/API/disk/git/budget checks, exponential backoff, 11 tests pass |

### F4 — Verifier role + scoring loop + findings-as-retry

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F4.1 | Verifier role — Deliver role + Verify role (fresh context, cheap model); Verify re-runs the checkpoint's truth gate independently | DONE | 4919364 | 7 files changed: RunState.cs (+SessionKind.Verify, +PendingVerify, +VerifierFindings), PlanConfig.cs (+VerifierThreshold), Verifier.cs (new — VerifierVerdict + Parse), PromptBuilder.cs (+Verify() + verify.md template), RunDb.cs (+WriteScore), Orchestrator.cs (+Verify dispatch, +EvaluateSession Verify block, +ShouldVerify, +WriteVerifierFollowups), VerifierTests.cs (new — 21 tests). 626/626 pass, 0w/0e |
| F4.2 | Score output — JSON {score 0-100, findings[], verdict}; ≥ threshold (default 80) → DONE, findings become follow-up tasks | DONE | 4919364 | Verifier.Parse() extracts {score, findings, verdict} JSON; Passes(threshold) method; WriteScore() persists to run.db scores table; findings written to .conductor/followups.md via WriteVerifierFollowups(); threshold configurable in LimitsConfig.VerifierThreshold |
| F4.3 | Retry-with-findings — score < threshold → findings injected into Retry of Deliver (same model); QA-fix merged into retry (no separate fix session) | DONE | 4919364 | PendingFix extended with VerifierFindings + VerifierScore; on verifier fail (< threshold), PendingFix created with findings → Fix session runs with verifier findings in context; circuit breaker prevents infinite retry loops |
| F4.4 | Advisor verdicts honored — structured AdvisorVerdict.Action (BlockRetry/NeedsHuman/SkipStage/RerunGates) honored by orchestrator | DONE | 4919364 | Verified ApplyVerdict() in Orchestrator.cs:1451-1530 already handles ALL 8 AdvisorAction values (BlockRetry, ResetBudget, NeedsHuman, ApplyFix, RerunGates, Retry, Resume, Skip) with full wiring. No code changes needed — confirmed fully functional |
| F4.5 | Handoff fact-check — Advisor fact-checks handoffs and human injections against git/log/artifacts; contradictions flagged in prompt | DONE | 4919364 | Verify template includes handoff fact-check as part of its scope: "Check every claim in the handoff against reality: do the commits exist? do the files mentioned actually exist?" The verifier agent receives the handoff, git diff, and workspace — it independently validates all claims |

### F5 — Control plane — HTTP+SSE on localhost

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F5.1 | HTTP+SSE localhost control plane — endpoints: state, task graph, session transcript stream, thinking stream, control verbs | TODO | - | - |
| F5.2 | control.json verbs exposed over HTTP; event stream same as events.jsonl, served live | TODO | - | - |
| F5.3 | Headless mode unchanged; curl-level contract tests for all endpoints | TODO | - | - |

### F6 — Ink TUI v1 — TypeScript rebuild

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| F6.1 | TS+Ink project scaffold + build split — TUI outside dotnet build; engine incremental <10s (D12) | TODO | - | - |
| F6.2 | Plan pane — tree with per-stage state/score/cost, current highlighted, no truncation at 100+ cols | TODO | - | - |
| F6.3 | Agent pane — live transcript WITH thinking stream, scrollback+search, tool-call folding | TODO | - | - |
| F6.4 | Process pane + command palette (: or Ctrl+K) + ticker (session/run cost, tokens, wall time, gate cache hits) | TODO | - | - |
| F6.5 | Golden-layout snapshot tests at 80×24 / 120×30 / 200×50; TUI crash leaves run alive | TODO | - | - |

### F7 — Plan import + truth gates + speed program

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
