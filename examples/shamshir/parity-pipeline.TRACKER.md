# Shamshir — iter-parity-pipeline Tracker (resume here)

**This is the machine-readable progress source Conductor parses.** The narrative docs
(`PLAN.md`, `AUDIT.md`, and any `PROGRESS.md`/`HANDOVER.md`) stay as the human authority — this file
is the strict checkpoint table + handoff that Conductor verifies against (BATON-BRIEF D-2).

**Read order for a fresh session:** this file → `PLAN.md` → `AUDIT.md` (the F-findings your phase
fixes) → `docs/WORKFLOW.md` → `docs/reference/SYSTEM-REFERENCE.md` (+ CODE-MAP, BACKTEST-ARCHITECTURE).
Branch: `iter/parity-pipeline` off `iter/quant-model--p1-tf-agnostic`.

> Conventions for this plan (set in the Conductor plan file, since ids are irregular):
> stage-id pattern allows `P-0`, `P0`, `P0.1`, `P3.4b`, `F5`; handoff marker `## Handoff`;
> human token `HUMAN:`; status vocabulary TODO/IN PROGRESS/DONE/BLOCKED.

## Handoff  (overwrite this block, ≤12 lines, no history)
last: (none) — tracker skeleton authored by Baton B1 as the Shamshir drivability proof.
stage: **P-0 NOT STARTED**.
gate: not run. See PLAN §11 verification matrix for each phase's exact gate.
next: **P-0** — land the working tree deliberately (revert 8 strategy JSONs to Market per Q1; 3 commits
      with pasted gate output; build 0 / Unit / fast Sim / tsc clean).
dirty: ~24 modified / 3 new in the Shamshir tree (F5 kernel fix, P7/P3.3 tests, compare-both UI).
trap: do NOT batch-commit blind (R4). Golden fixtures WILL move on P0.1 — separate REBASELINE commit.

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path from a run this phase.
Owner-gate rows (P2.2) BLOCK for approval even when green.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| P-0 | Land the working tree deliberately (3 commits, gates pasted) | TODO | | |
| P0.1 | ¼-sizing bug (F1): VenueSizingParityTests green + equal lots in DB | TODO | | |
| P0.2 | Run-status truth (F5): ctrader run completed; fault→completed-with-warnings | TODO | | |
| P0.3 | Trade persistence barrier (F6): BTC-scenario test; backfill restores trades | TODO | | |
| P0.4 | Entry-latency instrumentation (F2): entryDelayBars in reconcile | TODO | | |
| P0.5 | Venue-parity test tier (R8): Category=VenueParity wired into gate | TODO | | |
| P1.1 | One database (F10): Host CLI verbs run against Web DB; 84/84 ReferenceScales | TODO | | |
| P1.2 | Config propagation + drift (F9,F7): JSON edit reflected; UI edit survives | TODO | | |
| P2.1 | Run state machine + tests (F8): cancel/watchdog/orphan-kill green | TODO | | |
| P2.2 | Owner-gated compare-both reconcile verdict (inherited P6.1 headline gate) | TODO | | |
| P3.1 | ResearchCli console project (verbs, --json, VERDICT lines) | TODO | | |
| P3.2 | Playbook engine (typed steps, owner-gate, resume) | TODO | | |
| P3.3 | UI review page /research (read + approve) | TODO | | |
| P3.4 | Canonical playbooks (venue-parity, explore-exit) run end-to-end via CLI | TODO | | |
| P4.1 | Exploration funnel (F11) + MAE/MFE units doctrine (F12) + entry lab (P3.6) | TODO | | |
| P5.1 | UI truth (F13-F16) + targeted Angular refactor | TODO | | |
| P6.1 | Wild list (pipeline-gated; each ships with a measuring playbook) | TODO | | |

## Quick commands

```powershell
dotnet build TradingEngine.slnx
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"
# per-phase gates: see PLAN.md §11 verification matrix
```
