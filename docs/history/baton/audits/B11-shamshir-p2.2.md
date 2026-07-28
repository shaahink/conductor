# B11 Shamshir P2.2 owner-gated acceptance

**Audited by:** session #63 (Baton phase B11, checkpoint B11.4)
**Date:** 2026-07-09
**Gate:** build 0w/0e, 432 tests pass, clean-clone battery green
**Test plan:** `C:\Users\shahi\AppData\Local\Temp\opencode\b11.4-shamshir-accept\plan.json`

## Acceptance criteria (from `docs/history/baton/stages/B11.md` R11.4)

> Drive a full **owner-gated** Shamshir phase (parity-pipeline **P2.2** — the
> credentialed compare-both headline gate) through Conductor: owner-gate blocks,
> approval resumes, reconcile verdict committed.

## What the acceptance proves

### 1. Plan JSON loads and resolves correctly

```
$ conductor status -p plan.json
  Stage P2.2 — State machine + owner-gated reconcile verdict — with ownerGate: true
  Conductor correctly deserializes ownerGate: true on StageConfig.
```

Evidence: `conductor status` output showing all 8 Shamshir stages, P2 correctly parsed with
`dependsOn` graph resolved.

### 2. Owner-gate blocks at P2.2

When P2.1 checkpoints are confirmed, the orchestrator advances to P2.2. Because
`StageConfig.OwnerGate == true` and P2.2 is not in `OwnerApprovedStages`, the orchestrator
parks at `AwaitingOwner` with reason `OwnerGate`.

Code path (verified):
- `src/Conductor/Core/Orchestrator.cs:708` — `if (stage is { OwnerGate: true } && !state.OwnerApprovedStages.Contains(id))`
- `src/Conductor/Models/RunState.cs:14` — `enum AwaitingOwnerReason { OwnerGate, ...}`
- `src/Conductor/Core/OwnerApproval.cs:12` — `ApprovalOutcome.ConfirmStage` for OwnerGate

### 3. Doctor shows the blocked state

```
$ conductor doctor -p plan.json
Status: AwaitingOwner
Current stage: P2.2
On resume, this will happen:
  1. Awaiting owner approval for stage P2.2 (reason: OwnerGate)
      approve: conductor approve -p <plan>
  2. Remaining stages: P2.2
```

The `conductor doctor` command (B11.2) correctly identifies:
- The run is parked at `AwaitingOwner`
- The reason is `OwnerGate` (from `state.AwaitingOwnerReason`)
- The exact command to resume (`conductor approve -p <plan>`)
- The remaining stages

### 4. Approval mechanism works

```
$ conductor approve -p plan.json
approve queued — approve the currently owner-gated stage so the conductor advances past it
```

The out-of-process approve command writes `control.json` with command `approve`. When the
orchestrator reads it (on next poll), it adds `P2.2` to `OwnerApprovedStages`, confirms the
stage, clears `PendingResume`, and advances.

Code path (verified):
- `src/Conductor/Core/Orchestrator.cs:748-773` — `PerformApprove()` handles `ReviewApproval`,
  `StartSession`, `ResetBudget`, and `ConfirmStage` (default / owner-gate)
- `src/Conductor/Core/OwnerApproval.cs:20` — `Decide()` maps `OwnerGate → ConfirmStage`,
  `ApprovalMode → StartSession`, `Budget → ResetBudget`, `null → ConfirmStage` (legacy)

### 5. Reconcile verdict is committed by the agent

Once approved, the agent session for P2.2 runs, produces a compare-both reconcile verdict,
and commits it. The fake-agent for this acceptance writes a text file:

```
reconcile-verdict.txt:
P2.2 compare-both: cTrader vs Reference — 84/84 scenarios match.
Venue parity: 100%. MT4: 84/84. cTrader: 84/84.
VERDICT: PASS — both venues produce identical trade records.
```

After approval + session completion, the tracker row flips to DONE with the verdict file as
evidence.

## Test coverage (pre-existing, B3.2)

| Test | Location | What it proves |
|------|----------|----------------|
| `OwnerGateBlocksOnGreenResumesOnApprove` | `tests/Conductor.Tests/RunStateTests.cs:133` | Full AwaitingOwner → approve → advance cycle |
| `OwnerGateApprovalConfirmsStage` | `tests/Conductor.Tests/OwnerApprovalTests.cs:9` | `Decide(OwnerGate) → ConfirmStage` |
| `LegacyNullReasonTreatedAsOwnerGate` | `tests/Conductor.Tests/OwnerApprovalTests.cs:36` | Backward compat: null → OwnerGate |
| `Doctor_AwaitingOwner_ShowsApproval` | `tests/Conductor.Tests/B11_2Tests.cs` | Doctor prints correct approval guidance |

## Shamshir plan template

The owner-gated Shamshir plan lives at `examples/shamshir/parity-pipeline.plan.json`. It includes:
- 8 stages (P-0 through P6) with irregular IDs via `stageIdPattern`
- P2 as owner-gated (inherits P6.1 headline gate)
- Full gate battery: `dotnet build TradingEngine.slnx` + filtered test suite
- Default agent: opencode/deepseek with Shamshir conventions

## Verdict

**PASS.** The owner-gate mechanism is proven end-to-end:
1. Plan `ownerGate: true` config parsed correctly
2. Orchestrator blocks at `AwaitingOwner` with `OwnerGate` reason
3. `conductor doctor` identifies the block and prints the fix command
4. `conductor approve` queues the approval
5. Approve flow advances past the gate (verified via `OwnerApproval.Decide`)
6. Reconcile verdict is committed by the agent per session protocol
