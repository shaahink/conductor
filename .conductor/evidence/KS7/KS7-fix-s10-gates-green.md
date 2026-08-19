# KS7 fix session #10 — the nine reds, and what each one actually was

Conductor ran the `engine-full` battery independently after session #9 and it came back RED.
This session reproduced the failure set once, fixed six root causes, and re-ran the battery.

## The failure set, triaged from ONE run

Session #9's report showed five failures; the block was truncated, so the complete set was taken
from a single background run rather than guessed at:

`.conductor/bg-logs/full-suite-triage-20260819-001252351.log`

    Failed!  - Failed: 9, Passed: 2869, Skipped: 0, Total: 2878, Duration: 3 m 23 s

| # | Test | Root cause |
|---|---|---|
| 1 | `B11_2DoctorAndCompletionTests.Completion_ContainsAllRegisteredVerbs_Exhaustive` | `otel` registered, not in `CompletionCommand.Verbs` |
| 2 | `K7_2DocsVerbCoverageTests.CliReference_NamesEveryShippedVerb` | `otel` absent from `docs/cli.md` |
| 3 | `SF7_1…TheOperatingGuideFullCommandReferenceNamesEveryShippedVerb` | `otel` absent from `docs/operating.md` §2 |
| 4 | `SF7_1…TheCompletionListTheCliReferenceAndTheOperatingGuideAgreeOnTheVerbSet` | the same one verb, third bar |
| 5 | `SF7_1…PlanConfigDocDocumentsEveryKeyThePlanSchemaDeclares` | 5 new plan keys undocumented |
| 6 | `SF7_1…DeletingOneDocumentedRowMakesTheDerivationNameThatExactKey` | same 5 keys, the pin-proving-the-pin |
| 7 | `SF7_1…NothingTheBacklogCallsStillOpenHasQuietlyBeenBuilt` | backlog still calls the two batteries unbuilt |
| 8 | `SF6_1TemplateLessonsTests.TheLessonsFitTheCommandLineBudgetEvenOnAMultiRepoPlan` | deliver 7942 / fix 7909 over the 7900 argv budget |
| 9 | `KS3_4PreflightTests.ThePreflightVerbExitsZeroAndSaysReadyOnACleanPlan` | **this session's own artifact** — see below |

Not one of them was the repo's known flake. Every duration matched the last green run, and the
failures are all *consequences of KS7.3–KS7.5 landing work without landing its record*.

## 1-4. One verb, four bars

`OtelCommand` was registered in `Program.cs` at KS7.3 and nothing else. Four separate parity tests
read the shipped verb list **off `Program.cs`** — precisely so a verb cannot be reachable and
undiscoverable at once — so a single unregistered verb is four reds.

Fixed in three places, all of them the record rather than the code:

* `src/Conductor/Commands/CompletionCommand.cs` — `otel` into the shared `Verbs` constant (both
  generators read it, so tab-complete gains it in bash and PowerShell together).
* `docs/cli.md` — a row in **Token and money**, plus two example lines. The paragraph that said
  "All three read the machine-wide run catalogue" was corrected rather than left to rot: it names
  `budget`, `money` and `spend` now, and says what `otel` does instead (`--dry-run`/`--out` render
  the exact OTLP payload with no collector up).
* `docs/operating.md` §2 — the full-reference row, with the flags **verified against
  `OtelCommand`'s own `[CommandOption]` attributes** rather than transcribed from the description.

## 5-6. Five plan keys with no rows

`PlanKeySchema` walks the type graph, so the expectation is derived: adding a property to a config
class *is* a documentation obligation, and two tests enforce it.

| key | shipped by | documented as |
|---|---|---|
| `agent.forkArgs` | KS7.4 | table row + a worked `jsonc` block |
| `agent.forkKinds` | KS7.4 | table row + the same block |
| `batteries.repoMap` | KS7.5 | table row, default `false`, with *why* it is opt-in |
| `batteries.definitionOfDone` | KS7.5 | table row, default `false`, with the argv arithmetic |
| `batteries.repoMapMaxEntries` | KS7.5 | table row, default `12` |

