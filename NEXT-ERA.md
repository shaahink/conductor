# Conductor Era v3 — Strategic Roadmap

**Written:** 2026-07-09 after Baton v2 completion (77 sessions, 67/67 checkpoints)
**Base state:** Conductor v2 (Baton) fully delivered. `master` (stable binary), `feat/baton` (v2 worktree), `feat/era-v3` (live plan running).
**Live plan:** `.conductor/plans/conductor-era3.plan.json` — 14 stages. D1 running now.
**Scope:** Conductor-only improvements. Loom and Shamshir are delivered projects; they inform these gaps but are not in scope.

---

## Current Inventory — What Conductor v2 Has

```
IAgentProvider (opencode/claude/generic)  │  IProgressProvider (markdown/script/plan)
Event-sourced backbone (events.jsonl)     │  Host/DI/Options/Serilog
RunState projection + crash recovery      │  Owner-gates, budget caps, Ctrl+C
Alt-screen TUI (Spectre Layout)           │  Hierarchical plan tree
Severity model, thinking pane             │  Command filters, live tokens
Timeline, replay/time-travel              │  AI-health metrics, confidence
Telegram (two-way inline keyboard)        │  9 personas (planner/reviewer/...)
Task graph + MCP server (B9)              │  Soft-break + hard rollover
dependsOn graph + hooks + battery collapse│  Cross-platform gates
Tier A analysis lanes + Tier B merge gate │  Fix-lanes consuming followups
Async ratchet (MA0045/MA0002 at error)    │  Integration harness
497 tests, build 0w/0e                    │  17 open followups (deferred)
```

---

## Gap Analysis — Conductor-Only

### Daily Driver (highest pain)

| # | Gap | Observed impact | Fix |
|---|-----|----------------|-----|
| 1 | **No `conductor status`** — you ask "what's happening?" and must manually read state.json + log | You had to ask an external LLM to synthesize status. Conductor should answer directly. | `conductor status` CLI verb calls configurable LLM (deepseek-flash default, ~$0.002/call) with `state.json` + log tail → streams analysis to TUI. |
| 2 | **No dynamic plan reconfiguration** — edit plan JSON → restart conductor to pick it up | `heartbeatMinutes: 0` needed manual edit to plan files. `conductor plan reload` doesn't exist. | `conductor plan set/get/reload/add-stage`. TUI plan tree editor (`E` on stage). Next session picks up changes, never mid-session. |
| 3 | **No `conductor gate`** — infra false-red needs a full fix-session instead of a quick re-run | DNS false-red: spawned a fix-session (15+ min) when `dotnet build` re-run (87s) would suffice. | `conductor gate` re-runs battery at HEAD, reports to log, clears pendingFix if green. No agent spawned. |
| 4 | **No heartbeat runtime toggle** — 93-95% of git history is heartbeat noise | `heartbeatMinutes: 0` had to be hardcoded into plan JSONs. No TUI toggle, no CLI command. | `conductor heartbeat on|off` + TUI `H` key. Amend previous heartbeat commit instead of creating N new ones. |

### Observability (structured data replaces guesswork)

| # | Gap | Observed impact | Fix |
|---|-----|----------------|-----|
| 5 | **Unstructured log** — `conductor.log` is line-oriented text. No query, no correlation. | "How many times did the build gate fail?" — unanswerable without grep + manual counting. | Structured JSON sink (B2.5 foundation exists). `conductor log --query "stage=P7 and gate=build and outcome=fail"`. |
| 6 | **Attempt-budget is blind** — identical stalls burn full budget | B4: 6 DNS-stalled sessions with 0 output before NeedsHuman. Re-verification ran 3x after already done. | Identical-outcome early termination (2 consecutive stalls with 0 commits → NeedsHuman). Exponential backoff. |
| 7 | **DNS/network health gate** — no pre-flight before spawning agent | 13 sessions against unreachable network DNS outage, each burned 12 min stall timer. | `Dns.GetHostEntry` check before agent spawn. Park + clear message if fail. Recheck + auto-resume. |
| 8 | **No cost/overhead split** — "session cost" conflates agent work + gate battery | TUI shows total per-session cost but can't distinguish agent vs overhead. | Split `sessionCost` (agent work) + `overheadCost` (gates, setup, teardown). Report in TUI + REPORT.md. |

### Architecture & Workflow

