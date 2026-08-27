# CH4.1 — `conductor release preflight`: the era-close stops being prose

**Measured 2026-08-27, session 5 of Charkh, through the FRESH BUILD**
(`dotnet run --project src/Conductor -- release preflight`), never the `conductor` on PATH.

The artifact this replaces is `.conductor/evidence/DV7/dv7-3-owner-runbook.md` — the best
hand-written era-close this project has produced. It was written because its predecessor,
`ks12-3-owner-runbook.md`, had six of its seven acts go unperformed one era earlier with nothing
anywhere saying so. A prose checklist has no failure mode: it is equally quiet whether it was
followed or ignored. This checkpoint gives it one.

---

## What shipped

| File | What it is |
| --- | --- |
| `src/Conductor.Core/Release/ReleaseCheck.cs` | the verdict record: `Name`, `State`, `Headline`, `Detail` — the launch drill's `Leg` shape, one door further along |
| `src/Conductor.Core/Release/ReleaseFacts.Repo.cs` | `MergeFacts`, `ChangelogFacts` |
| `src/Conductor.Core/Release/ReleaseFacts.Machine.cs` | `LiveEngine`, `ProcessFacts`, `MigrationFacts` |
| `src/Conductor.Core/Release/ReleaseFacts.Record.cs` | `CourierFacts`, `MirroredRun`, `BackfillFacts` |
| `src/Conductor.Core/Release/ReleasePreflight.cs` | the six **pure** verdicts + `ExitCode` + `Verdict` |
| `src/Conductor/Commands/ReleaseCommand.cs` | the verb, the render, the exit code |
| `src/Conductor/Commands/ReleaseCommand.Probes.cs` | the **impure** half: git, `sh`, the process table, `schtasks`, the store |
| `tests/Conductor.Tests/CH4_1ReleasePreflightTests.cs` | 21 tests, every one of them a negative control |
| `src/Conductor/Program.cs:149` | registration |
| `src/Conductor/Commands/CompletionCommand.cs:47` | `release` in the completion verb list |
| `docs/cli.md`, `docs/operating.md` §2 | the rows the CH3 docs battery demands |

**The split is the point.** Measuring is impure and lives in the verb; deciding is pure and lives in
`Conductor.Core.Release`. That is what lets a test seed a red fact and prove the exit code moves
without a git repository, a courier or a store — the one property a hand-written checklist never had.

---

## The six lines, and where each precondition came from

| Line | DV7.3 measured it by hand as | The engine now measures |
| --- | --- | --- |
| `merge` | §1, three `git rev-list` invocations typed out | `base...branch` counts, `origin/base` counts **both ways**, `status --porcelain` |
| `changelog` | §2, `sh tools/changelog-section.sh 0.5.0` pasted with its output | the same script, **run**, its exit code IS the verdict |
| `processes` | §3, `Get-CimInstance Win32_Process` pasted | `UpdateSafety.Blockers` + every live `conductor`/`conductor-face` image, `CONDUCTOR_PID` named |
| `migration` | §3, `MigrationRunner.cs:11` read + a `git log` over the migrations dir | `MigrationRunner.CurrentVersion` vs the migrations landed since the installed engine's commit, plus the store's `schema_version` |
| `courier` | §4, `courier status` + three `[Environment]::GetEnvironmentVariable(...,'User')` probes | token, **persisted scope**, chats, allowlist, scheduler state, presence |
| `backfill` | §5, a `sqlite3 -readonly` listing + a hand-picked run id | `RunArchive.Runs()` reconciled + `github_map` counts, full run ids |

**Exit code.** `0` only when every line is green · `1` when anything is red · `2` when nothing is red
and something is a **judgement only the owner makes**. The third state is the CH4 idea: KS12.3's
owner-only acts read exactly like its performed ones, so they were skipped in silence. The version
number and whether a run joins the published corpus are now *named and stopped at*.

---

## Live run 1 — `--tag 0.6.0`, exit **1**

Full capture: [`ch4-1-live-tag.txt`](ch4-1-live-tag.txt)

