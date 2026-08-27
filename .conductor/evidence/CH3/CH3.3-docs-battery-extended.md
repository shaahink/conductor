# CH3.3 - the battery covers what Charkh changed, and the three blind spots that found

Measured 2026-08-27, session 4, on `feat/charkh` at `2e280fa`.

## What this era actually added, measured rather than assumed

Diffed `v0.5.0..HEAD` over `src/`:

- **one sub-verb** - `github ci`, with `--repo` and `--branch`. The two-binary diff at CH3.1 shows
  it is the *whole* CLI delta: `conductor github --help` on the released engine lists `sync, sarif`;
  on this tree it lists `sync, sarif, ci`.
- **one runtime artifact** - `<stateDir>/ci-status.json`, written from
  `src/Conductor.Core/Integrations/CiStatus.cs` behind `public const string FileName`.
- **two derived report keys** - `ci-battery` and `ci-verdict` (`CiAgreement.cs:31,34`).
- **no new plan config keys.** The only property added to `PlanConfig` this era is
  `RepoAsWritten`, and it is `[JsonIgnore]` - not a key a plan can set, so `PlanKeySchema` does not
  declare it and `docs/plan-config.md` owes it no row. That is a measurement, not an omission.

`github ci` and its flags were already covered - `SF7_1DocsMatchRealityTests.Subverbs` derives the
sub-verb list from the dispatcher and `.Flags` derives the option list by reflection, and both are
green. The artifact and the placement were not, and that is what this checkpoint is.

## Blind spot 1 - the artifact scan could not see its own assembly

`TrackerDocNamesEveryRuntimeArtifactTheEngineCanWrite` derived its expectation by scanning
**`src/Conductor/`** - the shell - for `Path.Combine(plan.StateDir, "name")`. Two things defeat it:

1. CH1.3 put the writer in `Conductor.Core`, one assembly over.
2. It writes `Path.Combine(plan.StateDir, CiStatus.FileName)`, and a const is not a literal.

So `ci-status.json` shipped undocumented and the bar stayed green. The method also appended two
names **by hand** - which is what a hand-kept list looks like the moment before it misses the third.

Widened to scan `src/`, and to follow a const to its declaration (`StateDir, CiStatus.FileName` to
the file that declares `CiStatus` to `const string FileName = "..."`), it came back with **six**
artifacts the page had never named, across three eras:

    docs/tracker.md does not mention 6 artifact(s) the engine writes under plan.StateDir:
    ci-status.json, cloud, inbox, judge, secrets.local.json, settings.session.json.

All six are now in the runtime-files tree, described from their writers - `GithubCommand.Ci.cs`,
`LaneCoordinator.cs:366`, `TelegramService.Inbound.cs`, `VerdictEngine.Judge.cs:122`,
`GithubIdentity.cs`, `SessionRunner.Mcp.cs:192`. The hand-added `EngineLock.FileName` is now derived
and was dropped; `SupervisorPolicy.FiresFile` stays, because it has no `StateDir, X` call site to
derive it from, and the comment says so.

**This matters beyond one file.** `docs/tracker.md` says of that tree: *"The tree below is the whole
set - every name the engine can compose under the state dir."* It was not, and the sentence made the
gap invisible: an operator who found `.conductor/inbox/` had a page telling them it could not exist.

## Blind spot 2 - "somewhere on the page" is not "where the reader is looking"

`TheCliReferenceNamesEveryLongOptionAShippedVerbDeclares` asks whether `docs/cli.md` names each
option **anywhere in the document**. CH3.1 measured the per-verb question and found thirteen options
that no doc row named for the verb declaring them. `--home`, written on the `spend` and
`mcp-observe` rows, counted as documented for the six other verbs that declare it.

The new bar, `TheCliReferenceNamesEveryOptionOnTheRowOfTheVerbThatDeclaresIt`, splits the page into
**blocks** - a table row is a block of its own (each row is a different verb; letting a whole table
count as one context credits every verb with every option), a fenced help listing is a block,
everything else is a paragraph - and requires each option to appear in a block that names its verb.
The same split `tools/ch3/docs-surface-diff.py` makes, restated in C# rather than shared: two bars
that share a helper can be relaxed together by one edit.