| # | Gap | Fix |
|---|-----|-----|
| 9 | **Sequential pipeline** — audit waits for deliver, next deliver waits for audit. Dead air between phases. | **QA parallelization:** launch audit + next deliver concurrently. Audit against pinned commit. Deliver on separate branch. ≥20% faster end-to-end. |
| 10 | **Advisor is a passive observer** — diagnoses but can't act. Orchestrator ignores verdict, burns attempts. | **Stronger advisor:** structured `AdvisorVerdict` with `Action` enum (BlockRetry, ResetBudget, NeedsHuman, ApplyFix, RerunGates). Orchestrator honors these actions. |
| 11 | **Git history pollution** — 6-8 `chore(conductor)` commits per session across 77 sessions = ~500 commits of noise | **Squash-bookkeeping mode:** on phase confirm, squash all `chore(conductor):` commits into one. Feature/audit commits preserved. |
| 12 | **Re-audit impossible** — once a phase is confirmed, no way to re-audit with new knowledge | **`conductor audit <stage> --replay`** — runs audit prompt against completed phase without affecting RunState. Output to `.conductor/audits/<stage>-replay-<timestamp>.md`. |
| 13 | **Mid-session control feedback** — control verbs (`retry-stage`, `rollback`, `goto`) arrive silently; operator gets no feedback if guard rejects them | Operator receives log message + TUI toast for every control action attempt, including rejected ones. |
| 14 | **MCP task server not production-wired** — B9 delivered the server code but it's never spawned as a real child process for the agent | Wire `McpTaskServer` into the agent launch pipeline. Agent has live task_list/task_update/task_add during its session. |
| 15 | **Multi-conductor coordination** — no shared state between concurrent conductor runs on different repos | `~/.conductor/registry.json` — lightweight shared file for cross-run coordination (port allocation, lock files). |

---

## Proposed Stage Map — Era v3

```
DAILY DRIVER (high-return, low-risk — ship first, independent order)
  D1  conductor status — LLM-powered status report (deepseek-flash, ~$0.002/call)
  D2  conductor gate — ad-hoc gate re-run without agent session
  D3  Heartbeat runtime toggle + amend strategy (H key · CLI heartbeat on|off)
  D4  Mid-session control feedback — rejected verbs produce visible feedback

OBSERVABILITY (structured data replaces guesswork)
  O1  Structured log — JSON rolling sink + conductor log --query
  O2  Attempt-budget intelligence + DNS/network health gate
  O3  Cost/overhead split (agent vs gate accounting in TUI + report)

PIPELINE EFFICIENCY (faster, smarter execution — depends on O1-O2 foundation)
  P1  Dynamic plan reconfiguration (conductor plan set/reload/add-stage)
  P2  QA parallelization (audit + next deliver concurrently)
  P3  Stronger advisor (structured verdicts, orchestrator honors actions)
  P4  Squash-bookkeeping (git history dedup on phase confirm)
  P5  Post-hoc audit replay (conductor audit <stage> --replay)

INFRASTRUCTURE (plumbing that unlocks the next wave)
  I1  MCP task server production wiring — spawn for real agent sessions
  I2  Multi-conductor coordination registry (~/.conductor/registry.json)

SELF-EVOLUTION (conductor eats its own dogfood again)
  E1  Era v3 delivered by conductor sessions under the v2 protocol
  E2  Era v3 closes with its own final handover + human checklist
```

**Dependency order:** D1-D4 are independent, ship in any order. O1-O3 build on each other. P1-P5 depend on O1-O2. I1-I2 are independent. E1-E2 close the era.

---

## Key Architectural Decisions

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| AD-01 | Status report LLM | deepseek-chat (fast/cheap, ~$0.002/call) | Status queries are frequent, don't need deep reasoning. Claude-sonnet opt-in for complex analysis. |
| AD-02 | Plan reconfiguration | In-memory `PlanConfig` re-read from JSON on `reload`; `set` writes JSON + updates in-memory | Next-session boundary (never mid-session). Plan version bumps on every modification. |
| AD-03 | Structured log | Serilog JSON rolling-file sink + text sink side-by-side | Text for humans, JSON for `conductor log --query`. Both coexist during migration. |
| AD-04 | QA parallelization | Audit against pinned commit SHA; deliver session on separate branch/worktree | No rollback needed. HIGH-severity audit finding → gracefully interrupt deliver. |
| AD-05 | This worktree | `feat/era-v3` on the same `C:\Code\conductor` repo (not a separate worktree) | Simpler than worktree for mutation-level work. Stable binary still from `master`. |
| AD-06 | Multi-conductor registry | JSON file at `%USERPROFILE%\.conductor\registry.json` | Lighter than SQLite for infrequent cross-run coordination. Easy to inspect with any editor. |

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| D1 status report costs drift higher with use | Medium | Low ($0.002→$0.01 per call still negligible) | Rate-limit: `statusReport.maxPerHour` in PlanConfig |
| P2 QA parallelization introduces merge conflicts | Low | Medium | Audit against pinned commit; deliver on separate branch. Conflicting audit fix → defer to next deliver. |
| P1 plan reconfiguration breaks running session | Low | High | Changes take effect NEXT session only. Validation runs on every change. |
| O1 structured log increases disk I/O | Low | Low | JSON sink is async, text sink unchanged. Log rotation built into Serilog rolling file. |
| I1 MCP task server spawn adds agent overhead | Low | Low | Single lightweight stdio process per session. Negligible vs agent's own token burn. |
| D4 control feedback missed if TUI not running | Low | Low | Also writes to conductor.log and structured log. TUI toast is bonus, not primary channel. |

---

## First Session — What to Do First

Ship **D1 (conductor status)** — solves the most immediate pain ("what's happening?"). Single session, high impact, cheap. Uses deepseek-flash for fast/cheap analysis. The agent needs to:
1. Add `conductor status` CLI verb that reads `state.json` + log tail
2. Call a configurable LLM API with the data
3. Stream the analysis to TUI + console
4. Wire rate limiting

---

*This document feeds the Era v3 plan. It is NOT a plan file — it's the strategic brief that the plan implements.*
