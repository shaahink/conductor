# KS9 — the battery goes green, and the three reds were one commit's collateral

Session #21, fix attempt 2/6 on stage KS9. Conductor's independent battery came back RED after
session #20 with four failures in `engine-full`. None of them was a defect in the mirror; all of
them were bookkeeping KS9.2 shipped without moving. This artifact is the measurement, not a claim.

## What the battery said (its output, verbatim in the session prompt)

```
Failed Conductor.Tests.K4_1ContextWindowTests.A_v11_database_migrates_to_v12_and_keeps_its_sessions
  Expected: 13   Actual: 14
Failed Conductor.Tests.K3_3ProvenanceTests.A_v10_database_migrates_to_v11_and_keeps_its_rows
  Expected: 13   Actual: 14
Failed Conductor.Tests.RunDbTests.Schema_version_is_thirteen
  Expected: 13   Actual: 14
Failed Conductor.Tests.ArchitectureTests.NoFileGrowsPastItsLineCeilingOrItsRecordedDebt
  RunContext.cs   514 lines — over the 500-line ceiling. Split it by responsibility.

Failed!  - Failed: 4, Passed: 2642, Skipped: 0, Total: 2646
```

## Red 1 — three tests, one fact: the schema really is at v14

`MigrationRunner.CurrentVersion` is 14. It is 14 because KS9.2 added
`src/Conductor.Core/Store/Migrations/v14_github_cursor.sql`, a registered, embedded migration that
creates the two tables the live mirror cannot work without — `github_cursor` (the per-`(run_id,
repo)` high-water mark) and `github_map` (the local "what have I already created there" map that
exists because the REST issues LIST endpoint turned out to be eventually consistent). The migration
is real, it is applied, and the three pins were simply left behind.

Those pins are literals **on purpose**. `RunDbTests` says so in its own comment: reading the number
back from `SqliteRunStore.CurrentSchemaVersion` would assert that a constant equals itself and let a
schema bump land without anyone deciding it should. So the pin is a forcing function, and the honest
response to it is to make the decision out loud rather than to route around it.

The decision, made with the migration in hand: **v14 is correct and the pins move to 14.**

| file | line | was | now |
|---|---|---|---|
| `tests/Conductor.Tests/RunDbTests.cs` | 56 | `Schema_version_is_thirteen`, `Assert.Equal(13L, …)` | `Schema_version_is_fourteen`, `Assert.Equal(14L, …)` |
| `tests/Conductor.Tests/K3_3ProvenanceTests.cs` | 321 | `Assert.Equal(13, CurrentSchemaVersion)` | `Assert.Equal(14, …)` |
| `tests/Conductor.Tests/K4_1ContextWindowTests.cs` | 285 | `Assert.Equal(13, CurrentSchemaVersion)` | `Assert.Equal(14, …)` |

Nothing was weakened: each pin still asserts an exact literal, each still fails on the next
undeclared bump, and each carries a comment naming the migration that moved it. The two migration
tests also keep their independent assertion that the migrated database's own `schema_version` row
equals `CurrentSchemaVersion`, so the upgrade path is still proved, not assumed.

## Red 2 — the ratchet, fixed by splitting, not by raising

`tests/Conductor.Tests/architecture-baseline.json` is `"filesOverLineCeiling": {}` — **empty**. There
is no debt entry to add and `lineCeiling` stays 500; the test's own remarks say raising it is a human
decision and "do not raise it to make your own session pass". So the file had to shrink for real.

It shrank where the seam already was. Everything the GitHub mirror needs on the context — the
`Mirror` property, `AttachMirror`, `MirrorBoard`, `DetachMirror`, `MirrorFinalPass` — concerns a
destination **outside** the run: one-way, off by default, and read by not one decision the run loop
makes (D-7, ADR 0005). That is a different job from the run's own state, budget and logging, which
is what the rest of `RunContext` is. It moved to a new partial,
`src/Conductor.Core/Orchestration/RunContext.Mirror.cs`, under the same `Type.Aspect.cs` convention
`SqliteRunStore.Github.cs` already uses.

```
src/Conductor.Core/Orchestration/RunContext.cs          514 -> 459 lines   (ceiling 500)
src/Conductor.Core/Orchestration/RunContext.Mirror.cs   new, 76 lines
tests/Conductor.Tests/architecture-baseline.json        unchanged: {} , lineCeiling 500
```

No public surface changed — a partial class is the same class — so no call site moved.

## Red 3 — the one only the full suite could show

The targeted filter (10/10 green,
`.conductor/bg-logs/ks9fix-20260815-025148385.log`) did **not** cover it. Running the whole suite
did, and it was worth the 2m53s:

```
Failed Conductor.Tests.ArchitectureBoundaryTests.TheGithubMirrorIsNeverRegisteredOnTheEventPath
  KS9.2 — the mirror left its lane:
  RunContext.Mirror.cs names GithubMirror directly — boundaries poke it through
  RunContext.MirrorBoard / MirrorFinalPass, which are null-safe and cannot throw.
```

The boundary rule says: under `src/Conductor.Core/Orchestration`, only `RunContext.cs` and
`RunLoop.Plumbing.cs` may name `GithubMirror` — "RunContext is the only door". The allowlist is
**types wearing filenames**, and splitting `RunContext` into two files broke the spelling while
leaving the rule's meaning untouched: `RunContext.Mirror.cs` *is* `RunContext`. It joins the list,
with a comment saying why, and the set of permitted **types** is exactly what it was. Every other
file under `Orchestration` still has to come through the null-safe pokes.

This is the general lesson and it is now in the ledger: **two separate architecture tests key on file
NAME** — the ratchet's per-file line ceiling and this boundary allowlist — so a split that satisfies
one can break the other. Check both.

## What was measured

| run | scope | result | log |
|---|---|---|---|
| targeted | 3 schema pins + all `ArchitectureTests` | **10 passed, 0 failed**, 315 ms | `.conductor/bg-logs/ks9fix-20260815-025148385.log` |
| full suite | `dotnet test Conductor.slnx` | **2645 passed, 1 failed**, 2 m 53 s — the single failure was red 3, whose fix was not yet in that build | `.conductor/bg-logs/ks9full-20260815-025314359.log` |
| boundary re-run | `ArchitectureBoundaryTests` + `ArchitectureTests` | **19 passed, 0 failed**, 308 ms | `.conductor/bg-logs/ks9bnd-20260815-025650650.log` |

Read together, those three runs cover all 2646 tests with every fix applied. The full run is the
proof for the battery's original four failures — 2645 of 2646 passed with the schema pins and the
ratchet already fixed — and its single remaining failure is the boundary test the third run then
turns green, along with every other architecture assertion in the suite. Nothing is left untested
and nothing is left red.

## What was NOT done, deliberately

- No test deleted, skipped or renamed-away. `Schema_version_is_thirteen` -> `_is_fourteen` is a
  rename in place; the test count is unchanged.
- No ratchet ceiling raised. No entry added to `architecture-baseline.json` — it is still `{}`.
- No expectation relaxed. Every pin is still an exact literal.
- Bug #44 (ratchet gate: 43 analyzer suppressions against a ceiling of 38) is untouched and still
  open. It is not in this battery's failure list, session #20 measured 43 both at `5ff45e3` and at
  HEAD, and this session adds zero suppressions. Raising that ceiling is an owner decision.
