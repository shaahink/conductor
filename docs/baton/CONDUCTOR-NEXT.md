# Conductor Next — Ideas for the post-Baton self-improvement cycle

**Generated:** 2026-07-08 from 60+ live sessions across Loom, Baton, and Shamshir.
**This document feeds the NEXT plan** — it is NOT the current Baton iteration (B0-B12).
These are observations, patterns, and feature ideas that are either too large for the current cycle or cross-cutting enough to warrant dedicated stages.

---

## 1. QA parallelization — audit while delivering next stage

### Observation
Currently the conductor is strictly sequential: deliver → full-battery → audit → confirm → advance.
An audit session runs against a *completed* phase while the next phase waits idle. Across the 3 projects, audit sessions averaged 15-25 minutes (Baton: 8-20 min, Loom: 14-32 min, Shamshir: 23 min). That's dead air in the pipeline.

### Proposal
After a phase's full battery goes green and the audit session launches, immediately start the NEXT phase's first deliver session in parallel. The audit runs against the committed HEAD (which won't change during delivery — the deliver session works on its own branch or commits to the next phase's tree). If the audit finds a defect, it queues a followup fix for the next deliver session to pick up. No rollback needed — the next session already has the audit findings in its context.

### Design constraints
- Audit must run against a **pinned commit** (the phase's confirm-commit, not HEAD)
- Deliver session workspace must be independent (different branch or clean tree)
- Audit findings are injected into the in-progress deliver session's prompt as "previous-phase audit: N defects found, see `.conductor/handovers/L<N>.md` for the first action"
- If audit finds a HIGH-severity defect that fundamentally undermines the phase: the deliver session is gracefully interrupted with the audit fix queued first
- Gate: measured end-to-end time reduction ≥ 20% vs sequential on a 4-phase project

---

## 2. Stronger advisor — decision-making, not just diagnosis

### Observation
The advisor correctly diagnosed the B4 DNS stall as "environmental, not architectural" and "repeated retries without human diagnosis are wasteful." But it had **no power to act** — it could only return a verdict string that the orchestrator printed to the log. The orchestrator then burned all 6 attempts anyway.

### Proposal: Advisor with escalation authority
| Adivsor capability | Current | Proposed |
|---|---|---|
| Diagnose cause | Yes (returns string) | Yes + structured `AdvisorVerdict` with severity |
| Block retry | No | Yes — `AdvisorVerdict.Action = BlockRetry` prevents next attempt |
| Reset budget | No | Yes — `AdvisorVerdict.Action = ResetBudget` resets attempt counter |
| Escalate to human | Yes (indirect) | Yes — `Verdict.Action = NeedsHuman(reason, suggestedFix)` |
| Auto-apply fix | No | Yes — `Verdict.Action = ApplyFix(script)` runs a remediating script |
| Re-run gate | No | Yes — `Verdict.Action = RerunGates` re-runs the battery without a session |

Example: DNS outage scenario
- Current: advisor says "environmental, don't retry" → orchestrator ignores and retries 6x
- Proposed: advisor returns `{ action: BlockRetry, autoFix: "ping github.com", retryAfter: "network up" }` → orchestrator pauses, pings every 60s, auto-resumes when DNS resolves

### Design
- `AdvisorConfig` in PlanConfig: `mode: advisory | gating | autonomous`
- `AdvisorVerdict` struct: `Action`, `AutoFixScript`, `RetryAfterCondition`, `Reason`
- Orchestrator `HandleControl` integration: advisor call is a control action source (like TUI or control.json)
- Gate: advisor blocks retry on known-environmental failures; advisor auto-fix script runs and resolves a simulated DNS outage

---

## 3. Heartbeat runtime toggle + clean history

### Observation
Heartbeat commits (F-4) constituted 93% of Loom's git history and 95% of Shamshir's. The plan JSON's `heartbeatMinutes` controls this but is read once at startup — no runtime toggle. `heartbeatMinutes: 0` had to be manually edited into 3 plan JSONs to temporarily suppress noise.

### Proposal: Runtime toggle + amend strategy
1. **Runtime toggle:** TUI keybinding (`H`) + CLI `conductor heartbeat on|off` → updates in-memory `PlanConfig` AND writes to plan JSON so next run respects it. (`ControlAction.ToggleHeartbeat`)
2. **Amend instead of new commits:** When `heartbeatMinutes > 0`, heartbeat commits amend the previous heartbeat commit instead of creating a new one: `git commit --amend --no-edit` (requires `--force-with-lease` on next push, which is safe since only the conductor writes REPORT.md). This keeps the branch at 1 heartbeat commit per session, not N.
3. **Report ref:** Alternative — push heartbeats to a dedicated `refs/reports/<branch>` ref so the feature branch stays clean entirely.

### Implementation notes
- PeriodicTimer per BATON-BRIEF.md §250 (replaces `Thread.Sleep(400)` polling)
- Amend mode: `git log --oneline -1 --format=%s` → if previous commit is a heartbeat → `git commit --amend`
- Gate: simulated 90-minute session produces ≤ 2 commits on feature branch (1 initial + 1 amended heartbeat)

---

## 4. DNS/network health gate

### Observation
When the DNS outage hit, the conductor spawned 13 sessions (6 Baton + 3 Loom gate-red + 4 advisor-timeouts) against an unreachable network. Each session burned 12 minutes in the stall timer. The agent binary never produced a single line of output, but the conductor couldn't distinguish "agent slow" from "agent can't reach its API."

### Proposal: Pre-flight health check
Before spawning an agent session:
1. **DNS check:** `Dns.GetHostEntry("api.nuget.org")` and `Dns.GetHostEntry("github.com")` — both must resolve. Fail → log + park + set NeedsHuman with clear message.
2. **API liveness check:** If configured, ping the agent's API endpoint (e.g., opencode's health endpoint).
3. **Configurable:** `healthCheck.dns` and `healthCheck.apiEndpoint` in PlanConfig.
4. **Recheck interval:** If checks fail, recheck every N seconds (configurable). Auto-resume when checks pass.

