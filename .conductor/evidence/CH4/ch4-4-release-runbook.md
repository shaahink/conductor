# CH4.4 — the runbook becomes the verb's output

**Date** 2026-08-27 · **Branch** `feat/charkh` · **Verb** `conductor release runbook`
**Generated artifacts** [`ch4-4-charkh-runbook.md`](ch4-4-charkh-runbook.md) (this repo, this era)
and [`ch4-4-rig-runbook.md`](ch4-4-rig-runbook.md) (the scratch rig, post-close)

---

## 1. What is being replaced

`.conductor/evidence/KS12/ks12-3-owner-runbook.md` and its successor
`.conductor/evidence/DV7/dv7-3-owner-runbook.md` were hand-written a fortnight apart, in the same
shape, by two sessions. The second one's **first finding was that the first had not been carried
out**: `master` was fast-forwarded to `feat/karvansara-edge` and then nothing else in that runbook
happened — no tag, no CHANGELOG rename, no doc move, no backfill. Six of seven acts, unperformed,
for an entire era, unnoticed.

That is not a quality problem with the writing. DV7.3 is the best version of the artifact this
project has produced. It is a structural problem: **a document cannot notice an act it never
mentioned, and cannot know whether the acts it does mention were done.**

CH4.1 and CH4.2 had already moved the substance into code — six measured preconditions
(`ReleasePreflight`), nine acts split into four mechanical and five owner-only
(`ReleasePerform.MechanicalOrder` / `OwnerOrder`). What was missing was the projection.

## 2. The verb

```
conductor release runbook [--tag 0.6.0] [--base master] [--branch feat/x] [--out PATH]
```

A third sibling of `preflight` and `perform`. It calls **the same two entry points those verbs
call** — `MeasureAsync` for the checks and `RunActsAsync(dryRun: true)` for the acts — and renders
the result as markdown. Exits with the preflight's own code. stdout by default; `--out` writes the
file and says where.

**It performs nothing, and — unlike `perform` — it does not refuse a live run.** `perform` stops
dead while a conductor holds the plan's state directory, because it rewrites the CHANGELOG and the
plan itself. This verb only measures and renders, and mid-run is exactly when the owner wants to
read what the close will involve. Refusing there would be caution costing the reader the one thing
they came for.

`ReleaseRunbook.Render(RunbookFacts)` in `src/Conductor.Core/Release/ReleaseRunbook.cs` is **pure**:
no clock, no git, no network. The timestamp is an input, so the same facts render byte-identical
output and a regenerated runbook diffs to exactly what changed in the tree.

## 3. The property that replaces the hand-written document

Ten facts in `tests/Conductor.Tests/CH4_4ReleaseRunbookTests.cs`. Two of them are the point, and
both derive the engine's vocabulary **by reflection over the const fields themselves** rather than
from a list written next to them:

| test | asserts |
| --- | --- |
| `EveryActTheEngineDeclaresIsInOneOfTheTwoOrders` | the vocabulary tripwire, before any rendering: an act the engine declares but never orders is one no verb performs and no document mentions |
| `TheRunbookGivesEveryDeclaredActItsOwnSection` | every declared act has its own `### <name> —` heading — found by heading, because "tag" and "merge" are ordinary English and would pass a substring test by accident |
| `TheRunbookGivesEveryDeclaredPreconditionItsOwnRow` | the same for the six measured lines |
| `NoOwnerActIsEverRenderedAsDoneOrAsNothing` | KS12.3's failure mode as an assertion — an owner act is `**YOURS**` on every run, and the words `**done**` and `**already true**` appear nowhere |
| `TheOwnerActsCarryTheCommandsTheOwnerTypes` | the invocations are IN the document, not left for the reader to find |
| `AnUnnamedReleaseKeepsThePlaceholderAndSaysTheNumberIsYours` | no tag ⇒ `--tag <x.y.z>` stays a hole; the version is never invented |
| `TheSameFactsRenderTheSameBytes` | determinism — the timestamp is an input, not a clock reading |
| `AMeasuredHeadlineCarryingAPipeCannotBreakTheTable` | a measured headline is whatever the machine said; one pipe would silently eat the column after it and the table would still *look* fine |
| `ASentenceThatMerelyOpensWithACommandIsNotCodeSpanned` | found on the rig, fixed, then pinned (§5) |
| `TheDocumentSaysItPerformedNothingAndWhatExitTwoMeans` | says both, in the reader's words |

**Negative control.** A tenth act const added to `ReleasePerform` and wired to nothing:

```
$ # public const string AnnounceAct = "announce";
[FAIL] CH4_4ReleaseRunbookTests.EveryActTheEngineDeclaresIsInOneOfTheTwoOrders
       Assert.Equal() Failure: HashSets differ
[FAIL] CH4_4ReleaseRunbookTests.TheRunbookGivesEveryDeclaredActItsOwnSection
       Assert.Contains() Failure: Sub-string not found

Failed!  - Failed: 2, Passed: 7, Skipped: 0, Total: 9
```

Reverted, and the four CH4 classes green together:

```
$ dotnet test Conductor.slnx --no-build --filter "CH4_1|CH4_2|CH4_3RetireScope|CH4_4ReleaseRunbook"
Passed!  - Failed: 0, Passed: 51, Skipped: 0, Total: 51
```

