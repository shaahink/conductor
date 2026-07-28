# Fusion — Cross-Project Completion + Conductor Era v3

**Written:** 2026-07-09
**Scope:** Close remaining work across Loom, Shamshir, and Conductor simultaneously.
**Total sessions:** ~22 across 5 phases.
**Design principle:** Highest value first, smallest effort first within each phase.
**Driver:** `C:\Code\conductor\bin\conductor.exe` (stable from master) for all runs.

---

## Live Status Dashboard

```
Project     │ PID   │ Stage              │ Phase         │ Sessions
────────────┼───────┼────────────────────┼───────────────┼─────────
Shamshir    │ 22452 │ P7.3 (s50)         │ Phase 1       │ 3/8 done
Loom        │ 28848 │ D6 TfmScore (s55)  │ Phase 2       │ 2/5 done
Conductor   │   —   │ Era v3 planned     │ Phase 3-5     │ 0/14 done
```

---

## Phase Map (dependency order)

```
PHASE 1 — SHAMSHIR FINISH (6 sessions, ~4h)
  Closes the iteration. Proves the headline gate. Writes FINAL-AUDIT.md.
  Running headless now on iter/parity-pipeline. Inject chain active.
  [P7.3 → P7.4 → P7.5 → P7.6 → P7.7 → P7.8]

PHASE 2 — LOOM DEBT (4 sessions, ~2h)
  Cleans the last engine debt. No more deferred items.
  Running headless now on feat/loom-l7. Inject chain active.
  [D6 → D7 → D8 → D9]

PHASE 3 — CONDUCTOR DAILY DRIVER (4 sessions, ~4h)
  Fixes your daily pains. Starts when plan files are ready.
  [D1 status → D2 gate → D3 heartbeat → D4 control feedback]

PHASE 4 — CONDUCTOR OBSERVABILITY (4 sessions, ~5h)
  Structured data replaces guesswork.
  [O1 log query → O2 budget intelligence → O3 cost split]

PHASE 5 — CONDUCTOR PIPELINE (6 sessions, ~10h)
  Efficiency, dynamic plans, knowledge sharing at scale.
  [P1 plan reconfig → P2 QA parallel → P3 advisor → P4 squash → P5 replay → I1 MCP wire]
```

---

## Phase 1 — Shamshir Finish (6 sessions)

**Repo:** `C:\Code\Shamshir` on `iter/parity-pipeline`
**Plan:** `.conductor/plans/shamshir-cleanup.plan.json` (existing)
**State:** P7.1-P7.3 done. Inject chain guides 4-8.

| # | Session | Effort | Value | Detail |
|---|---------|--------|-------|--------|
| P7.4 | Traps 4+5+6 + run playbooks | ~40m | Medium | Code fixes + close block-bootstrap + meta-allocator OWNER-PENDING |
| **P7.5** | **⭐ Paired compare-both (HEADLINE)** | ~60m | **Critical** | Proves iteration success criterion. Real EURUSD H1 paired run. Committed reconcile verdict. |
| P7.6 | F6-R economics recovery | ~40m | Medium | Close-fill reconstruction. Real cTrader proves no TRADES_UNRECONSTRUCTABLE. |
| P7.7 | cTrader test audit | ~30m | Low | Classification doc. |
| P7.8 | Final audit | ~45m | High | Rates all 34 checkpoints. Writes FINAL-AUDIT.md. |

---

## Phase 2 — Loom Debt (4 sessions)

**Repo:** `C:\Code\DevContext2-ui` on `feat/loom-l7`
**Plan:** `.conductor/plans/loom-debt.plan.json` (existing)
**State:** D1-D5 done. D6 running. Inject chain guides D7-D9.

| # | Session | Effort | Value | Detail |
|---|---------|--------|-------|--------|
| D6 | TfmScore net10.0+ | ~35m | High | Fix TfmScore blind spot to own framework version |
| D7 | Lambda scope pollution | ~40m | Medium | Latent correctness bug in multi-lambda methods |
| D8 | Flow model hardening | ~40m | Medium | Depth warning, proportional budget, integration test |
| D9 | SymbolTable member indexing | ~45m | Medium | Populate Member/Endpoint from BodyFacts pipeline |

**Skipped permanently:** D4 merge, R1-R3 design reviews, QA driver.

---

