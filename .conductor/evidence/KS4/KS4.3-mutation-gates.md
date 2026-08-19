# KS4.3 — the mutation gate kind

**Claim.** `class: "mutation"` is a real gate class: the gate produces a Stryker report, the ENGINE
computes the mutation score over the files this branch changed, compares it to the plan's threshold,
and a gate that **exits 0** below that bar makes the session red — with the class named, the score
and the bar quoted, and every surviving mutant listed by file and line in the brief the next session
is handed.

Commits: `4d6ad56` (the class), `a27dc51` (the live rig + `tools/mutation-run.ps1`), `b4b3efa`
(the remaining consumers + `docs/plan-config.md`).

---

## Why this class exists, in one paragraph

Every other gate asks *do the tests pass*. When the same agent writes the code and the tests, that
question is answerable without writing a single lie: an assertion on a constant, a mock that returns
the value under test, a test that exercises no branch. All of them run, all of them pass. A mutation
score asks the one question those moves do not survive — break the implementation on purpose, and
does anything go red. It is deterministic, it is not a judgement, and unlike coverage it cannot be
raised by executing a line without asserting on it.

---

## A1 — the plan refuses a mutation gate it cannot enforce

`src/Conductor.Core/Models/GateRules.cs:59,64,71,77` — four load-time refusals, each with a test in
`KS4_3MutationGateTests.APlanIsRefusedWhenTheMutationGateCannotBeEnforced` /
`AMutationGateMayNotAlsoBeAHoldout`:

| refused | why |
|---------|-----|
| unknown or missing `mutation.format` | an unknown class must not project to "standard" in silence — a plan that asked for a mutation gate and got an exit-code gate has been downgraded without being told |
| no `mutation.path` | the engine scores the report the gate wrote, so it has to be told where |
| `threshold` outside `(0, 100]` | zero is the switch-it-off value and reads in a plan diff as a number rather than as a removal; above 100 is a gate that can only ever be red |
| `holdout` **and** `mutation` | this class's output is a list of files and line numbers, which is exactly what a holdout may never say (same reasoning as KS4.2's regression/holdout refusal) |

## A2 — the ENGINE computes the score, and scopes it to the diff

`GateRunner.ApplyMutationClassAsync` (`src/Conductor.Core/GateRunner.cs:292`), reached from the
battery at `:224` — after the SC4.1 retry pass, so a gate that failed and passed on the retry is
scored on the run that counted.

- The changed set is `Git.ChangedFiles(plan.Repo, cfg.BaseRev)` (`src/Conductor.Core/Git.cs:28`),
  filtered to `.cs` by `MutationConfig.IsMutableSource` (`Models/MutationConfig.cs:78`).
- The arithmetic is `MutationScore.Percent` (`src/Conductor.Core/MutationReportReader.cs:32`):
  `(Killed + Timeout) / (Killed + Timeout + Survived + NoCoverage)`.
- The gate's own exit code is **not** the verdict. `tools/mutation-run.ps1` deliberately does not
  pass Stryker's `--break-at`.

**The falsifying test** — `TheRestOfTheRepositoryCannotCarryTheFileThisSessionChanged`: one changed
file with 2 survivors and one untouched file with 18 kills. Whole-report scoring says **90%** and
clears a 60% bar; diff-scoped scoring says **0%** and the battery is red. That gap is the checkpoint.

**NO-COVERAGE counts in the denominator** (`AMutantNothingExecutedCountsAgainstTheScoreRatherThanVanishingFromIt`).
Stryker also publishes a score that omits them, and that is the number a checkpoint adding untested
code would rather be judged by — it *rises* when a mutant is never executed at all.

`Git.ChangedFiles` takes three commands, not one: `diff --name-only <rev>` (rev vs working tree),
`diff --cached <rev>` (staged-only), and `ls-files --others --exclude-standard` — because a
brand-new source file is invisible to `diff`, and that is the exact shape of a file a session just
wrote. An unresolvable base rev returns **empty**, never "everything": promoting a diff-scoped gate
to a whole-repository one blows the budget rather than the verdict, which is the harder failure to
notice (`AnUnresolvableBaseRevYieldsNothingRatherThanEverything`).

## A3 — fail closed, and the one case that is not a failure

`GateClass.UnreadableMutationNotice` (`Models/GateClass.cs:73`).

