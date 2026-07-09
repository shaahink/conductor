# Conductor Era v3 — Phase Tracker

**Read order:** this file → `FUSION.md` (master plan) → `docs/workflows/conductor-era3-workflow.md` (per-session details).
**Branch:** `feat/era-v3`. **Driver:** `C:\Code\conductor\bin\conductor.exe` (stable from master).

## Handoff (overwrite this block, ≤12 lines, no history)
last: O3 — cost overhead split landed. agentCost vs overheadCost in TUI + REPORT.md. SessionRecord + RunState track overhead separately; GateResult.EstimatedCostUsd(duration × rate); DashboardRenderer.CostLine shows "agent $X gates $Y"; Reporter header + sessions table have split. 530 tests pass (14 O3), 0w/0e.
stage: O3 DONE. Next: P1 — dynamic plan reconfiguration.
next: Read workflow §P1. Plan set/reload/add-stage, TUI E editor, next-session boundary, version bumps.
trap: O2 QA passed — evidence present, claims verified against fresh artifacts.

## Baseline Numbers

| Metric | Value |
|--------|-------|
| Target framework | net10.0 |
| Tests | 530 pass (0 warn, 0 err) |
| Source files | ~40 .cs under src/Conductor |
| Branches | master (stable), feat/baton (v2), feat/era-v3 (this phase) |

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| D1 | conductor status — LLM-powered status report | DONE (verified) | 8b9ec2b | docs/era3/evidence/D1/ |
| D2 | conductor gate — ad-hoc gate re-run | DONE | 9b85d7e | docs/era3/evidence/D2/ |
| D3 | Heartbeat runtime toggle + amend strategy | DONE | 79a96a8 | docs/era3/evidence/D3/ |
| D4 | Mid-session control feedback | DONE | f40a974 | docs/era3/evidence/D4/ |
| O1 | Structured log + conductor log --query | DONE | be216a4 | docs/era3/evidence/O1/ |
| O2 | Budget intelligence + network health gate | DONE | 2f4d103 | docs/era3/evidence/O2/ |
| O3 | Cost overhead split | DONE | 419fb9a | docs/era3/evidence/O3/ |
| P1 | Dynamic plan reconfiguration | TODO | — | — |
| P2 | QA parallelization | TODO | — | — |
| P3 | Stronger advisor — structured verdicts | TODO | — | — |
| P4 | Squash bookkeeping — clean git history | TODO | — | — |
| P5 | Post-hoc audit replay | TODO | — | — |
| I1 | MCP task server production wiring | TODO | — | — |

## Quick Commands

```powershell
dotnet build Conductor.slnx
dotnet test Conductor.slnx --no-build
C:\Code\conductor\bin\conductor.exe run --dry-run --plan .conductor\plans\conductor-era3.plan.json
C:\Code\conductor\bin\conductor.exe run         --plan .conductor\plans\conductor-era3.plan.json
```