## 4. Live — the scratch rig

CH4.2's rig, under the temp directory, with its own plan, its own repository and its own state
(`C:/Users/shahi/AppData/Local/Temp/ch42-rig`, plan `plans/aban/core.plan.json`). CH4.2 already
performed its four mechanical acts there, so this is what a **closed** era looks like:

```
$ dotnet run --project src/Conductor -- release runbook -p <rig>/plans/aban/core.plan.json \
      --tag 0.1.0 --out <temp>/ch44-rig-runbook.md
runbook written C:\Users\shahi\AppData\Local\Temp\ch44-rig-runbook.md (6 preconditions, 9 acts)
NOT READY - 2 of 6 red: processes, courier; 1 waiting on the owner: backfill
nothing was merged, tagged, moved, installed or pushed.
exit=1
```

151 lines, captured as [`ch4-4-rig-runbook.md`](ch4-4-rig-runbook.md). All four mechanical acts read
`**already true**` with their idempotence reasons; all five owner acts read `**YOURS**` — which is
the whole bar, on a rig where the mechanical work is genuinely finished:

```
### docmove — **already true**
nothing left to move - the plan already points into the record

### version — **YOURS**
the release is named 0.1.0 because you said so
- MinVer derives a build id, not a release name; nothing in this repo can decide which number an era is
```

The two red preconditions are correct, not a defect: a conductor is live on this machine and the
real courier owns its token, so a binary swap is unsafe *right now*.

## 5. What the rig found that reading could not

The generated rig document contained this line, wrapped as a code span:

```
- `tools/install.ps1 stops the courier at step 0 and puts it back on the new engine - re-check 'conductor courier status' after the reinstall`
```

A *sentence* that merely opens with a command token. The renderer's command heuristic was
first-token-only, so it code-spanned a whole paragraph and made it unreadable. Two further
conditions settle it, and both are now pinned by
`ASentenceThatMerelyOpensWithACommandIsNotCodeSpanned`:

- a line already carrying a **backtick** was written as markdown by whoever wrote the act, and is
  left exactly as it is rather than re-marked-up around its own spans;
- a line long enough to be a sentence (over 90 characters) is a sentence.

## 6. Live — this repository, this era

The artifact the checkpoint is actually for. Read-only throughout: the store is opened through
`RunArchive`, `RunActsAsync` performs nothing in dry run, and the git probes only read.

```
$ dotnet run --project src/Conductor -- release runbook -p plans/charkh/core.plan.json \
      --out .conductor/evidence/CH4/ch4-4-charkh-runbook.md
runbook written ... (6 preconditions, 9 acts)
NOT READY - 2 of 6 red: merge, processes; 2 waiting on the owner: changelog, backfill
exit=1
```

[`ch4-4-charkh-runbook.md`](ch4-4-charkh-runbook.md) — the DV7.3 artifact, generated. It carries the
measured branch state (`feat/charkh` 25 ahead of `master`, working tree dirty, local `master` 9
behind `origin/master` with all 9 already contained), the CHANGELOG sections that exist, the four
pids that make a binary swap unsafe with `CONDUCTOR_PID` named among them, the courier verdict, the
runs owed a GitHub record with their backfill commands, and the order to do it in:

```
## 4. The order

1. `conductor release perform --tag <x.y.z> --yes` — changelog, merge, tag, docmove
2. **version** — the release has no name yet
3. **split** — one release or two is a call about what the world reads, not about the tree
4. **corpus** — 3 run(s) have no GitHub record
5. **reinstall** — the reinstall cannot happen yet - a conductor is live on this machine
6. **publish** — pushing is what makes any of this public  ·  `git push origin master`
```

This is what CH5.2 closes the era with.

## 7. A finding the generated document surfaced — bug #91

The `corpus` act lists **three** runs owed a GitHub record and **not** the Karvansara edge run —
the one run CH4.3 says is owed one. The runbook does name it, in the `backfill` precondition detail:

```
- 9491891fe700 Karvansara edge - gates that can't be gamed, and the courier - still needs_human;
  its own backfill is the closing act, not something owed yet
```

`ReleasePreflight` treats a run as in-flight via `RunLiveness.IsStillGoing`, which counts
`needs_human` as still going. The edge run has been parked since 2026-08-18 and is not going to
write its own closing backfill, so classifying it as in-flight means the runbook never hands the
owner that command. Filed as **bug #91** rather than fixed here: `RunLiveness.IsStillGoing` is a
shared predicate used across the engine, and changing its meaning is not a rendering change.

It is worth saying plainly that the generated document found this and the two hand-written ones did
not — because it lists every run and says why each is or is not owed, which is what a projection
does and a summary does not.

## 8. Files

- `src/Conductor.Core/Release/ReleaseRunbook.cs` — `Render(RunbookFacts)`, pure, and `RunbookFacts`
- `src/Conductor/Commands/ReleaseCommand.Runbook.cs` — `RunbookAsync`, stdout or `--out`
- `src/Conductor/Commands/ReleaseCommand.cs` — the verb, the `--out` option, the help
- `tests/Conductor.Tests/CH4_4ReleaseRunbookTests.cs` — ten facts, two of them by reflection