The fork prose carries the measurement KS7.4 actually took, not a restatement of the flags: the
carried conversation arrives as a cache **read** (30,098 read / 0 write on a 30k base), a fork
measured 0.15% larger and a hundredth of a cent cheaper than resuming, `--fork-session` composes
with `--session-id` so conductor keeps id control — and the failure mode that makes it opt-in is
stated: a fork resumes a transcript **on disk**, and a pruned base session cannot be forked.

## 7. The backlog listed finished work

`SF7_1DocsMatchRealityTests` keeps two arrays: features the page calls shipped (whose symbol must
exist) and features it calls open (whose symbol must **not**). `RepoMapBattery` and
`DefinitionOfDoneBattery` had been on the open list since SF7.1 and were re-verified absent twice.
They landed at KS7.5, so the test went red exactly as designed, and the edit it demands is the one
made here — off the open list, onto the shipped list, in **both** the doc and the test:

* `docs/dev/NEXT-FEATURES.md`: the "two batteries that were designed and never built" bullet is
  gone from *Still open*; a shipped-record bullet names both classes, the file they live in, the
  two flags that gate them and the cap that bounds the map. The 2026-08-05 re-check note is not
  falsified — it is dated ("stood as written **on that date**") and a 2026-08-19 note says which
  two of its four entries closed and that the other two were re-grepped and still stand.
* the test: both symbols moved from `UnbuiltSymbols` into `ShippedFeatures`, which is a *stronger*
  assertion — the shipped half demands the symbol exists **and** that the page still names it.

## 8. The argv budget — a real ceiling, missed by measuring the wrong prompt

Session #9 measured the **single-repo** prompt (7,545 chars via doctor's argv lint) and never
rendered the **multi-repo** one, which carries `MultiRepoSection` and is ~400 chars bigger. The
budget test renders both, and both were over:

    before:  deliver = 7942   fix = 7909      (budget 7900, cliff ~8191)
    after:   deliver = 7808   fix = 7775      (92 chars of margin)

The "after" numbers are measured, not derived: the budget constant was temporarily set to 1 so the
test would print its own measurement, then restored (`SF6_1TemplateLessonsTests.cs:172`).

**The budget was not raised.** It was paid for the way the test's own failure message demands —
by cutting old prose. Five paragraphs of `ToolContract` were compressed and every phrase pinned
elsewhere in the suite survives, verified by running those pins:

* `PID {ProcessId}`, `CONDUCTOR_PID`, `locked by: conductor (PID)`, `Another repo's run may share
  this machine` (`SF0_3PidsAndBackgroundWorkTests`)
* `THIS IS THE ONLY WAY TO REPORT PROGRESS`, `There is no second mechanism`, `handoff block`
  (`W2OnePromptTests`)
* `delegate the wide reads`, `SUBAGENT` (`KS7_5ContextEconomicsTests`)
* `conductor bg` (`PromptBuilderTests`)

No instruction was dropped — the delegation block still teaches all four of its rules, the pid
block still names the hazard and the second run on the machine, the claim block still says the
command is the claim.

## 9. The preflight red was this session's own hand

    ✗ rebuild   …conductor.exe (2026-08-19T00:13:52Z) is OLDER than the sources that build it
                in C:\code\conductor: src/Conductor/Commands/CompletionCommand.cs was written
                36s after the engine image

That is the preflight leg working correctly. This session edited `src/` while a background full
suite was running, so the test image genuinely predated the source. It is not a defect and it is
not the repo's orphaned-test-host flake; it disappears the moment the suite builds after the last
edit. Recorded in the ledger so the next session does not chase it.

## Verification

Narrow set first — the nine failures' classes plus every class that pins a phrase in the
compressed tools block:

    .conductor/bg-logs/narrow-verify-20260819-002647764.log
    Passed!  - Failed: 0, Passed: 119, Skipped: 0, Total: 119, Duration: 5 s

Then one full battery run, after the last edit:

    .conductor/bg-logs/full-suite-green-20260819-002850258.log

## What was NOT done

No gate was weakened. No test was deleted, skipped or relaxed; the two test edits both *tighten*:
the backlog symbols moved to the half with the stronger obligation, and the digest golden gained a
second assertion pinning the `via hook` variant alongside `via transcript`.
