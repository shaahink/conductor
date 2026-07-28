# Conductor Era v3 — Daily Driver + Observability + Pipeline

**Phase State:**
- Baton v2 COMPLETE (77 sessions, 67/67 checkpoints, $3.74)
- 497 tests, build 0w/0e
- 17 open followups (deferred)
- This phase: 14 new sessions across 3 sub-phases

**Design principle:** Highest value first. Daily Driver (your daily pain) → Observability (structured data) → Pipeline (efficiency).

---

## Universal Pre-Session Ritual (≤5 min)

1. Read `CONDUCTOR-ERA3-START.md` handoff block + the FUSION.md stage section for your session.
2. Read `NEXT-ERA.md` for strategic context (first session only).
3. Run **selective** gate:
   - Engine change → `dotnet build Conductor.slnx` + `dotnet test Conductor.slnx --no-build`
   - New CLI command → build + `--help` smoke test
   - Config/docs → relevant command only
   - **Never build on red.**

## Universal Post-Session Ritual (≤10 min)

1. Run gate again — confirm nothing regressed.
2. Produce evidence artifact under `docs/history/era3/evidence/<session>/`.
3. Overwrite handoff block (≤12 lines).
4. Update checkpoint status.
5. Commit (`feat(era3): <item>`). Push.

## Discipline Invariants

- `dotnet build Conductor.slnx` — 0 errors, 0 warnings (warnings=errors)
- `dotnet test Conductor.slnx --no-build` — all pass
- Evidence files exist before claiming DONE
- One commit per session. Never batch.

---

## Phase 1 — Daily Driver (sessions 1-4)

Goal: Fix your most frequent pains. High return, low risk.

### D1 — `conductor status` (LLM-powered status report)

**Goal:** Solve "what's happening?" without external help.

**What to build:**
1. New CLI verb `conductor status` in `Commands.cs`
2. Reads `state.json` + `conductor.log` tail + plan JSON
3. Calls configurable LLM API (deepseek-flash default, `status.model` in plan config)
4. Streams natural-language analysis to TUI + stdout
5. Rate-limited (`statusReport.maxPerHour` in PlanConfig)
6. `--since <time>` mode: only reports delta since last status call

**Files touched:**
- `src/Conductor/Commands/Commands.cs` (new StatusCommand)
- `src/Conductor/Ui/LiveDashboard.cs` (optional status pane/hotkey)
- `src/Conductor/Models/PlanConfig.cs` (status.model, status.maxPerHour)
- `src/Conductor/Core/StatusAgent.cs` (reuse context-building logic)
- `tests/Conductor.Tests/StatusCommandTests.cs`

**Gate:** `conductor status` returns human-readable analysis of current plan state. Cost < $0.01/call. `--since` mode shows only delta.

**Evidence:** `docs/history/era3/evidence/D1/status-output.txt` + gate output

---

### D2 — `conductor gate` (ad-hoc gate re-run)

**Goal:** Re-run battery at HEAD without spawning an agent session.

**What to build:**
1. New CLI verb `conductor gate` in `Commands.cs`
2. Re-runs `PlanConfig.gates` at HEAD
3. Reports results to `conductor.log` + structured log
4. If all green and previously-red: clears `pendingFix`, sets Idle
5. Does NOT spawn an agent session
6. `--full` flag runs full battery (not just fast tier)

**Files touched:**
- `src/Conductor/Commands/Commands.cs` (new GateCommand)
- `src/Conductor/Core/GateRunner.cs` (already exists — expose as API)
- `src/Conductor/Core/Orchestrator.cs` (clearPendingFix method)

**Gate:** `conductor gate` re-runs build + tests at HEAD. Reports PASS/FAIL to log. No agent spawned.

**Evidence:** `docs/history/era3/evidence/D2/gate-output.txt`

---

### D3 — Heartbeat runtime toggle

**Goal:** Control heartbeats without restarting conductor.