### Gate
- Simulated DNS failure: no session spawned, conductor parks cleanly, log says "DNS: api.nuget.org unreachable"
- DNS recovers mid-park: conductor detects on next recheck and auto-resumes
- No attempt counter increment for pre-flight failures

---

## 5. Structured conductor log

### Observation
`conductor.log` is line-oriented unstructured text. The TUI has structured data but the log (the replayable record) is:
```
[10:27:54] gate build: FAIL (exit 1) in 87s
[10:27:55] session #24 GatesRed — queuing fix session (attempt 1/4)
```
There's no structured timeline, no correlation between gate results and session outcomes, and no way to query "how many times did the build gate fail across all sessions?"

### Proposal: Serilog structured sink (already wired in B2!)
B2.5 added Serilog structured logging with correlation scopes (`runId`, `sessionId`, `stage`, `gate`). The next step:
1. Replace ALL `Log(...)` lines in Orchestrator with `_logger.LogInformation(...)` using Serilog's structured templates
2. Write a JSON rolling-file sink: `.conductor/logs/conductor-{Date}.json` with one JSON line per event
3. Gate results, session outcomes, and heartbeat events are already `ConductorEvent` types — serialize them directly
4. Build a `conductor log --query "stage=L3 and gate=build and outcome=fail"` command for querying

### Gate
- All current `Log(...)` calls routed through ILogger with structured templates
- JSON log file contains one valid JSON object per line, each with `@t` timestamp and correlation properties
- Existing `conductor.log` text format preserved as optional secondary sink

---

## 6. Git history deduplication via squash-on-confirm

### Observation
Even without heartbeats, the conductor produces:
- `chore(conductor): sN Stage working ▸CP @ HH:MM` ~6-8 per session
- `chore(conductor): sN Stage Advanced — Idle` at session boundaries
- `chore(conductor): sN Stage Stalled — NeedsHuman` at stalls

A 4-session phase produces ~30 conductor commits for ~4 real commits. The human developer reading `git log` must `--grep` filter to find anything.

### Proposal: `--squash-bookkeeping` mode
When enabled:
1. Conductor still commits heartbeat/session-boundary bookkeeping commits as normal (they serve as save points)
2. On phase confirm (full battery green + audit done), the conductor `git rebase -i` squashes all `chore(conductor):` commits between the phase's start commit and confirm commit into a single `chore(conductor): B<N> confirmed + audited` commit
3. Feature/audit commits (`feat(...):`, `fix(...):`, `docs(...):`) are preserved as individual commits