```
conductor release preflight — Charkh - the wheel: what the owner still does by hand becomes machinery
repo: C:/code/conductor  ·  release: 0.6.0

✗ merge      feat/charkh is 18 ahead of master and would fast-forward, but the working tree is not clean
             the working tree has uncommitted changes - commit or stash first, or the merge takes them along
             local master is 9 behind origin/master, and feat/charkh already contains all 9 - the
             fast-forward carries them; `git pull` first so you see what you merge into
             origin/master is read as of your last fetch - this verb does not fetch, and a stale
             remote ref is a stale verdict
✗ changelog  no CHANGELOG section for 0.6.0
             exit 1 - release.yml runs this as the first job of a tag build, so the tag would be refused
             changelog-section: no section for 0.6.0 in CHANGELOG.md.   <- the script's own words
✗ processes  1 reason(s) a binary swap is unsafe right now
             pid  3392 …\conductor\conductor.exe
             pid  5248 …\conductor\conductor.exe <- CONDUCTOR_PID, the run asking this question
             pid 33884 …\conductor\conductor.exe
             a run is live in C:\Code\conductor\.conductor (engine pid 5248)
✓ migration  no schema skew: tree v15, installed engine 0.5.0 carries the same migrations
             store: …\conductor-karvansara-core---the-open-door-308cfb9b\run.db at schema 15
✓ courier    the courier is installed, running and reachable, and its token is persisted where the
             task can see it
             token set, persisted at User scope - 1 chat(s), 4 project(s) allowed
             task registered, running pid 33884
? backfill   4 finished run(s) have no GitHub record - whether they join the published corpus is yours
             858b48387e4e Charkh … - still running; its own backfill is the closing act
             9491891fe700463ba0d876c06280cce2 - Karvansara edge (needs_human) has 0 issues
             9647f1b80d1841e9997a801562a267c7 - Karvansara core (Aborted)     has 0 issues
             8cefa5de8f164848bd42b275e14ba9cf - Sarban face (Completed)       has 0 issues
             e9e21d10aedf4390a1580ac6930bac3e - Sarban core (Completed)       has 0 issues
             run it ONCE - a second pass inside GitHub's replica lag mints the board again (bug #79)

NOT READY - 3 of 6 red: merge, changelog, processes; 1 waiting on the owner: backfill (2301ms)
nothing was merged, tagged, installed or pushed — this verb only measures.
```

Exit code **1**. Verified by hand: `git rev-list --left-right --count master...origin/master` → `0 9`,
`master...feat/charkh` → `0 18`, `origin/master...feat/charkh` → `0 9`.

## Live run 2 — `--tag Unreleased`, exit **1**: bug #88, caught by the verb

Full capture: [`ch4-1-live-unreleased.txt`](ch4-1-live-unreleased.txt)

```
✗ changelog  the Unreleased section exists but says nothing (2 non-blank line(s))
             this body IS the release notes the world reads - a placeholder ships as the release
```

`sh tools/changelog-section.sh Unreleased` **exits 0** on this section today. The script cannot tell
a section from a placeholder; the verb can, and it is red for exactly the reason bug #88 is open.
This is the checkpoint's own precondition catching its own open bug without being told about it.

## The negative control the verb itself provides — the PATH engine

```
$ conductor release preflight
error: Unknown command 'release'.        exit=1
```

The published `0.5.0+e60ae79c92dc` on PATH — the engine driving this session — does not have the
verb. Everything above was produced by the fresh build, as trap 2 requires.

---

## Three defects found by building this, all fixed in the same commit

