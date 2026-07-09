# Conductor Era v3 — Strategic Roadmap

**Written:** 2026-07-09 after Baton v2 completion (77 sessions, 67/67 checkpoints)
**Author:** Cross-project analysis of Loom (51 sessions), Shamshir (39+ sessions), Conductor (77 sessions)
**Base state:** Conductor v2 (Baton) fully delivered. Loom complete. Shamshir P7.2 running.
**Git:** `master` (stable binary), `feat/baton` (v2 worktree)

---

## Current Inventory — What Exists Today

### Conductor v2 (Baton) — Delivered
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

### Loom — Delivered
```
SymbolTable + SymbolRef + resolution tiers │  BodyFacts + seam detectors
SemanticLite populator                    │  Flow store + projections
MCP v2 (22 tools, cold ≥90%)             │  Workbench (tabs, code, inspector)
Archetype detection (4 types)            │  22-repo bench (21/22 OK)
3 product claims verified                 │  490 tests, build 0w/0e
Debt cleanup D1-D3 done, D4 skipped       │  HANDOVER-LOOM.md written
```

### Shamshir — In Progress
```
P0-P6 all DONE + audited (16 findings F1-F16)  │  Research pipeline + playbooks
cTrader proved (run 77e37dee ✅)               │  P7.2 running headless now
P7.3-P7.8 remaining (traps, gates, audit)       │  39 sessions, $3.49 total
```

---

## Gap Analysis — What's Missing

### Tier 1: Your Daily Experience (highest pain)

| # | Gap | Observed impact | Fix |
|---|-----|----------------|-----|
| 1 | **No `conductor status`** — you ask "what's happening?" and someone must manually read 3 state.json + logs | You had to ask me (an LLM) to synthesize a report. Conductor should answer directly. | `conductor status` CLI verb calls configurable LLM (deepseek-flash by default) with `state.json` + log tail → streams analysis to TUI. Cost: ~$0.002/call. |
| 2 | **No dynamic plan reconfiguration** — edit plan JSON → restart conductor to pick it up | `heartbeatMinutes: 0` needed manual edit to 3 plans. `conductor plan reload` doesn't exist. | `conductor plan set/get/reload/add-stage`. TUI plan tree editor (`E` on stage). Next session picks up changes. |
| 3 | **No `conductor gate`** — DNS false-red needed a full fix-session (15+ min) instead of 87-second `dotnet build` re-run | Loom s24: infra fail → spawned fix-session. Simple re-run wasted an agent pass. | `conductor gate` re-runs battery at HEAD, reports to log, clears pendingFix if green. No agent spawned. |
| 4 | **No heartbeat runtime toggle** — 93-95% of git history is heartbeat noise | `heartbeatMinutes: 0` was hardcoded into 3 plan JSONs. No TUI toggle. | `conductor heartbeat on|off` + TUI `H` key. Amend previous heartbeat commit instead of creating N. |

### Tier 2: Quality & Observability

| # | Gap | Observed impact | Fix |
|---|-----|----------------|-----|
| 5 | **Unstructured log** — `conductor.log` is line-oriented text. No query, no structured timeline, no correlation. | "How many times did the build gate fail?" — currently unanswerable without grep + manual counting. | Structured JSON sink (already partially wired in B2.5). `conductor log --query "stage=P7 and gate=build and outcome=fail"`. |
| 6 | **Attempt-budget is blind** — 6 consecutive stalls with 0 output lines burned full budget. Advisor diagnosed but couldn't stop it. | Baton B4: 6 DNS-stalled sessions before NeedsHuman. Shamshir P7.1: same re-verification ran 3 times after it was already done. | Identical-outcome early termination (2 consecutive stalls with 0 commits → NeedsHuman). Exponential backoff. Configurable: `limits.stallPatternTermination`. |
| 7 | **DNS/network health check** — no pre-flight before spawning agent | 13 sessions spawned against unreachable network during DNS outage. Each burned 12 min stall timer. | `Dns.GetHostEntry("github.com")` + `Dns.GetHostEntry("api.nuget.org")` before agent spawn. If fail → park + clear message. Recheck every N seconds. Auto-resume when healthy. |
| 8 | **No cost/overhead split** — "session cost" includes both agent work AND gate battery | TUI shows total per-session cost but can't distinguish "$0.10 agent + $0.00 gates" from "$0.05 agent + $0.05 gates" | Split `sessionCost` (agent work) + `overheadCost` (gates, setup, teardown). Report in TUI + REPORT.md. |

### Tier 3: Architecture & Workflow

| # | Gap | Fix |
|---|-----|-----|
| 9 | **Sequential pipeline** — audit waits for deliver, next deliver waits for audit. Dead air between phases. | **QA parallelization:** launch audit + next deliver concurrently. Audit runs against pinned commit. Findings inject into running deliver session. Target: ≥20% faster end-to-end. |
| 10 | **Advisor is a passive observer** — diagnoses but can't act. Orchestrator ignores verdict, burns attempts anyway. | **Stronger advisor:** structured `AdvisorVerdict` with `Action` enum (BlockRetry, ResetBudget, NeedsHuman, ApplyFix, RerunGates). Orchestrator honors actions. |
| 11 | **Per-repo followups** — 25+ open followups across 3 repos with no central tracking. Items get re-homed and lost. | **Cross-project followup registry:** `~/.conductor/followups.db` (SQLite). Each followup has project/stage/severity/status/rehomedFrom/rehomedTo. TUI plan tree shows `⚠ N followups` per stage. |
| 12 | **Git history pollution** — 6-8 `chore(conductor)` commits per session × 77 sessions = ~500 commits of noise | **Squash-bookkeeping mode:** on phase confirm, `git rebase -i` squashes all `chore(conductor):` commits into one. Feature/audit commits preserved. |
| 13 | **Re-audit impossible** — once a phase is confirmed, there's no way to re-audit with new knowledge | **`conductor audit <stage> --replay`** — runs audit prompt against a completed phase without affecting RunState. Output goes to `.conductor/audits/<stage>-replay-<timestamp>.md`. |

