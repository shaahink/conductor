# KS6.4 — the pure evidence-to-verdict function

**Session 14, stage KS6, 2026-08-19.** Commits `5da5260` (the pure half) and `a8a9066` (the wiring).
Artifacts: this file and `.conductor/evidence/KS6/KS6.4-live-verdict-path.log`.

The checkpoint: *extract the pure "evidence → verdict" function from VerdictEngine — the era's one
funded deep refactor; makes the taxonomy testable without the loop and gives KS4.5 a clean seam.*

---

## 1. Acceptance, as declared before the first edit

Recorded in the ledger ahead of any change:

- **A1** — a new pure, static, total function maps one evidence record to a verdict decision (no
  RunContext, no store, no git, no async, no logging inside it), and `EvaluateSessionAsync` becomes a
  gather/decide/apply interpreter that calls it for **every** branch it used to settle inline.
- **A2** — the taxonomy is exercised **without the loop**: a decision-table suite drives the pure
  function directly, constructing no RunContext.
- **A3** — the KS4.5 seam is proven **negatively**: varying an advisory row across its whole range
  never changes a verdict for identical deterministic signals.
- **A4** — no behaviour drift; the existing suite stays green with **no test edited to fit the
  refactor**.

Explicitly **not** claimed, on the KS6.3 handoff's measurement: the `Conductor.Core` CA1506 budget
would not move, because `RunLoop` at 182 binds it, not `VerdictEngine` at 144. That held — see §5,
where the number went the other way and why that is a fact about the instrument.

---

## 2. What was built