**What to build:**
1. CLI: `conductor heartbeat on|off` — writes `control.json` with ToggleHeartbeat action
2. TUI: `H` key toggles heartbeat on/off, footer reflects state
3. Amend strategy: when heartbeat is on, amend the previous heartbeat commit instead of creating a new one (`git commit --amend --no-edit`)
4. Plan JSON on disk is updated so next `conductor run` respects the choice
5. PeriodicTimer replaces Thread.Sleep polling (per BATON-BRIEF.md §250)

**Files touched:**
- `src/Conductor/Commands/Commands.cs` (new HeartbeatCommand)
- `src/Conductor/Core/Progress.cs` (add ToggleHeartbeat to ControlAction)
- `src/Conductor/Core/Orchestrator.cs` (HandleControl, PeriodicTimer)
- `src/Conductor/Core/Reporter.cs` (amend logic)
- `src/Conductor/Ui/LiveDashboard.cs` (H key binding + footer state)
- `src/Conductor/Models/PlanConfig.cs` (Report.HeartbeatMinutes writable)

**Gate:** TUI `H` toggles heartbeat. CLI `conductor heartbeat off` pauses heartbeats mid-session. Amend strategy: simulated 90m session produces ≤2 commits.

**Evidence:** `docs/history/era3/evidence/D3/heartbeat-toggle.txt`

---

### D4 — Mid-session control feedback

**Goal:** Rejected/applied controls produce visible feedback, not silence.

**What to build:**
1. When a control action (retry-stage, rollback, goto, pause-after-stage) is rejected by the guard:
   - Write log message with reason
   - Emit TUI toast (if dashboard running)
   - Write to conductor.log
2. When applied: confirm via log + toast
3. Current state: control.json is consumed even on guard failure — operator thinks it worked

**Files touched:**
- `src/Conductor/Core/Progress.cs` (control feedback channel)
- `src/Conductor/Core/Orchestrator.cs` (emit feedback on guard failure)
- `src/Conductor/Ui/LiveDashboard.cs` (toast renderer)

**Gate:** Rejected control produces log + TUI message. Applied control confirms. No silent failures.

**Evidence:** `docs/history/era3/evidence/D4/control-feedback.txt`

---

## Phase 2 — Observability (sessions 5-8)

### O1 — Structured log + `conductor log --query`

**Goal:** Answer "how many times did the build gate fail?" without grep.

**What to build:**
1. Enable Serilog JSON rolling-file sink: `.conductor/logs/conductor-{Date}.json`
2. Replace all `Log(...)` calls in Orchestrator with `_logger.LogInformation(...)` using structured templates with correlation properties (runId, sessionId, stage, gate)
3. New CLI verb `conductor log --query "stage=P7 and gate=build and outcome=fail"` — filters JSON log
4. Text sink preserved alongside JSON for backward compatibility

**Files touched:**
- `src/Conductor/Core/Orchestrator.cs` (replace Log calls)
- `src/Conductor/Core/Hosting/ConductorHost.cs` (add JSON sink)
- `src/Conductor/Commands/Commands.cs` (new LogCommand)
- Multiple Core files (Log → _logger)

**Gate:** JSON log file has one valid JSON object per line with @t + correlation properties. `conductor log --query` returns matching entries. Text log still works.

**Evidence:** `docs/history/era3/evidence/O1/structured-log.txt`

---

### O2 — Budget intelligence + network health gate

**Goal:** Stop burning attempts on identical stalls. Check network before spawning.

**What to build:**
1. **Identical-stall detection:** If 2 consecutive sessions have `outcome: stalled` AND `newCommits: 0` AND empty `resultSummary`, skip directly to `needsHuman` instead of attempting sessions 3-6.
2. **Exponential backoff:** Double delay between stalled attempts (12→24→48 min).
3. **DNS preflight:** Before spawning agent, `Dns.GetHostEntry("github.com")` + `Dns.GetHostEntry("api.nuget.org")`. If either fails, park with clear message. Recheck every N seconds. Auto-resume when healthy.
4. **Configurable:** `limits.stallPatternTermination` and `limits.healthCheck.dns` in PlanConfig.

**Files touched:**
- `src/Conductor/Core/Orchestrator.cs` (session loop: preflight + stall detection)
- `src/Conductor/Models/PlanConfig.cs` (new limits fields)
- `src/Conductor/Core/AgentSession.cs` (preflight check point)
- `tests/Conductor.Tests/OrchestratorIntegrationTests.cs` (new tests)

