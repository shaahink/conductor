# Conductor Era v3 — Phase Tracker

**Read order:** this file → `FUSION.md` (master plan) → `docs/workflows/conductor-era3-workflow.md` (per-session details).
**Branch:** `feat/era-v3`. **Driver:** `C:\Code\conductor\bin\conductor.exe` (stable from master).

## Handoff (overwrite this block, ≤12 lines, no history)
last: D2 — conductor gate command landed. gate (fast) + gate --full (full battery) both working. 510 tests, 0w/0e.
stage: D2 DONE. Next: D3 — Heartbeat runtime toggle + amend strategy.
next: Read workflow §D3. Add HeartbeatCommand, H key in TUI, PeriodicTimer.
trap: None — build + 510 tests green.

## Baseline Numbers

| Metric | Value |
|--------|-------|
| Target framework | net10.0 |
| Tests | 497 pass (0 warn, 0 err) |
| Source files | ~40 .cs under src/Conductor |
| Branches | master (stable), feat/baton (v2), feat/era-v3 (this phase) |

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| D1 | conductor status — LLM-powered status report | DONE (verified) | 8b9ec2b | docs/era3/evidence/D1/ |
| D2 | conductor gate — ad-hoc gate re-run | DONE | be2f2ef | docs/era3/evidence/D2/ |
| D3 | Heartbeat runtime toggle + amend strategy | TODO | — | — |
| D4 | Mid-session control feedback | TODO | — | — |
| O1 | Structured log + conductor log --query | TODO | — | — |
| O2 | Budget intelligence + network health gate | TODO | — | — |
| O3 | Cost overhead split | TODO | — | — |
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