### Tier 4: Project-Specific Next Work

| # | Project | Work | Effort |
|---|---------|------|--------|
| 14 | **Loom** | 8 debt items from conductor-DEBT.md (SymbolTable member indexing, BodyFacts scoping, TfmScore net10+, Flow hardening) | 2-3 sessions |
| 15 | **Loom** | eShop 5 TraceQuality failures on non-CQRS call-spine | 1 session |
| 16 | **Loom** | ContextPack server round-trip (close Meridian Trap A properly) | 1 session engine |
| 17 | **Shamshir** | P7.3-P7.8: traps, gates, cTrader test audit, final audit | 6 sessions (after P7.2 finishes) |

---

## Proposed Stage Map — Era v3

```
DAILY DRIVER (high-return, low-risk — ship first)
  D1  conductor status — LLM-powered status report (deepseek-flash, ~$0.002/call)
  D2  conductor gate — ad-hoc gate re-run without agent session
  D3  Heartbeat runtime toggle + amend strategy (H key, CLI heartbeat on|off)

OBSERVABILITY (structured data replaces guesswork)
  O1  Structured log — JSON sink + conductor log --query
  O2  Attempt-budget intelligence + DNS/network health gate
  O3  Cost/overhead split + cross-project followup registry

PIPELINE EFFICIENCY (faster, smarter execution)
  P1  Dynamic plan reconfiguration (conductor plan set/reload/add-stage)
  P2  QA parallelization (audit + next deliver concurrently)
  P3  Stronger advisor (structured verdicts, orchestrator honors actions)
  P4  Squash-bookkeeping (git history dedup on phase confirm)
  P5  Post-hoc audit replay (conductor audit --replay)

PROJECT WORK (Loom + Shamshir)
  L1  Loom debt items — SymbolTable, BodyFacts, TfmScore, Flow (2-3 sessions)
  L2  Loom eShop trace investigation (1 session)
  S1  Shamshir P7.3-P7.8 completion (6 sessions, after P7.2 done)

SELF-EVOLUTION (conductor eats its own dogfood again)
  E1  Era v3 delivered by conductor sessions under the v2 protocol
  E2  Era v3 closes with its own final handover + human checklist
```

**Dependency order:** D1-D3 are independent, ship in any order. O1-O3 need D3's structured log foundation. P1-P5 build on O1-O3. L1-L2 depend on nothing. S1 depends only on P7.2 finishing.

---

## Key Architectural Decisions for v3

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| AD-01 | Status report LLM | deepseek-chat (fast/cheap, ~$0.002/call) | Status queries are frequent and don't need deep reasoning. Claude-sonnet as opt-in for complex analysis. |
| AD-02 | Plan reconfiguration | In-memory `PlanConfig` re-read from JSON on `reload`; `set` writes JSON and updates in-memory | Next-session boundary (never mid-session). Plan version bumps on every modification. |
| AD-03 | Followup registry | SQLite at `%USERPROFILE%\.conductor\followups.db` | Cross-project by definition. SQLite avoids schema management. Multiple conductor instances can coexist. |
| AD-04 | Structured log | Serilog JSON rolling-file sink + text sink side-by-side | Text log for humans, JSON log for `conductor log --query`. Both coexist during migration. |
| AD-05 | QA parallelization | Audit runs against pinned commit SHA; deliver session on its own branch/worktree | No rollback needed. If audit finds HIGH-severity defect, deliver session is gracefully interrupted. |
| AD-06 | This worktree | `feat/era-v3` on the same `C:\Code\conductor` repo (not a separate worktree) | Simpler than worktree for mutation-level work. Stable binary still from `master`. |

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| D1 status report costs drift higher with use | Medium | Low ($0.002→$0.01 per call still negligible) | Rate-limit: `statusReport.maxPerHour` in PlanConfig |
| P2 QA parallelization introduces merge conflicts | Low | Medium | Audit runs against pinned commit; deliver session on separate branch. If audit fix conflicts → defer to next deliver |
| P1 plan reconfiguration breaks running session | Low | High | Changes take effect NEXT session only. Validation runs on every change. |
| O1 structured log increases disk I/O | Low | Low | JSON sink is async, text sink unchanged. Log rotation built into Serilog rolling file. |
| Shamshir P7.2 stalls again | Medium | Low (was already proved) | D3 (health gate) + D1 (status check) help detect early. Inject is already queued. |

---

## First Session — What to Do First

1. Ship **D1 (conductor status)** — solves your most immediate pain ("what's happening?"). Single session, high impact, cheap.
2. Ship **D2 (conductor gate)** — prevents the "DNS false-red → fix-session" waste cycle.
3. Let Shamshir P7.2 finish (running headless in its own window).
4. Re-evaluate: Does Shamshir need P7.3-P7.8, or is the cTrader proof enough to skip to the final audit?

---

*This document feeds the Era v3 plan. It is NOT a plan file — it's the strategic brief that the plan implements.*