| file | what it holds |
|---|---|
| `src/Conductor.Core/Orchestration/SessionEvidence.cs` | `SessionEvidence` (the taxonomy as data) + `AdvisoryEvidence` (KS4.5's row) |
| `src/Conductor.Core/Orchestration/VerdictDecision.cs` | `VerdictDecision`, `StallBackoffPlan`, `AttemptEffect` |
| `src/Conductor.Core/Orchestration/VerdictDisposition.cs` | the twelve dispositions |
| `src/Conductor.Core/Orchestration/SessionVerdict.cs` | `SessionVerdict.Decide` — the function |
| `src/Conductor.Core/Orchestration/VerdictEngine.Evaluate.cs` | the impure half: gather → decide → apply |
| `tests/Conductor.Tests/KS6_4PureVerdictTests.cs` | 34 tests, none of which builds a run |

`VerdictEngine.Signals.cs` is gone — its two readings (`NoteOutsideRepoWrites`,
`IdenticalStallPattern`) were always evidence gathers and now sit with the other gathers. The partial
count therefore stays at **9**, not 10.

### The design problem, and the move that solved it

`EvaluateSessionAsync` could not become one pure call, because it buys evidence in **three rounds** —
the free control rows, then the gate battery, then the tracker diff — and the whole economics of the
method is that each round is only bought when the cheaper rows have not already settled the session.

The resolution: **two of the twelve dispositions are continuations, not verdicts.**
`RunGateBattery` and `ReadWorkEvidence` mean *go and buy this, then ask again*; `HonourBlockUntil`
re-enters the same way when the wait turns out to be stale. The caller asks, is told what evidence it
still lacks, buys it, and asks again with the record widened by `with`. One total function, three call
sites — and *"every early return before `RunGateBattery` is a gate battery this run does not pay for"*
becomes a property a test can name instead of a consequence of statement order.

### Two behaviours that were accidents of statement order, now named fields

1. **`VerdictDecision.ReturnToIdle`.** The stall-branch circuit break returns leaving the run status
   exactly where it was; the delivery-branch circuit break sets `Idle`. Derive "go idle" from the
   disposition and you silently change one of them. Pinned by
   `TheCircuitBreakerOutranksTheBackoffAndLeavesTheRunStatusAlone` and
   `TheBreakerOnTheDeliveryPathDoesReturnToIdleWhereTheStallOneDoesNot`.
2. **`VerdictDecision.Backoff` is nullable.** The stall breaker returns *before* the backoff
   bookkeeping, so it carries no plan, while the resume and advisor paths do.

### One deliberate removal

The newly-blocked park called `NeedsHuman(...)` and then `_saveAndReport()` again. `NeedsHuman`
already saves (`VerdictEngine.cs`, the park path) and nothing mutates between the two calls, so the
second was dropped. This is the only intentional behaviour change in the checkpoint and it is
observable only as one fewer identical write.

---

## 3. A2 — the taxonomy, driven without the loop

`tests/Conductor.Tests/KS6_4PureVerdictTests.cs`, **34 tests, 0 failures**. Not one of them
constructs a `RunContext`, a store, a git repository or an agent. Before the extraction every branch
below could only be reached by standing up all four and running a session to completion, which is why
most had never been asserted at all.

| test | what it pins |
|---|---|
| `KilledByUserPausesTheRunAndGradesNothing` | a kill outranks every failure signal at once |
| `AStalledSessionInsideItsResumeBudgetResumesAndLengthensTheBackoff` | `StallBackoffPlan(3, 30, TouchesUntil: true)` from multiplier 2 |
| `AStalledSessionOutOfResumeBudgetConsultsTheAdvisorDefaultingToRetry` | the resume budget boundary and the advisor default |
| `ATimeoutClearsTheBackoffInstantWhereAStallExtendsIt` | a timeout collapses the multiplier; a stall multiplies it |
| `TheCircuitBreakerOutranksTheBackoffAndLeavesTheRunStatusAlone` | both statement-order behaviours above |
| `TheIdenticalStallParkFiresOnlyWhileTheBreakerIsOff` | the two terminators are alternatives, not both |
| `AWaitRequestOutranksAuditAndVerifyButNotAStall` | SC5.1's precedence, exactly |
| `AStaleWaitFallsThroughToTheOrdinaryVerdict` | the re-entry contract |
| `AnAuditSessionSchedulesThePhaseGateWithoutBuyingABattery` | an audit costs no battery |
| `TheVerifierThresholdIsAnInclusiveFloor` (×3) | 81 pass / **80 pass** / 79 fail |
| `AnUnparseableVerifierScoreIsAnAgentErrorNotAPass` | the failure mode that must not read as success |
| `NothingCheapSettlesADeliverSessionSoTheBatteryIsBought` | the one path that pays |
| `CancellationDuringTheBatteryQueuesAResumeAndNoFix` | a cancelled verification burns no attempt |
| `AnyOneOfTheThreeDeliverySignalsIsDelivery` (×3) | SC4.2/SC4.3/W1.3 — commit, graph claim, stage done |
| `NoneOfTheThreeSignalsIsNoProgressEvenWithEveryGateGreen` | green gates alone are not delivery |
| `AFlippedCheckpointGivesTheStageItsAttemptsBackAndWorkAloneDoesNot` | Advanced/Reset vs Progress/Unchanged |
| `TheRedOutcomeTaxonomyNamesWhichKindOfRed` (×4) | AgentError > GatesRed > NoProgress |
| `ANewlyBlockedCheckpointParksOnlyWhereThePlanAsksForIt` | a park stamps no outcome and spends no attempt |
| `EveryDispositionIsReachableFromTheTable` | no dead disposition, no branch the table has stopped covering |
| `TheDecisionIsAFunctionOfTheEvidenceAlone` | determinism over the whole table |

---

## 4. A3 — KS4.5's seam, stated negatively

A promise in a doc comment is worth nothing here. The invariant is asserted twice.

**Behaviourally** — `AnAdvisoryRowNeverChangesAVerdict` crosses all **19 rows** of the decision table
with four advisory shapes (absent; a glowing judge at 100; a damning judge at 0; two judges
disagreeing) and requires the decision to be *equal* in every one of the 76 combinations. A judge that
could flip a verdict fails this test on its first row.

**Structurally** — `NothingInTheDecisionPathReadsTheAdvisoryRows` counts `AdvisoryRows` across the
four files of the verdict surface and requires **exactly one** occurrence, the declaration, and zero
in `SessionVerdict.cs`. The behavioural proof samples the evidence space; this one is total.

The same file carries `TheVerdictFunctionNamesNothingImpure`, which is what stops the function
drifting back into the loop it was cut out of: no `_ctx`, `RunContext`, `Git.`, `File.`, `Directory.`,
`DateTime`, `Store`, `Process`, `Console`, `Random`, `Environment.`, `async `, `Task<` or `HttpClient`
anywhere on the surface, and no `using` outside `Conductor.Models` / `Conductor.Planning`.

**`WorkingTreeDirty` gets the same treatment** (`ADirtyWorkingTreeIsReportedAndIsNotEvidence`): it is
reported by the verdict-inputs line and written into the fix brief, it has never decided anything, and
if that ever changes it changes there first.

---

## 5. A4 and the measurements — including the one that went the wrong way

**Suite, same build:** `dotnet test Conductor.slnx` → **2941 passed, 0 failed**, 3m16s
(background child `ks64full`, PID 15736). **No existing test was edited**; the commit adds files and
touches `VerdictEngine.cs` and `SessionVerdict.cs` only.

**Live proof:** the verdict path is driven end to end by the repo's fake-agent rigs — real
orchestrator, real git repo, real session, real verdict (`U03GatelessLiveTests`, `P2QaDialLiveTests`,
`P5RolloverLiveTests`, `VerifyLongOutputLiveTests`, `W1ClaimPathTests`, `KS2_6ParkHygieneTests`,
`KS5_4Approve…`, `SC4_1SettleAndRetryTests`, plus the new suite): **121 passed, 0 failed**, 32s. Log:
`KS6.4-live-verdict-path.log`.

**Code metrics**, probed the KS6.3 way — `src/Conductor.Core/CodeMetricsConfig.txt` wound down to
`5 / 40 / 20`, solution project rebuilt, diagnostics read, config restored (the restore is verified:
`git diff` on that file is empty). Raw diagnostics in the log.

| measurement | before | after |
|---|---|---|
| `EvaluateSessionAsync` cyclomatic | **70** | **11** |
| `VerdictEngine.cs` lines | 483 | **171** |
| `VerdictEngine` partial files | 9 | **9** |
| `VerdictEngine` class coupling (CA1506) | 144 | **152** |
| `RunLoop` class coupling (still binding the project) | 182 | 182 |
| `SessionVerdict` class coupling | — | **≤ 20** (never fires at threshold 20) |
| the pure branches: `Triage` / `StallOrTimeout` / `Delivery` | — | 7 / 12 / 14 |

### The finding: CA1506 cannot reward this refactor, and here is why

`VerdictEngine`'s class coupling went **up 8**. That is not a regression in the design and it is not
noise — it is what CA1506 measures. The rule counts *distinct types a type depends on*, so making a
seam **explicit** adds `SessionEvidence`, `VerdictDecision`, `VerdictDisposition`, `AttemptEffect`,
`StallBackoffPlan`, `WorkPass` and `SessionVerdict` to the depender even though the whole decision
*logic* left the class.

The general rule, for whoever writes the next checkpoint against this metric: **an extraction into
new types can only lower CA1506 on the class it came from if the extracted types replace more types
than they add.** Only two moves reliably lower it — deleting a dependency, or moving a whole
responsibility into a type the class then does *not* reference. "Extract a function and the coupling
drops" is false, and this checkpoint is the measurement that says so.

The project budget is untouched either way: `RunLoop` is still 182 and still binds `Conductor.Core`,
exactly as the KS6.3 handoff predicted, and the build is green against the unchanged budgets.

### The ratchet caught this refactor twice, and was right twice

`ArchitectureTests` failed the first wiring commit on both counts, and both were fixed in the code,
not in the baseline: `SessionVerdict.cs` declared 7 types against a ceiling of 3 (split four ways),
and `VerdictEngine.cs` reached 585 lines against a ceiling of 500 (the pipeline moved out, and
`VerdictEngine.Signals.cs` was folded into it so the partial count did not grow). `architecture-baseline.json`
is unchanged — no debt was recorded to make this pass.

---

## 6. What this hands KS4.5

KS4.5 wants a second-model review joining the evidence taxonomy as an advisory row, with the
deterministic signals still deciding. The seam is:

1. Add rows to `SessionEvidence.AdvisoryRows` where the judge's output is gathered — in
   `VerdictEngine.WorkEvidence` for a delivery-time judge, or in `ControlEvidence` for a verify-time
   one. `AdvisoryEvidence(Source, Verdict, Score, Detail)` already carries a structured verdict.
2. Persist them and surface them as evidence; the disposition and outcome come back unchanged.
3. **The "no code path lets a judge score flip a gate verdict" exit criterion is already asserted** —
   `AnAdvisoryRowNeverChangesAVerdict` and `NothingInTheDecisionPathReadsTheAdvisoryRows` are that
   test, written before the judge exists. KS4.5 should extend the advisory variants in the first and
   leave the second exactly as it is; if the source rule ever needs relaxing, the judge has reached
   the decision path and that is the bug.

`Decide` never needs to change for KS4.5. That was the point.
