# Conductor Era v3 — Phase Tracker

**Read order:** this file → `FUSION.md` (master plan) → `docs/workflows/conductor-era3-workflow.md` (per-session details).
**Branch:** `feat/era-v3`. **Driver:** `C:\Code\conductor\bin\conductor.exe` (stable from master).

## Handoff (overwrite this block, ≤12 lines, no history)
last: Plan created. All 14 stages TODO.
stage: D1 — conductor status (LLM-powered report).
next: Read FUSION.md Phase 3, then workflow §D1. Add StatusCommand.
trap: New branch `feat/era-v3` — need to create it from feat/baton HEAD.

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
| D1 | conductor status — LLM-powered status report | TODO | — | — |
| D2 | conductor gate — ad-hoc gate re-run | TODO | — | — |
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
