# Conductor Era v3 — Phase Tracker

**Read order:** this file → `FUSION.md` (master plan) → `docs/workflows/conductor-era3-workflow.md` (per-session details).
**Branch:** `feat/era-v3`. **Driver:** `C:\Code\conductor\bin\conductor.exe` (stable from master).

## Handoff (overwrite this block, ≤12 lines, no history)
last: P5 — Post-hoc audit replay landed. AuditCommand (`conductor audit <stage> --replay`) spawns the plan's agent with a structured replay prompt against completed stages. Read-only: no RunState mutation. Output to .conductor/audits/<stage>-replay-<ts>.md. Prompt includes checkpoints, git log, evidence tails. Registered as "audit" verb. Completion scripts (PS+bash) updated. 555 tests pass (0w/0e).
stage: P5 DONE. Next: I1 — MCP task server production wiring.
next: Read FUSION.md §I1. Wire McpTaskServer (B9) into AgentSession production launch. Agent gets live task_list/update/add. Task persistence via events.jsonl.
trap: None.

## Baseline Numbers

| Metric | Value |
|--------|-------|
| Target framework | net10.0 |
| Tests | 555 pass (0 warn, 0 err) |
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
| P1 | Dynamic plan reconfiguration | DONE | c153a2b | docs/era3/evidence/P1/ |
| P2 | QA parallelization | DONE | 2a0fdde | docs/era3/evidence/P2/ |
| P3 | Stronger advisor — structured verdicts | DONE | 56ec088 | docs/era3/evidence/P3/ |
| P4 | Squash bookkeeping — clean git history | DONE | f03f4a0 | docs/era3/evidence/P4/ |
| P5 | Post-hoc audit replay | DONE | 587119d | docs/era3/evidence/P5/ |
| I1 | MCP task server production wiring | TODO | — | — |

## Quick Commands

```powershell
dotnet build Conductor.slnx
dotnet test Conductor.slnx --no-build
C:\Code\conductor\bin\conductor.exe run --dry-run --plan .conductor\plans\conductor-era3.plan.json
C:\Code\conductor\bin\conductor.exe run         --plan .conductor\plans\conductor-era3.plan.json
```