Run first, it named **24** pairs. What that produced:

- `--yes` and `--force` ride on eleven control verbs with one meaning each. Documented **once**, in
  a paragraph under `## Control` that names all eleven, rather than repeated on eleven rows. The
  block rule is what makes that legal, and it is the honest shape for the doc.
- `catalogue --json`, `worktree --json`, `money --repo`, `plan --repo`, `log --since`,
  `status --since` were real gaps on real rows. Added.

`--plan`, `--help` and `--version` are excluded by name: they are inherited by every verb from
`PlanSettings`, and fifty rows repeating them would be noise the page already handles once.

## Blind spot 3 - the front page described the tour it used to have

CH2.1 extended the demo tour to the courier's tab, the owner-queue pane and the run switcher on
2026-08-26. On 2026-08-27 the README still said *"Seven screens"* and listed the old stops. CH2.2
had already made the recorder write `docs/assets/demo.manifest.json` - what the GIF was recorded
FROM - so the caption now has something to be checked against, and
`TheReadmeCaptionCountsTheStopsTheDemoManifestRecords` checks it. Quitting ends the tour and is not
counted as a stop on it.

## The negative controls - each one seen RED, on a seeded stale doc

The seeds were applied to the real files, the specific test run, and the tree restored. Raw output
in `CH3.3-negative-controls.txt` beside this file.

| Seed | What went red, and what it said |
|---|---|
| `docs/tracker.md` loses the `ci-status.json` entry | `TrackerDocNamesEveryRuntimeArtifactTheEngineCanWrite` - "does not mention 1 artifact(s) the engine writes under plan.StateDir: **ci-status.json**" |
| `docs/cli.md` loses `--home` from the shared paragraph | `TheCliReferenceNamesEveryOptionOnTheRowOfTheVerbThatDeclaresIt` - "names 5 option(s) nowhere near the verb that declares them: **budget --home, catalogue --home, github --home, history --home, money --home**" |
| `README.md` caption says `Seven stops` | `TheReadmeCaptionCountsTheStopsTheDemoManifestRecords` - **Expected: 9, Actual: 7** |

Each names the exact thing that went missing, which is the bar: a test that fails without saying
what broke sends the next session looking.

Three more controls live inside the suite and seed the document in memory, so they run on every
build rather than only when someone remembers to:

- `RemovingOneDocumentedArtifactMakesTheDerivationNameThatExactFile` - three shapes the widened scan
  added: a const in Core (`ci-status.json`), a literal in Core (`settings.session.json`), and a
  directory (`inbox`). `run.db` is deliberately not among them: it lives under the state *home*, not
  the plan's state dir, so it is not this derivation's to find.
- `BlankingOneOptionMakesTheDerivationNameThatExactVerbAndOption` - three differently-written rows: a
  table row (`watches --ports`), a fenced listing (`run --detach`), and the shared paragraph
  (`budget --home`).
- `ACaptionThatCountsTheOldTourIsRed` - seeded in both directions, too few stops and too many.

## Verification

- `dotnet build Conductor.slnx -clp:ErrorsOnly` - Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test Conductor.slnx --filter FullyQualifiedName~SF7_1DocsMatchReality` - **Passed! 43/43**
  (38 before this checkpoint, 5 added).
- Seeded negative controls: three, each red, each naming the exact artifact / verb-option pair /
  count. `CH3.3-negative-controls.txt`.
- Working tree restored after seeding: `git diff --stat` over the three seeded files shows only the
  CH3.3 edits, and `README.md` shows none.

## One finding for the next session, recorded not fixed

`dotnet build` run through the PowerShell tool resolves this repo as `C:\Code\conductor` (capital C)
and fails with **748** Meziantou analyzer errors in `Conductor.Core` that do not exist; the identical
command through the Bash tool, where the cwd is `C:/code/conductor`, prints *Build succeeded, 0
Warning(s), 0 Error(s)*. The analyzer configuration is path-matched and the casing decides which
rules apply. Build through Bash, or pass `--no-build` to `dotnet test`. In the ledger as a trap.