- **Red**: the branch changed mutable source and the report scores none of it. A stale report, a
  mis-pointed `path`, a `--mutate` glob narrowed past the changed file — all arrive here, and none of
  them differs from a perfect score by exit code.
  (`AReportThatScoresNoneOfTheChangedFilesIsRedNotPerfect`, `AMissingReportIsRedForTheSameReason`)
- **Green, with no finding at all**: the branch changed no `.cs` file. A docs checkpoint has no
  mutants, and reddening it would teach every reader to ignore the class.
  (`ABranchThatChangedNoMutableSourceIsGreenWithNoFindingAtAll`)
- **Not scored**: a gate whose command FAILED. A Stryker run that fell over wrote no report, and
  "0% of nothing" would report an unkilled-mutant problem where the real one is a broken runner.
  (`AFailingGateIsReportedAsAFailingGateAndNotAsAMutationShortfall`)

Never cache-served: `GateConfig.IsClassed` excludes both named classes from the per-gate SHA cache,
because the verdict is computed against the *current* diff and a cached pass at this HEAD would
answer for a different set of changed files.

## A4 — distinct reporting, walked to every surface in the same commit

One line carries the redness — `GateResult.IsGreen` (`GateRunner.cs:77`) asks `HasClassFailure`
(`:54`) — and the *reporting* is deliberately not shared. The glyph is `MUTANTS`, not `FAIL` (the
gate exited 0) and not `REGRESSION` (a reader sent to look for a deleted test when the finding is an
unkilled mutant has been sent to the wrong file).

| surface | file | rendered |
|---------|------|----------|
| fix brief, workflow path | `GateRunner.MutationDetail` (`:646`) | class, score, bar, every survivor |
| fix brief, ordinary session | `GateFailureSpill.Render` | the same block |
| per-gate log line | `GateOrchestrator.OutcomeLine` | `gate mutation: MUTANTS (2s) — …` |
| verdict reason | `SessionVerdict.MutationReason` (`SessionVerdict.cs:254`) | `mutation class (mutation score): …` |
| evidence taxonomy | `MutationEvidence` (`Orchestration/SessionEvidence.cs`) | its own advisory-free row, read by `Decide` |
| `conductor gate` | `GateCommand.cs` | glyph first, then the class's finding |
| `REPORT.md` | `Reporter.cs` | the class's finding, not forty lines of "PASS" |
| circuit-breaker fingerprint | `FailureCircuitBreaker.FailingGates` | class failures now included |

The last four were still asking `!g.Passed` and are fixed in `b4b3efa` — that also repairs the same
blindness for KS4.2's regression class. Filed, not fixed: **bug #58**, `ParseFailingGates` matches
glyphs the summary has never emitted.

### The live proof — the composed prompt read off disk

`KS4_3MutationHarnessTests` (`[Trait("Category","Integration")]`), two real sessions of the real
orchestrator over a real temp repo through a real `PlanConfig.Load`. This is the KS4.2 rule applied
before it could be paid for twice: *a new failure shape must be walked to every renderer, and only a
live rig finds them.*

Session 1 does everything a delivering session does — writes the source, writes tests, the suite
exits 0, claims its checkpoint, commits — and comes back **`GatesRed`** with `mutation:MUTANTS` in
the record and `suite:OK` beside it.

`TheFixSessionIsHandedTheClassByNameTheScoreTheBarAndEveryLivingMutant` reads
`.conductor/logs/<prompt for session 2>` off disk and asserts it contains `MUTANTS`, `EXITED 0`,
`25%` (the score), `60%` (the bar) and `src/Calc.cs:8`, `:9`, `:10` — and **not** `src/Calc.cs:7`,
the one line that session did assert on, nor `PASS-TO-PASS`, because the fix a regression asks for is
the opposite one.

**The falsifier**: the same rig with one line changed — session 1 writes assertions that kill all
four mutants — runs green throughout (`TheSameRigStaysGreenForASessionWhoseTestsCanActuallyFail`).
Without that leg these tests would pass just as well against a gate that fails every first session.

The rig's mutation runner is a stand-in and its docs say so. What is not stand-in: the plan load, the
class, the diff scoping against real git history (the rig tags `rig-base` at setup, because the agent
COMMITS and an uncommitted diff would be empty by the time the battery runs), the arithmetic, the
verdict and both renderers.