**Gate:** Simulated 4× zero-output agent parks at NeedsHuman after 2 instead of 6. Simulated DNS failure parks with clear message. DNS recovery auto-resumes.

**Evidence:** `docs/history/era3/evidence/O2/budget-intelligence.txt`

---

### O3 — Cost overhead split

**Goal:** Distinguish agent cost from gate cost in reports.

**What to build:**
1. Split `TuiMetrics.RunCostUsd` into `agentCost` and `overheadCost`
2. GateRunner reports per-gate cost (duration × estimated compute)
3. REPORT.md shows "Agent: $0.10 | Gates: $0.02 | Total: $0.12"
4. TUI token line shows both

**Files touched:**
- `src/Conductor/Core/GateRunner.cs` (track gate cost)
- `src/Conductor/Ui/DashboardRenderer.cs` (show split)
- `src/Conductor/Core/Reporter.cs` (show split in report)

**Gate:** TUI shows agent vs overhead split. Report shows the same.

**Evidence:** `docs/history/era3/evidence/O3/cost-split.txt`

---

## Phase 3 — Pipeline (sessions 9-14)

### P1 — Dynamic plan reconfiguration

**Goal:** Edit plan at runtime without restart.

**What to build:**
1. `conductor plan set <key> <value>` — hot-update a single field
2. `conductor plan reload` — re-read full plan JSON, validate, apply (next session)
3. `conductor plan add-stage <json>` — append a new stage with checkpoints
4. TUI `E` on stage in PlanTree → inline editor for stage config
5. Plan version bumps on every modification
6. Failed validation rejects with clear error

### P2 — QA parallelization

**Goal:** Shave 20%+ off end-to-end time by running audit + deliver concurrently.

**What to build:**
1. After full battery green, launch audit AND next deliver in parallel
2. Audit runs against pinned commit SHA (read-only, separate branch or worktree)
3. Audit findings inject into running deliver session's prompt
4. If HIGH-severity defect found: gracefully interrupt deliver, queue audit fix first

### P3 — Stronger advisor

**Goal:** Advisor can act, not just diagnose.

**What to build:**
1. `AdvisorVerdict` struct with `Action` enum: BlockRetry, ResetBudget, NeedsHuman, ApplyFix, RerunGates
2. Orchestrator `HandleControl` calls advisor with escalation authority
3. ApplyFix runs a remediating script (e.g., kill stale agent, clean temp files)
4. BlockRetry prevents next attempt until human or condition clears

### P4 — Squash bookkeeping

**Goal:** Clean git history.

**What to build:**
1. On phase confirm: `git rebase -i` squashes all `chore(conductor):` commits between phase start and confirm into one
2. Feature/audit commits (`feat:`, `fix:`, `docs:`) preserved as individual commits
3. Idempotent: re-running confirm doesn't re-squash

### P5 — Post-hoc audit replay

**Goal:** Re-audit completed phases with new knowledge.

**What to build:**
1. `conductor audit <stage> --replay` — runs audit prompt against completed phase
2. Does NOT affect RunState (read-only diagnostic)
3. Output goes to `.conductor/audits/<stage>-replay-<timestamp>.md`

### I1 — MCP task server wiring

**Goal:** Agent gets live task_list/update/add during its session.

**What to build:**
1. `McpTaskServer` exists (B9) but never spawned for real agent sessions
2. Wire into `AgentSession` launch: start MCP server as child process, pass its endpoint to agent via env or args
3. Agent can call `task_list`, `task_update`, `task_add` during its session
4. Task persistence: events survive across sessions via events.jsonl

---

## Quick Commands

```powershell
# Build + test
dotnet build Conductor.slnx
dotnet test Conductor.slnx --no-build

# Run Era v3
C:\Code\conductor\bin\conductor.exe run --plan .conductor\plans\conductor-era3.plan.json
C:\Code\conductor\bin\conductor.exe run --dry-run --plan .conductor\plans\conductor-era3.plan.json
```
