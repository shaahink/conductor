# Conductor Era v3 — Phase Tracker

**Read order:** this file → `FUSION.md` (master plan) → `docs/workflows/conductor-era3-workflow.md` (per-session details).
**Branch:** `feat/era-v3`. **Driver:** `C:\Code\conductor\bin\conductor.exe` (stable from master).

## Handoff (overwrite this block, ≤12 lines, no history)
last: D3 — heartbeat runtime toggle + amend strategy landed. CLI heartbeat on|off, TUI H key, amend beats create-per-interval. 510 tests, 0w/0e.
stage: D3 DONE. Next: D4 — Mid-session control feedback.
next: Read workflow §D4. Rejected/applied controls produce log + TUI toast. No silent failures.
trap: H was already bound to health modal. Health moved to F1. PeriodicTimer deferred (sync/async boundary).

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
| D2 | conductor gate — ad-hoc gate re-run | DONE | 9b85d7e | docs/era3/evidence/D2/ |
| D3 | Heartbeat runtime toggle + amend strategy | DONE | 0d199f1 | docs/era3/evidence/D3/ |
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