## A5 — the era-boundary run, on conductor's own suite

**Status: NOT COMPLETED IN THIS SESSION. The number is owed.** Everything below is measured, and
the honest summary is that dotnet-stryker 4.16.0 was installed, made to run against conductor's own
source and its own 3019-test suite twice, and did not reach a score inside the session's wall clock.

What the two runs DID establish, and none of it was known at the start of the day:

| fact | measured |
|------|----------|
| `dotnet-stryker` cannot share a working tree with a `dotnet test` | run 1 died with `MSB3021`/`MSB3027` — an xunit `testhost` holds `Conductor.Core.dll` and `conductor.dll` under `tests/Conductor.Tests/bin`. It surfaces as "Initial build of targeted project failed", which reads like a Stryker problem and is not. Fix: a separate `git worktree`. |
| running it from the repo root fails differently | `Could not find an assembly reference to a mutable assembly … No project found` — it picks up `Conductor.slnx` and slnx solution mode does not resolve. Run it from `tests/Conductor.Tests` with `--project Conductor.Core.csproj`. |
| `--mutate` on the **command line** was ignored | run 2 was given three `--mutate` globs and its log shows mutants injected into `Reporter.cs` and `SnapshotBuilder.cs` — i.e. all of Conductor.Core. Scope has to go in `stryker-config.json`. |
| the fixed cost before a single mutant is tested | **~18 minutes**: project analysis (~25s) + test-project build (~60s) + the initial run of all 3019 tests. |

That last row is the whole argument for diff scoping, in a number: a whole-repository mutation pass
over Conductor.Core is not a gate this run can afford per session, and the class was built diff-scoped
for exactly that reason.

**What is owed and how to finish it in one background child.** The rig is left in place and ready:

```
git worktree add --detach %TEMP%/ks43-mutation-wt HEAD
# tests/Conductor.Tests/stryker-config.json  (scope goes HERE, not on the CLI)
{ "stryker-config": { "project": "Conductor.Core.csproj",
    "mutate": ["**/MutationReportReader.cs", "**/ReportPath.cs", "**/PassSetExtractor.cs",
               "**/Models/MutationConfig.cs"],
    "reporters": ["json", "progress"], "concurrency": 4 } }
conductor bg start --purpose stryker -- powershell -NoProfile -Command "cd %TEMP%/ks43-mutation-wt/tests/Conductor.Tests; dotnet stryker"
```

Then point a scratch plan's mutation gate at the report it writes and run the battery through the
FRESH build — the plan is already written at `%TEMP%/ks43-proof/mutation.plan.json`, with
`path: "tests/Conductor.Tests/StrykerOutput/*/reports/mutation-report.json"` and
`diffBase: "master"`:

```
dotnet run --project src/Conductor -- gate -p %TEMP%/ks43-proof/mutation.plan.json --full
```

That closes the one assumption in this checkpoint that is still un-measured: that a **real** Stryker
report's `files` keys match `MutationReportReader.SamePath` against repo-relative changed paths. The
reader tolerates absolute, project-relative and `./`-prefixed keys by design
(`AReportKeyAndAChangedPathMatchOnTheirTrailingSegments`), so the expected outcome is that it just
works — but expected is not measured, and this file does not claim it is.

---

## Test run

```
dotnet test Conductor.slnx --no-build --filter "FullyQualifiedName~KS4_3"
  Passed!  - Failed: 0, Passed: 36, Skipped: 0, Total: 36

dotnet test Conductor.slnx --no-build --filter "KS4_|Gate|Verdict|Spill|Reporter|CircuitBreaker|Lane"
  Passed!  - Failed: 0, Passed: 468, Skipped: 0, Total: 468
```

And the whole suite, against the shipped tree (`9707af7`), after the split and the two red fixes:

```
dotnet test Conductor.slnx --no-build
  Passed!  - Failed: 0, Passed: 3019, Skipped: 0, Total: 3019, Duration: 4 m 17 s
```

That is the number the two pre-existing reds were hiding: the same command an hour earlier read
`Failed: 3, Passed: 3016` — the architecture ratchet twice and `K4_1`'s schema pin — and none of the
three was this checkpoint's doing. The suite costs four minutes; a session that only runs a filtered
subset cannot see any of them.

No test was deleted, skipped, or weakened for this checkpoint.
