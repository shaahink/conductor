# CH4.3 — the retire sweep asks whose board it is

**Date** 2026-08-27 · **Branch** `feat/charkh` · **Commit** `f4022f6`
**Engine under test** fresh build from the tree (`dotnet run --project src/Conductor --`)
**Baseline** the installed engine on PATH, `0.5.0+e60ae79c92dc` — the one driving this run

---

## 1. The defect, measured on the real repository before anything was changed

`GithubBoardSync.BackfillAsync` lists a repository's issues WHOLE
(`GithubBoardSync.cs:57`, `client.ListIssuesAsync(repo)`), `SyncCardsAsync` indexes every one of
them whose body carries a task marker (`GithubBoardSync.cs:82-84`), and `RetireAsync`
(`GithubBoardSync.cs:204-227` before this change) closed every entry of that index the run being
synced did not declare — with a comment reading *"this checkpoint is no longer declared in the
plan"*.

Nothing scoped it to a run, because nothing could: `GithubIdentity.TaskMarker` is
`<!-- conductor:task <id> -->` and carries no run, while `RunMarker` and `SessionMarker` both do.

**This had already happened, on `shaahink/conductor`, and nobody had seen it.** Read-only sweep of
the live board:

```
$ gh api "repos/shaahink/conductor/issues?state=all&per_page=100"   # markers + labels

CH5.2 #125 open   [conductor:source:tracker,conductor:status:todo]
CH4.3 #122 open   [conductor:source:tracker,conductor:status:todo]
CH1.1 #112 closed [conductor:status:done,conductor:source:tracker,conductor:confirmed]
DV7.3  #59 closed [conductor:status:done,...,conductor:confirmed,conductor:retired]
DV1.1  #37 closed [conductor:status:done,...,conductor:confirmed,conductor:retired]
KS9.1  #32 closed [conductor:status:done,...,conductor:confirmed,conductor:retired]
KS5.3  #30 closed [conductor:status:done,...,conductor:confirmed,conductor:retired]
```