1. **A shell that never ran was reporting as a missing CHANGELOG section.** On Windows `sh` is Git's,
   in `usr/bin`, which is not on the Windows PATH; `ProcessRunner` reports a *failure to start* as
   exit `-1` with the reason on **stdout**. Read naively the first run printed
   `no CHANGELOG section for 0.6.0` — a verdict about the CHANGELOG produced by a shell that never
   ran. Could-not-measure rendering as measured is the exact family of failure this era exists to
   remove. Fixed: the shell is resolved explicitly (PATH → `bash` → Git's `usr/bin/sh.exe`) and an
   absent one is a *distinct* red line. Pinned by
   `A_shell_that_never_ran_is_reported_as_unmeasured_not_as_a_missing_section`.

2. **`conductor` on PATH here is a scoop `.CMD` shim.** `UpdateSafety`'s process-image detector
   compares `MainModule` to the path it is handed, and no process ever executes a `.CMD` — so handing
   it the PATH entry makes that half of the detector silently blind, leaving only the engine lock.
   The real executable comes from `version --json`'s `binary` field ("which file answered"). One
   `version --json` now feeds both the `processes` and `migration` lines.

3. **`DV4_3CourierSeamTests` had a 1-in-16 flake** (bug **#89**, filed and fixed here):
   `secret[..^1] + "0"` is not a mutation when the secret already ends in `0`, and
   `CourierSecret.Resolve` mints a fresh value per state home. It fired in this session's first full
   suite run. Now flips to a character that cannot be the one already there.

Three architecture bars in this repo also caught real problems in the new code and each was fixed by
changing the code, never the bar:

- `ArchitectureTests.NoFileGrowsPastItsTypeCeilingOrItsRecordedDebt` — one file declared 10 types
  (allowed 3) → split into five files.
- `B11_2DoctorAndCompletionTests.Completion_ContainsAllRegisteredVerbs_Exhaustive` → `release` added
  to the completion verb list.
- `KS1_6FoldIsTruthTests.ReadersOutsideTheEngineDoNotConsumeMutableSnapshotColumns` — the first draft
  read `runs.status` in its own SQL. `runs.status` is what the last engine to write the row believed,
  and an engine that was killed never corrects it: **four rows on this machine say `running` for
  ever**, and a backfill line that believed the column would never name any of them as owed a record.
  Now the run list comes through `RunArchive.Runs()` and the status is `RunLiveness.Reconcile`d, with
  the row's own claim carried beside it (`StoredStatus`) and `InFlight` computed by
  `RunLiveness.IsStillGoing` — a **property**, not a list of status words that rots the next time the
  park vocabulary grows (lesson 21).

---

## Tests — 21 of them, every one a negative control

Capture: [`ch4-1-tests.txt`](ch4-1-tests.txt) — `Passed: 110, Failed: 0` across
`CH4_1ReleasePreflightTests`, `DV4_3CourierSeamTests`, `ArchitectureTests`, `KS1_6FoldIsTruthTests`,
`B11_2DoctorAndCompletionTests` and `SF7_1DocsMatchRealityTests`.

The bar that makes this a checklist rather than a list is
`Each_precondition_alone_is_enough_to_refuse_the_release`: it builds an all-green run, asserts exit
**0**, then substitutes exactly one red line at a time and asserts the whole run goes **1** and that
the verdict *names* that line. Five preconditions, five controls, so a line that stops measuring
cannot go quiet. Alongside it:

- `The_exit_code_separates_broken_from_undecided_from_ready` — 0 / 1 / 2, and red outranks owner.
- `A_stale_local_base_the_branch_already_contains_is_green_and_said_out_loud` and
  `A_remote_base_ahead_of_the_branch_is_red_even_when_the_local_merge_would_work` — the pair measured
  live on this repo, and the reason the merge line carries three counts instead of two.
- `A_section_that_exists_and_says_nothing_is_red_even_though_the_script_exits_zero` (bug #88's shape)
  and `A_real_section_is_not_called_a_placeholder_for_being_short` (the overshoot bar).
- `A_token_set_only_in_this_shell_is_red_because_the_scheduled_task_cannot_see_it` — the exact risk
  DV7.3 measured by hand and found green.
- `A_run_whose_row_still_claims_running_after_its_engine_died_is_owed_a_record` — KS1.6's rule as a
  behaviour, not a lint.
- `An_unidentifiable_installed_engine_is_red_rather_than_assumed_current` — "could not measure" is
  never "measured green".

---

## What this verb deliberately does NOT do

- **It performs nothing.** No merge, no tag, no `install.ps1`, no push, no issue. That is CH4.2's
  work, and the line between the mechanical and the judgement is drawn here first.
- **It never dials Telegram.** One `getUpdates` consumer per token and the live courier owns it
  (trap 4), so the courier line reads the scheduler, the presence file and the settings file only.
- **It opens the store read-only.** `StateHome.Peek` (the zero-side-effect twin of `Resolve`) and
  `RunArchive` over `Mode=ReadOnly`, so asking cannot migrate a store the driving engine is holding
  (trap 18).
- **It does not `git fetch`.** `origin/<base>` is read as of the last fetch, and the merge line says
  so on every run rather than letting a stale ref produce a confident stale verdict.

## Open, carried forward

- Bug **#88** — `[Unreleased]` still says "Nothing yet" after CH1, CH2 and CH3 shipped. The verb now
  reports it; writing the section is a content act, and CH4.2/CH5 own it.
- Bug **#87** — `courier status` still ends with "ready - conductor courier run starts polling" after
  printing "running: yes". Untouched here; the release preflight's courier line does not repeat it.
- The four unmirrored runs above are named, with their full run ids and the once-only rule. Whether
  they join the published corpus is the owner's, which is why that line is `owner` and not `fail`.