### Gate
- Phase with 30 conductor commits + 4 real commits → after confirm: 5 commits (4 real + 1 squash)
- Squash is idempotent (re-running confirm doesn't re-squash)
- Force-push only after squash (documented as the trade-off)

---

## 7. Ad-hoc gate re-run without session

### Observation
Loom's s24 gate-red was 100% infra (DNS). The fix? Re-run `dotnet build` once DNS was back. That took 87 seconds. Instead, the conductor wanted to spawn a fix-session — which would read the tracker, run the gate battery, commit the evidence, and take 15+ minutes.

### Proposal: `conductor gate` command
A CLI verb that:
1. Runs the current phase's gate battery at HEAD
2. Reports results to `conductor.log` + structured log
3. If all gates green and previously-red: updates state (clears pendingFix, sets Idle)
4. Does NOT spawn an agent session — purely a gate re-run
5. Optional: `--full` runs the full battery (not just fast gates)

### Use cases
- DNS false-red resolution (Loom s24)
- "I just fixed something manually, prove it didn't break gates"
- Pre-push sanity check without a full conductor session

---

## 8. Attempt-budget intelligence

### Observation
6 consecutive stalls with 0 output lines (Baton B4) burned the full attempt budget. The advisor correctly diagnosed but couldn't stop the burn. The general pattern is: if session N+1 has the same zero-output outcome as session N, session N+2 will too.

### Proposal
Exponential backoff or pattern-detection on attempts:
- **Identical-outcome early termination:** If 2 consecutive sessions both have `outcome: stalled` AND `newCommits: 0` AND `resultSummary: ""`, skip directly to `needsHuman` instead of attempting sessions 3-6.
- **Exponential backoff:** If the same session keeps stalling, double the delay between attempts (12 min → 24 min → 48 min) to give environmental conditions time to clear.
- **Configurable:** `limits.stallPatternTermination` and `limits.backoffFactor` in PlanConfig.

### Gate
- Simulated 4x zero-output agent: parks at NeedsHuman after 2 consecutive instead of 6
- Simulated 4x agent-slow-but-producing (output after 5 min): backoff applies but doesn't terminate early

---

## 9. Per-session overhead accounting

### Observation
Gate batteries dominate session time. Across the 3 projects:
- Loom: 3-5 minutes per build gate, 3 minutes for tests, 2 minutes for mcp-qa, 1 minute for pnpm check = ~9-11 min overhead per session
- Baton: 10-18 seconds build, 7-9 seconds tests = ~20-28 seconds overhead per session
- Shamshir: 42-51 seconds build, 75-81 seconds sim-fast, 75 seconds unit = ~3-4 min overhead per session

For short sessions (Baton B0 audit: 8 min), overhead is ~4% of session time. For Loom's build gate (87s on failure), a FAIL gates the session but the cost framework doesn't distinguish "agent work" from "gate overhead."

### Proposal
Split cost/token tracking into `sessionCost` (agent work only) and `overheadCost` (gate battery, setup, teardown). The TUI already shows per-gate timing; it should report a summary: "Agent 45 min | Gates 8 min | Cost $0.15 + $0.00 overhead."

---

## 10. Cross-project followup registry

### Observation
Followups are per-repo in `.conductor/followups.md`. Across 3 projects there are 25+ open followups with no central tracking. The B3 audit noted FU-B2-3 was "carried in, owned by B3" but "NOT addressed." Nobody but a human reading handovers would know.

### Proposal
A shared `~/.conductor/followups.db` (SQLite) or `~/.conductor/followups.json` that:
- Every audit session writes to
- Cross-references: `project`, `stage`, `severity`, `status`, `rehomedFrom`, `rehomedTo`, `createdSession`, `handoverFile`
- The TUI plan tree can show "this stage has 2 open followups from previous phases"
- The Baton self-plan's prompt builder can inject "Open cross-project followups affecting your stage" into the session context

---

## Summary — proposed stages for next plan

| Stage | Features |
|---|---|
| N1 | QA parallelization — audit runs concurrently with next deliver |
| N2 | Stronger advisor — structured verdicts, block-retry, auto-fix scripts |
| N3 | DNS/network health gate + attempt-budget intelligence |
| N4 | Heartbeat runtime toggle + amend strategy + PeriodicTimer migration |
| N5 | Structured conductor log (JSON sink) + `conductor log --query` |
| N6 | Git history squash-on-confirm + `conductor gate` command |
| N7 | Cross-project followup registry + overhead accounting |
| N8 | Dynamic plan reconfiguration at runtime |
| N9 | On-demand status report via configurable LLM (deepseek-flash) |
| N10 | Post-hoc audit + cost-iteration tracking + live plan via prompting |

Each stage gates independently. N1-N3 are the highest-impact (they address the actual failures observed across 3 live runs). N4-N7 are quality-of-life improvements driven by audit observations. N8-N10 are structural capabilities.

---

## 11. Dynamic plan reconfiguration at runtime

### Observation
When `heartbeatMinutes` needed to change, it took a manual edit to 3 plan JSONs and a restart of the conductor. Plan config fields (limits, gates, stage order, heartbeat) are read once at startup into `PlanConfig` and never re-read. There's no way to hot-reload a plan while a run is in progress.

### Proposal: `conductor plan` command + runtime re-read
1. **`conductor plan` CLI verb:** reads, validates, and applies a plan JSON at runtime.
   - `conductor plan set limits.maxRunCostUsd 5.00` — hot-update a single field
   - `conductor plan reload` — re-read the full plan JSON, validate, apply
   - `conductor plan add-stage <json>` — append a new stage (with checkpoints) to the in-memory plan
   - `conductor plan update-stage <id> --owner-gate` — toggle an existing stage's owner-gate flag
2. **TUI plan editor:** pressing `E` on a stage in the plan tree opens an inline editor for stage-level config (owner-gate, max attempts, heartbeat). Pressing `+` adds a new stage with prompted checkpoint rows.
3. **prompt-builder integration:** when a plan is updated mid-run, the next session's prompt includes a `PLAN MODIFIED` section showing the delta (what changed, when, by whom).

### Design constraints
- Plan changes during a session take effect on the NEXT session (the current session is not interrupted)
- Plan validation runs on every change — invalid config is rejected with a clear error
- Plan version bumps on every modification for audit trail
- `PlanConfig` events (`PlanModified`) emitted for the timeline

### Gate
- `conductor plan reload` mid-run updates in-memory plan; next session reflects the change
- Invalid plan modification is rejected with structured error
- Plan update events appear in the event log
- Running session is NOT disrupted by plan change

---

## 12. On-demand status report via configurable LLM

### Observation
When you wanted a status check on 3 running plans, you asked me (an LLM) to read state.json and conductor.log files and synthesize a report. The conductor's own REPORT.md is a static template — it lists checkpoint rows and commit hashes but doesn't *analyze*. There's no "what's the current situation?" command.

### Proposal: `conductor status` command with pluggable LLM
1. **`conductor status`** — reads `state.json` + `conductor.log` + the event log, constructs a prompt, calls a configurable LLM API, and streams the result to the TUI.
2. **Configurable model:** `PlanConfig.StatusReport.Model` — default `deepseek-chat` (fast/cheap), can be `claude-sonnet-4-20250514` for deeper analysis, or `local` for offline.
3. **Prompt template:** includes the current RunState JSON, the last N log lines, gate results, cost summary, and the plan tree. The LLM returns a structured summary: what's healthy, what's stuck, what needs attention.
4. **On-demand via TUI:** pressing `G` (already bound to "status agent probe" but currently shallow) triggers a full `conductor status` LLM call. The thinking pane shows the analysis.
5. **Cost-controlled:** status reports are a single LLM call (~2000 tokens in, ~500 tokens out = ~$0.002 with deepseek-flash). Configurable rate limit: `statusReport.maxPerHour`.
6. **Diff mode:** `conductor status --since 1h` — only reports what changed since the last status call (reads the previous status output, diffs it).

### Gate
- `conductor status` against a running plan returns a human-readable analysis with: health assessment, active stage, stuck checkpoints, cost burn rate, time remaining estimate
- Status report costs < $0.01 per call
- `--since` mode correctly identifies only recent delta
- Works against a stopped (paused/needsHuman) plan as well as a running one

---

## 13. Post-hoc audit + cost-iteration tracking

### Observation
The conductor audits each phase immediately after it completes. There is no mechanism to re-audit a phase later ("audit later fully"), no way to track how much a specific iteration cost relative to previous iterations of the same stage, and no way to compare agent performance across iterations.

### Proposal: full audit replay + iteration ledger
1. **`conductor audit <stage> --replay`** — re-runs the audit prompt against a completed phase at any time. Useful when: the initial audit was shallow (time-boxed), new findings emerged in later phases, or you want a second opinion. The replay audit runs as a normal session (spawns agent, gates independently) but does NOT advance or confirm anything — it's purely diagnostic, output goes to `.conductor/audits/<stage>-replay-<timestamp>.md`.
2. **Iteration cost ledger:** `RunState` already tracks per-session cost/tokens. Add `IterationLedger` that groups sessions by `(stage, checkpoint)` and shows:
   - Attempt count per checkpoint
   - Total cost per checkpoint (agent + gates)
   - Tokens-in per effective commit (efficiency ratio)
   - Time-to-done per checkpoint
3. **TUI iteration view:** pressing `C` (cost) on a checkpoint row shows the cost ledger — "P0.3 cost $0.11 across 143 turns, P0.2 cost $0.11 across 143 turns" etc. Identifies expensive checkpoints for plan optimization.
4. **Regression detection:** if re-running the gate battery on a previously-green phase shows a new failure, flag it as a regression (not a "new" failure) with the original green commit hash for bisection.

### Gate
- `conductor audit L3 --replay` produces a diagnostic audit report without affecting RunState
- Iteration ledger groups sessions correctly by stage+checkpoint
- Cost-efficiency ratio is visible in TUI per checkpoint
- Regression: stale-green detection triggers a distinct log event (not a generic gate failure)

---

## 14. Live plan via prompting

### Observation
Conductor plans are JSON files authored by hand or via `conductor new-plan --template`. The plan is static — it doesn't adapt to discovered work, newly surfaced debt, or changing priorities. When the cross-project audit found 22 bugs across 3 repos, someone had to manually write DEBT.md files and stage them into the plan.

### Proposal: `conductor plan evolve --prompt "..."` 
1. **AI-assisted plan evolution:** you describe what changed ("Loom L0-L4 audits found 8 unresolved checkpoints, here are the files, group them into stages") and the conductor calls the configured LLM to generate stage entries (with checkpoints, gates, and read-order) that you review and approve.
2. **Debt ingestion:** `conductor plan ingest-debt <debt-file.md>` — reads a DEBT.md file, extracts the structured checkpoint sections (using the same conventions-as-config parser from B1.4), and proposes plan stage additions. You approve/reject/edit each.
3. **Checkpoint auto-split:** if a proposed stage has too many checkpoints (> N), the LLM suggests splitting into sub-stages.
4. **Read-order inference:** the LLM reads the existing plan's read-order and the new stage's docs directory to suggest where in the read-order chain the new stage's docs should slot in.
5. **Plan diff + review:** the proposed plan changes are shown as a diff. You accept, edit, or reject. Accepted changes are written to the plan JSON and the tracker.

### Gate
- `conductor plan evolve --prompt "add stages for Loom L0-L4 deferred bugs from conductor-DEBT.md"` produces valid, parseable stage entries with checkpoints
- `conductor plan ingest-debt conductor-DEBT.md` extracts all sections with `## Stage` headers and proposes plan entries
- The resulting plan passes `PlanConfig.Validate()` (repo, tracker, agent, prompt sections are coherent)
- Human review step: plan diff is shown before writing

---

## Summary — revised stages for next plan

| Stage | Features |
|---|---|
| N1 | QA parallelization — audit runs concurrently with next deliver |
| N2 | Stronger advisor — structured verdicts, block-retry, auto-fix scripts |
| N3 | DNS/network health gate + attempt-budget intelligence |
| N4 | Heartbeat runtime toggle + amend strategy + PeriodicTimer migration |
| N5 | Structured conductor log (JSON sink) + `conductor log --query` |
| N6 | Git history squash-on-confirm + `conductor gate` command |
| N7 | Cross-project followup registry + overhead accounting |
| N8 | Dynamic plan reconfiguration at runtime (`conductor plan reload/set/add-stage`) |
| N9 | On-demand status report via configurable LLM (`conductor status`, deepseek-flash default) |
| N10 | Post-hoc audit replay + cost-iteration ledger + live plan via prompting (`conductor plan evolve`) |

N1-N3 are the highest-impact (they address the actual failures observed across 3 live runs).
N4-N7 are quality-of-life improvements driven by audit observations.
N8-N10 are structural capabilities that make the conductor self-modifying and introspectable.