Every `DV*` card (all 23 of Divan's checkpoints, #37–#59) and every `KS*` card before them carries
`conductor:retired`. Every `CH*` card is clean. The Charkh run — this run — retired the previous two
eras' boards, one stage boundary at a time, with the published engine, while nobody was looking.

## 2. The fix

The root cause is the marker, so the marker is where the fix starts.

- `GithubIdentity.OwnerMarker(runId)` → `<!-- conductor:owner <runId> -->`, planted in every card
  body next to the task marker (`GithubBoardPlan.BodyFor`). Deliberately **not** `RunMarker`: the
  diary issue is found by scanning bodies for that exact string, so a card carrying it would be
  adopted as the run's diary.
- `GithubBoardSync.IsOurs(taskId, issue)` — an issue is a retire candidate only when **this run's
  owner marker is in its body** *or* **this run's local map points at that exact issue number**.
  The map covers every issue created before the marker existed, which is every issue on the real
  repository today, and is the authority the v14 migration already settled on.
- Everything else is **refused, never skipped**: named with its number on
  `GithubSyncResult.RetireRefused`, counted in `Summary()`, printed in full by
  `conductor github sync` and logged by the live mirror (`GithubMirror.cs`). Not an error — a
  repository carrying an earlier era's board is the normal case here, and a backfill that exited
  non-zero for correctly leaving another run alone would be unusable.

## 3. Tests — and the negative control

`tests/Conductor.Tests/CH4_3RetireScopeTests.cs`, four facts:

| test | asserts |
| --- | --- |
| `ASecondRunDoesNotRetireTheFirstRunsBoard` | the regression: run B closes none of run A's board, and names both cards with their numbers |
| `EveryOutOfPlanCardIsEitherRetiredOrNamed` | the **property**: no third, quiet answer — retired ∪ refused covers every out-of-plan task-marked issue |
| `AnIssueFromBeforeTheOwnerMarkerIsAttributedByTheLocalMap` | scoping does not cost the feature: a legacy issue with no owner marker is still retired from the map row |
| `TheSameIssueWithNeitherMarkerNorMapRowIsLeftAloneAndNamed` | the read-only backfill path reports what it declined instead of reporting nothing |

**Negative control** — `IsOurs` replaced with the old, unscoped decision (always true), rebuilt, re-run:

```
Failed  CH4_3RetireScopeTests.ASecondRunDoesNotRetireTheFirstRunsBoard
  Assert.Empty() Failure: Collection was not empty
  Collection: ["DV1.1", "DV1.2"]
Failed  CH4_3RetireScopeTests.EveryOutOfPlanCardIsEitherRetiredOrNamed
  Assert.Equal() Failure: HashSets differ
  Expected: ["DV1.1", "DV1.2", "CH1.2"]
  Actual:   ["CH1.2"]
Failed  CH4_3RetireScopeTests.TheSameIssueWithNeitherMarkerNorMapRowIsLeftAloneAndNamed
  Assert.Empty() Failure: Collection was not empty
  Collection: ["KS1.2"]

Failed!  - Failed: 3, Passed: 1, Skipped: 0, Total: 4
```

The fourth passes both ways **by design** — it pins the capability that must not be lost.

Restored, rebuilt, and the touched classes green:

```
$ dotnet test Conductor.slnx --no-build --filter "CH4_3RetireScope|Github|KS9|DV6"
Passed!  - Failed: 0, Passed: 143, Skipped: 0, Total: 143
```

## 4. The live proof — A/B, same inputs, same instant

**Rig.** `sqlite3 .backup` of the live consolidated store into `%TEMP%\ch43\store.db`, then one
trimmed copy per run (`DELETE FROM runs WHERE run_id <> '<target>'`, so `ArchiveView.OpenDb`'s
newest-run-in-the-file rule selects it). Scratch plan copied from `plans/charkh/core.plan.json`
with `github.repo` repointed and `liveMirror` off. Destination `shaahink/ch43-retire-scratch`,
private, created for this proof. The live store was never opened for write by the fresh build.

**Step A** — the fresh build backfills Divan into the empty scratch repo:

```
run aa916828  plan Divan...  → shaahink/ch43-retire-scratch  token from CONDUCTOR_GITHUB_TOKEN
78 created · 17 updated · 4 unchanged · 0 retired · 0 comments · 0 errors
148 requests
```

**Step B** — the **installed 0.5.0** dry-runs the edge run's backfill against that repo:

```
$ conductor --version
0.5.0+e60ae79c92dc
run 9491891f  plan Karvansara edge...  → shaahink/ch43-retire-scratch
dry run — nothing will be written.
24 created · 49 updated · 38 unchanged · 23 retired · 0 comments · 0 errors
```

`23 retired` — the 2026-08-26 measurement, reproduced live.

**Step C** — the **fresh build**, same command, same repo state:

```
24 created · 49 updated · 38 unchanged · 0 retired · 0 comments · 0 errors · 23 retire refused
retire refused 23 task-marked issue(s) are out of this plan but not attributable to this run - left untouched
  DV1.1 #1     DV1.2 #2     DV2.1 #3     DV2.2 #4     DV2.3 #5     DV2.4 #6
  DV3.1 #7     DV3.2 #8     DV3.3 #9     DV3.4 #10    DV4.1 #11    DV4.2 #12
  DV4.3 #13    DV4.4 #14    DV5.1 #15    DV5.2 #16    DV6.1 #17    DV6.2 #18
  DV6.3 #19    DV6.4 #20    DV7.1 #21    DV7.2 #22    DV7.3 #23
```

**Step D** — the same run, performed for real (`115 requests`), then the repository read back:

```
total issues: 102
DV cards: 23   retired-labelled: 0   with comments: 0

$ gh api repos/shaahink/ch43-retire-scratch/issues/1
state=closed  labels=conductor:status:done,conductor:source:tracker,conductor:confirmed  comments=0
markers: <!-- conductor:task DV1.1 -->  <!-- conductor:owner aa91682821c14666915c16317a4fc72c -->
```

Both markers survive the wire. The 23 cards are closed **because Divan's checkpoints are done**,
not because they were retired: no retired label, no comment, untouched.

## 5. The same A/B against the real repository — and why the second half of this checkpoint is ordered after CH5

Both dry runs, `shaahink/conductor`, nothing written:

| engine | verdict |
| --- | --- |
| installed `0.5.0+e60ae79c92dc` | `24 created · 55 updated · 32 unchanged · **14 retired** · 0 errors` |
| fresh build | `24 created · 55 updated · 32 unchanged · **0 retired** · 0 errors · **14 retire refused**` — `CH1.1 #112 … CH5.2 #125` |

Those 14 are **Charkh's own live board** — this run's cards, including the four still open and
`CH4.3 #122`, this checkpoint. An edge-run backfill performed today with the installed engine would
have closed the board of the run performing it.

Which is the finding that orders the rest of the card. The checkpoint's second clause is *"then the
edge run's own GitHub record is written"*. The plan's github block is
`enabled: true, liveMirror: true, repo: shaahink/conductor` (`plans/charkh/core.plan.json:180-186`),
and the engine performing that live mirror is the published 0.5.0 with the repo-wide sweep. Writing
the edge run's ~24 issues to that repository now would see them closed, labelled
`conductor:retired` and permanently commented *"no longer declared in the plan"* at the very next
stage boundary — by the bug this checkpoint exists to end. The write is therefore ordered **after
CH5's reinstall**, not before it, and the command is exactly the one proven above with `--dry-run`
dropped. Recorded on the card by `conductor task --amend CH4.3`, and carried in the handoff.

The residue on the real board — 23 Divan and ~14 Karvansara cards already wearing
`conductor:retired` — is repairable by the fixed engine (`conductor:retired` starts with the label
prefix, so `MergeLabels` drops it on the next reconcile) and is filed as its own bug rather than
smuggled into this one.

## 6. Files

- `src/Conductor.Core/Integrations/Github/GithubIdentity.cs` — `OwnerMarker` / `OwnerIdIn`
- `src/Conductor.Core/Integrations/Github/GithubBoardPlan.cs` — `Cards(events, prefix, runId)`, body stamp
- `src/Conductor.Core/Integrations/Github/GithubBoardSync.cs` — `_runId`, `IsOurs`, scoped `RetireAsync`
- `src/Conductor.Core/Integrations/Github/GithubSyncResult.cs` — `RetireRefused`, `Summary()`
- `src/Conductor.Core/Integrations/Github/GithubMirror.cs` — the refusal on the run's own log
- `src/Conductor/Commands/GithubCommand.cs` — the refusal printed in full, never truncated
- `tests/Conductor.Tests/CH4_3RetireScopeTests.cs`, `tests/Conductor.Tests/FakeGithub.cs`
