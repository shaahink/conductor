# KS10.2 — the docs pins, and the proof they bite

Session #24, 2026-08-15. Repo `263c7f8` on `feat/karvansara` at capture time.
Raw transcript: `ks10-2-docs-pin.raw.txt` (this file is its reading).

Filter used throughout:

```
dotnet test Conductor.slnx --filter "SF7_1DocsMatchRealityTests|K7_2DocsVerbCoverageTests|K7_2ReadmeFrontPageTests|B11_2Tests"
```

## What was extended

`SF7_1DocsMatchRealityTests` gains two partials. Both derive their expectation from shipped code —
neither can pass by agreeing with a list somebody typed.

| new test | derives from | catches |
|---|---|---|
| `EveryFencedReadmeCommandNamesAVerbTheEngineRegisters` | `AddCommand<T>("verb")` scan of `Program.cs` | README quoting a verb that was removed or hidden |
| `EveryFlagTheReadmeWritesIsDeclaredByTheCommandItIsWrittenOn` | **reflection** over the settings type each command binds, off `conductor.dll` next to the tests | a renamed or dropped option — which under `UseStrictParsing` exits non-zero for whoever copies the line |
| `TheFirstCommandTheReadmeOffersIsBareConductorAndProgramRewritesItToTheHub` | `Program.cs`'s `argv.Length == 0 ? ["hub"]` rewrite | KS2.1 being undone while the front page still tells a reader to type `conductor` |
| `TheOperatingGuideFullCommandReferenceNamesEveryShippedVerb` | same verb scan, scoped to §2 | operating.md calling §2 "full" when it is not |
| `TheCliReferenceDocumentsNoVerbTheEngineHasStoppedShipping` | same verb scan, converse direction | a **deleted** verb keeping its cli.md row forever — K7.2 only ever asks what the doc is *missing* |
| `TheCompletionListTheCliReferenceAndTheOperatingGuideAgreeOnTheVerbSet` | `CompletionCommand.Verbs` | a verb you can only discover by pressing TAB |
| `TheControlSectionNamesEveryIntentTheEngineCanDispatch` | the `ControlAction` enum | a control intent the operator was never told about — same shape as the `WatchReason` wake table |

## What they found, before any seeding

Each of these is a doc fixed, never a test relaxed.

1. **README pointed at a closed era and at files that are not there.** The self-drive blockquote named
   `plans/karvan/CORE-TRACKER.md` and promised `SARBAN-CORE-TRACKER.md` / `SARBAN-FACE-TRACKER.md` at
   the repo root. `ls SARBAN-*.md` → *No such file or directory*; they are in
   `docs/history/archive/trackers/`.
2. **operating.md §2 is titled "Full command reference" and was not full.** Missing: `journey`,
   `heartbeat`, `demo`, `version`, `update`, and every machine-level verb Karvan and Karvansara
   added — `history`, `ps`, `catalogue`, `run close`, `run adopt`, `face --archive`, `budget`,
   `money`, `spend`, `github sync`, `watches`, `plan new`. 46 verbs ship; 5 + the whole across-runs
   surface were absent.
3. **`plan reload` queues a live control intent and the control section never named it.** The
   `ControlAction`-derived test went red on its first run:

   ```
   docs/operating.md's control section does not name `plan reload`, which is how
   ControlAction.ReloadPlan is reached from a terminal.
   ```

## Baseline

```
Passed!  - Failed: 0, Passed: 36, Skipped: 0, Total: 36
```

## Seeded stale docs — four seeds, each reverted

Every seed was applied, measured, then reverted with `git checkout --`; the transcript records
`0 modified path(s) remain` after each.

### Seed 1 — the contract's own example: `docs/cli.md` stops naming `github`

`sed -i 's/`github/`gitxhub/g' docs/cli.md` → **3 red**

```
K7_2DocsVerbCoverageTests.CliReference_NamesEveryShippedVerb
  docs/cli.md does not name these shipped verbs: github

SF7_1DocsMatchRealityTests.TheCompletionListTheCliReferenceAndTheOperatingGuideAgreeOnTheVerbSet
  these verbs tab-complete but are not on both doc pages (cli.md and operating.md §2): github.

SF7_1DocsMatchRealityTests.TheCliReferenceDocumentsNoVerbTheEngineHasStoppedShipping
  docs/cli.md has reference rows for verbs Program.cs does not register: gitxhub
```

The third one is the new direction working: renaming a verb in the doc is caught *twice over* — the
real verb goes undocumented **and** a verb the engine does not ship acquires a reference row.

### Seed 2 — README quotes a flag `run` does not declare

`conductor run --once` → `conductor run --onces` → **1 red**, with the option list read live off
`RunCommand`'s settings type:

```
SF7_1DocsMatchRealityTests.EveryFlagTheReadmeWritesIsDeclaredByTheCommandItIsWrittenOn
  README.md writes options the command does not declare. Strict parsing means each of these
  EXITS NON-ZERO when a reader copies it:
  `conductor run --onces` -> run declares no --onces (it declares: --detach --dry-run --headless
  --max-sessions --no-control-plane --no-face --once --paused --plan --port -p)
```

That option list is not in the test. It is reflected out of the built assembly, which is why the
message can name what the command *does* accept.

### Seed 3 — operating.md §2 loses the row for a shipped verb

`sed -i '/^| `watches /d' docs/operating.md` → **2 red**

```
TheOperatingGuideFullCommandReferenceNamesEveryShippedVerb
  docs/operating.md's "Full command reference" does not name these shipped verbs: watches
TheCompletionListTheCliReferenceAndTheOperatingGuideAgreeOnTheVerbSet
  these verbs tab-complete but are not on both doc pages: watches.
```

### Seed 4 — cli.md documents a verb the engine does not ship

Appending `| \`teleport\` | seeded ghost verb … |` → **1 red**

```
TheCliReferenceDocumentsNoVerbTheEngineHasStoppedShipping
  docs/cli.md has reference rows for verbs Program.cs does not register: teleport - a deleted
  verb keeps its documentation forever unless something asks this question.
```

## Green again, all seeds reverted

```
Passed!  - Failed: 0, Passed: 36, Skipped: 0, Total: 36
```

## The trap the previous session left, and how it was answered

Session #23's handoff: *"a doc pin written as `Contains()` over a whole file is worthless. Mine
PASSED against a doc whose prescription bullet still said 0.70."*

Every assertion added here is scoped to the thing that gives the instruction, not to the file:
§2 of operating.md is sliced out before the search (`## 2.` → `## 3.`), the control section is sliced
(`### Control a LIVE run` → the next `###`), the completion set is sliced out of the `Verbs` constant,
and README commands are matched inside fenced blocks only. Seed 3 is the direct demonstration: the
word `watches` still appears elsewhere on the page, and the test went red anyway.
