# KS4.5 — Judge as evidence, never verdict

Session #19, 2026-08-19, branch `feat/karvansara-edge`, commit `546a092`.

Exit criterion from the plan (section 6, KS4.5): *"Judge disagreement recorded as evidence; no code
path lets a judge score flip a gate verdict."*

## 1. What was built

| Piece | File |
|---|---|
| The `judge` plan block (off by default, 6 keys, unknown keys refused by name) | `src/Conductor.Core/Models/JudgeConfig.cs` |
| Plan-load refusals for both model-consult blocks | `src/Conductor.Core/Models/PlanConfig.Consults.cs:18` (judge), `:57` (advisor, moved) |
| The review type, its vocabulary and the agreement arithmetic | `src/Conductor.Core/JudgeReview.cs` |
| The parser and the spawn | `src/Conductor.Core/Judge.cs` |
| Shared balanced-JSON scan (Verifier + Judge) | `src/Conductor.Core/JsonScan.cs` |
| The engine limb: consult, log, write the artifact, return the row | `src/Conductor.Core/Orchestration/VerdictEngine.Judge.cs` |
| The one call site, after the decision | `src/Conductor.Core/Orchestration/VerdictEngine.Evaluate.cs:168` |
| Evidence registration with its own source | `src/Conductor.Core/Orchestration/RunLoop.Evidence.cs:60`, `src/Conductor.Core/Evidence/EvidenceArtifact.cs` (`JudgeSource`) |
| The prompt | `PromptBuilder.Judge` + built-in `judge.md` (`PromptBuilder.BuiltIns.cs`) |
| The bill | `SpendCategory.Judge` |
| The docs the ratchet required | `docs/plan-config.md` — `## judge` section |

## 2. The claim that matters, and how it is proved

**No code path lets a judge score flip a gate verdict.** Three independent proofs, because the
absence of a feature is exactly the thing a comment cannot establish.

### 2a. The order, read off the engine's own source
`VerdictEngine.Evaluate.cs`: the last `SessionVerdict.Decide(` is at line 162; the single
`JudgeSessionAsync(` call is at line 168. The judge is handed a `VerdictDecision` that already
exists and returns only a `SessionEvidence`; the decision is never recomputed.
`KS4_5JudgeTests.TheJudgeIsConsultedAfterEveryDecisionAndDecidesNothing` asserts that index
ordering, asserts the call is unique, and asserts `VerdictEngine.Judge.cs` never names
`SessionVerdict.Decide`.

### 2b. The source rule over the decision path
`KS4_5JudgeTests.TheDecisionPathNeverNamesTheJudge` — none of `SessionVerdict.cs`,
`SessionEvidence.cs`, `VerdictDecision.cs`, `VerdictDisposition.cs` contains the string `Judge`
(comments stripped). This sits beside KS6.4's existing rule that `AdvisoryRows` is named exactly
once across that surface — the declaration — and never in `SessionVerdict.cs`.

### 2c. Live, through the real orchestrator (`KS4_5JudgeHarnessTests`)
Each leg is a fresh temp repo, a real `Orchestrator.RunAsync`, a fake agent and a fake judge CLI.

| Leg | Gate | Judge says | Outcome |
|---|---|---|---|
| control | pass | (no judge block) | not GatesRed |
| hostile | pass | `fail`, score **0** | **identical to the control leg**, same `NewlyDone` |
| control | fail | (no judge block) | GatesRed |
| flattering | fail | `pass`, score **100** | **GatesRed** |

The "with judge" outcome is compared to a real no-judge baseline run of the same rig, not to an
expectation. Both hostile legs record `"agreement": "Disagrees"` in the artifact.

## 3. Disagreement is a recorded field, not prose

`<stateDir>/judge/<stage>-session-NNN.json` carries the review verbatim plus the measurement it was
compared against:

```
kind, note ("ADVISORY ONLY … No code path lets it change a gate result…"),
session, stage, judge, verdict, score, summary, findings,
agreement: Agrees | Disagrees | Inconclusive,
deterministic: { gatesGreen, disposition, outcome, workCommits, newlyDone }
```

`JudgeReview.Against(bool deterministicGreen)` is pure and one-way: it reads the measurement and
reports on the JUDGE. Ten cases pinned in `AgreementIsMeasuredAgainstTheDeterministicSignal`,
including the hedge (`concerns` → `Inconclusive`) and an unrecognised word, which is never guessed
into an opinion.

## 4. The taxonomy join

`TheReviewJoinsTheEvidenceTaxonomyWithItsOwnSource` runs the rig live and asserts the registered
artifact carries `Source == "judge"`, the session number, **no checkpoint id** (an opinion is not a
claim), and non-zero bytes — with KS4.4's `attempt` artifact registered beside it, so the two kinds
are distinguishable without opening either. The second row is the in-memory `AdvisoryEvidence` on
`SessionEvidence.AdvisoryRows` (KS6.4's seam, until now written by nobody), reported through
`VerdictEngine.AdvisoryNote` as a line that names itself: *"advisory (recorded, NOT part of the
verdict — the gates above decided)"*. It is silent when no judge ran, so every existing run's log is
byte-identical.

## 5. Failure modes, all of which cost the session nothing

- no `judge` block / `enabled:false` → never spawned (`JudgeReply.None`)
- empty `args` → refused out loud rather than spawned into a six-minute stdin wait (SC3.4's lesson)
- unparseable output → recorded as unavailable, **no artifact, no row**, session graded as if no
  judge existed (`AnUnreadableJudgeCostsTheSessionNothing`, live)
- timeout / exception → same, and the bill is still recorded (KS5.2's rule)
- artifact unwritable → the row survives, the artifact does not; never the verdict

Holdout safety (KS4.1): the judge is shown `rec.GateSummary`, which is safe because the redaction is
structural — a holdout gate leaves `GateRunner` already anonymous, so nothing composed from a
`GateResult` can name one.

## 6. Test run

```
dotnet test Conductor.slnx --filter "FullyQualifiedName~KS4_5"
Passed!  - Failed: 0, Passed: 42, Skipped: 0, Total: 42, Duration: 15 s
```

Neighbouring surfaces re-run green after this change (299 tests): KS6_4, SF6_3, KS3_1, HarnessTests,
Doctor, Architecture, SF7_1 docs-match-reality, Verifier.

Four ratchets bound the shape and each was obeyed rather than moved:
`Judge.cs` split at the 3-types-per-file ceiling; `PlanConfig.cs` back under 500 lines by moving both
consult validators to `PlanConfig.Consults.cs`; `judge.md` added to doctor's prompt matrix (without
it `{diff}` and `{focus}` were unresolvable by construction and a scaffolded templates dir was
doctor-red on a file the operator never touched); `docs/plan-config.md` documents the block because
`SF7_1DocsMatchRealityTests` demanded it.

## 7. Known red, pre-existing, NOT introduced here

`tools/gates/analyzer-debt.ps1` fails on this branch: `pragma-src now=33 bar=31`. Both new pragmas
came from KS4.4 (`05696d4`) — `AttemptWorktree.cs:98` and `WorktreeDrop.cs:110`, each justified on
its line. It is not in this plan's gate battery (build + test only), which is why KS4.4 was confirmed
with it red. KS4.5 adds **zero**: the one MA0045 this checkpoint needed became an async read instead.
Filed as bug #60.