## Phase 3 — Conductor Daily Driver (4 sessions)

**Repo:** `C:\Code\conductor-baton` on `feat/era-v3`
**Plan:** `.conductor/plans/conductor-era3.plan.json`

| # | Session | Effort | Value | Detail |
|---|---------|--------|-------|--------|
| **D1** | **`conductor status`** | ~60m | **Highest daily pain** | LLM-powered status via deepseek-flash (~$0.002/call). Reads state.json + log → natural-language analysis to TUI. |
| D2 | `conductor gate` | ~45m | High | Re-run battery at HEAD. No agent spawned. Clears pendingFix. |
| D3 | Heartbeat toggle + amend | ~30m | Medium | `conductor heartbeat on\|off`, TUI `H` key, 1 commit/session. |
| D4 | Control feedback | ~20m | Medium | Rejected controls produce log + TUI toast. Currently silent. |

---

## Phase 4 — Conductor Observability (4 sessions)

| # | Session | Effort | Value | Detail |
|---|---------|--------|-------|--------|
| O1 | Structured log + query | ~90m | High | JSON rolling sink. `conductor log --query "stage=P7 and gate=build and outcome=fail"` |
| O2 | Budget intelligence + preflights | ~60m | High | 2× stall → NeedsHuman. DNS check before spawn. |
| O3 | Cost overhead split | ~30m | Medium | agent vs gates accounting in TUI + report. |

---

## Phase 5 — Conductor Pipeline (6 sessions)

| # | Session | Effort | Value | Detail |
|---|---------|--------|-------|--------|
| P1 | Dynamic plan reconfig | ~90m | High | `plan reload/set/add-stage`. TUI `E` on stage. Next-session boundary. |
| P2 | QA parallelization | ~90m | Medium | Audit + deliver concurrently via existing lane infra. |
| P3 | Stronger advisor | ~60m | Medium | Structured `AdvisorVerdict.Action` — orchestrator honors it. |
| P4 | Squash bookkeeping | ~30m | Low | Collapse chore commits on phase confirm. |
| P5 | Post-hoc audit replay | ~45m | Low | `conductor audit <stage> --replay`. |
| I1 | MCP task server wiring | ~45m | Medium | `McpTaskServer` spawned for real agent sessions. |

---

## Paste-Plan Ingestion Tool

`tools/paste-plan.ps1` — paste any structured plan in this template, it generates runnable conductor files:

**Input template:**
```markdown
# Plan: <name>
Branch: <branch>
Repo: <repo-path>
Gates: build command | test command

## Stage 1
ID: S1
Title: Do the thing
Effort: ~30m
Notes: What to do, what files, what gate proves it.

## Stage 2
...
```

**Output:** `plan.json` + `TRACKER.md` + `workflow.md` — ready for `conductor run`.

**Usage:**
```powershell
# Paste plan markdown into stdin or file
.\tools\paste-plan.ps1 -InputFile myplan.md -OutputDir .conductor\plans\

# Or pipe from clipboard
Get-Clipboard | .\tools\paste-plan.ps1 -OutputDir .conductor\plans\
```

---

## Key Decisions

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| AD-01 | Status LLM | deepseek-chat (~$0.002/call) | Frequent, don't need deep reasoning |
| AD-02 | Plan reconfigure | Next-session boundary | Never mid-session. Validated on every change. |
| AD-03 | Structured log | Serilog JSON + text side-by-side | Both coexist during migration |
| AD-04 | 3 repos parallel | Separate conductor processes, shared binary | Independent locks, no shared state |
| AD-05 | Branch | `feat/era-v3` on main conductor repo | Same repo as v2. Stable binary from master. |

---

## Total Estimate

| Phase | Sessions | Effort | Status |
|-------|----------|--------|--------|
| Phase 1 — Shamshir | 6 | ~4h | 🟢 Running (s50) |
| Phase 2 — Loom | 4 | ~2h | 🟢 Running (s55) |
| Phase 3 — Conductor Daily | 4 | ~4h | 📋 Ready to start |
| Phase 4 — Conductor O11y | 4 | ~5h | ⏳ After D1-D4 |
| Phase 5 — Conductor Pipeline | 6 | ~10h | ⏳ After O1-O3 |
| **Total** | **~24** | **~25h** | **~2 days parallelized** |
