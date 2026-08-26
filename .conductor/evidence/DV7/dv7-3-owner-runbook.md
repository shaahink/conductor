# DV7.3 — owner runbook, pre-flighted

**Session 23, 2026-08-26. This artifact does NOT perform DV7.3.** DV7.3 is owner-only and this
session verified that rather than inheriting it from the handoff. What is delivered here is the
pre-flight: every precondition the owner will hit, measured today against this machine and this
working tree, so the release cannot fail halfway.

It follows the KS12.3 pattern (`.conductor/evidence/KS12/ks12-3-owner-runbook.md`) — and the first
thing it found is that **KS12.3 itself is only one-seventh done**. `master` was fast-forwarded to
`feat/karvansara-edge` and then nothing else in that runbook happened: no tag, no CHANGELOG rename,
no doc move, no backfill. That is not a criticism; it changes what DV7.3 is. Divan's release is now
**both eras' release**, and the doc move is **five files, not two**.

**Three RED, four GREEN.**

## Why no session can do DV7.3

Not a judgement call — the plan says so, and every sub-action has a named owner-only trap.

| Evidence | What it establishes |
|---|---|
| `plans/divan/core.plan.json:184` (stage DV7 notes) | "DV7.3 is OWNER-ONLY … A session PRE-FLIGHTS each precondition and parks with the runbook, the KS12.3 pattern; it does not perform them." |
| promptExtra trap 1 | "NEVER run `tools/install.ps1` or overwrite the published engine. The conductor driving you runs from that published copy. The owner reinstalls at DV7.3, never a session." |
| promptExtra trap 0 | `run`, `pause`, `plan reload`, `goto` and the rest of the run-control verbs may never be aimed at this repo — and the doc move needs the plan repointed and reloaded (trap 13). |
| promptExtra trap 4 | the real bot token is one `getUpdates` consumer. A session that starts the real courier starves the bot this run is pushing through. |
| promptExtra trap 15 | payesh: "Work there on a branch, open a PR, and stop; the owner merges at DV7.3." |
| the store, measured below | `github sync --backfill` of THIS run is "the closing act". The run's status is `running` — it is me. A backfill now mirrors an unfinished run. |

---

## 1. GREEN — the merge is a fast-forward, and KS12.3's merge already landed

```
$ git rev-parse master feat/karvansara-edge feat/divan
7f4ed4bb7ae5d0c9611e04bc4c4f51d698aa994f   master
7f4ed4bb7ae5d0c9611e04bc4c4f51d698aa994f   feat/karvansara-edge
76af3f860100cec92c1e243ed0e01e9484d6c012   feat/divan

$ git rev-list --left-right --count master...feat/karvansara-edge
0	0
$ git rev-list --left-right --count master...feat/divan
0	70
$ git merge-base --is-ancestor feat/karvansara-edge feat/divan   -> true
```

`master` **is** the edge tip. The stacking clause in the checkpoint text ("KS12.3 lands first or
together") is already satisfied by history: the owner fast-forwarded master to edge at some point
after 2026-08-19. `feat/divan` is 70 ahead and 0 behind, so the merge is a fast-forward with zero
conflicts possible. `origin/master`, `origin/feat/karvansara-edge` and `origin/feat/divan` all match
their local refs (`git ls-remote --heads`), so nothing is unpushed.

**What the owner types:**

```
git checkout master && git merge --ff-only feat/divan && git push origin master
```

---

## 2. RED — the tag would be refused before a single platform compiled

Same failure KS12.3 measured, one era later and still unfixed, because that step was never done.

`.github/workflows/release.yml` runs `tools/changelog-section.sh` as the first job of a tag build and
uses its output verbatim as the release body. The extractor matches a heading `## [<version>]`
exactly. `CHANGELOG.md:21` still says `## [Unreleased]`.

```
$ sh tools/changelog-section.sh 0.5.0
changelog-section: no section for 0.5.0 in CHANGELOG.md.
  Expected a heading '## [0.5.0] - <date>' with at least one line under it.
  Sections found:
## [Unreleased]
## [0.4.1] - 2026-08-15
  ...
exit=1

$ sh tools/changelog-section.sh Unreleased
exit=0   body: 204 lines, opens at "### Added"
```

**The section is 204 lines and it carries BOTH eras.** At KS12.3 it was 112 lines of edge; Divan's
entries joined the same section, as the checkpoint text said they should. No `v0.5.0` tag exists
anywhere — `git tag` and `git ls-remote --tags origin` both stop at `v0.4.1`, and the engine on PATH
reports `0.4.2-alpha.0.79+870786f5b17a.dirty` (MinVer counting 79 commits past `v0.4.1`).

**So the split-or-single call is effectively already made by history, and the owner should know
that before choosing.** Edge is on `master` untagged. Splitting now would mean tagging an
intermediate commit for edge and a second for Divan, with two CHANGELOG sections cut by hand out of
one. A single release covering both eras is one rename. The plan says the call is the owner's, and
this runbook does not make it — it only records that one branch of it is much cheaper than the other
today.

**The fix is one line and it is the owner's, because it carries the version number.** Rename
`CHANGELOG.md:21` from `## [Unreleased]` to `## [<version>] - 2026-08-26`, then re-run
`sh tools/changelog-section.sh <version>` and expect exit 0 with a 204-line body. That body is what
the world reads on the releases page.

The CHANGELOG preamble (`CHANGELOG.md:14-16`) says to re-run `conductor budget` and `conductor money`
when renaming, because a section that quotes a run's score is quoting a dated measurement. DV7.1
re-measured this run's figures into `docs/dev/TOKEN-BUDGET-TUNING.md`; **check the renamed section
for any run total or dollar figure before tagging** — if one is quoted, it is a day old by now.

---

## 3. GREEN, but re-check at the time — the reinstall

The precondition is "no other conductor run is live on this machine". Measured now:

```
$ Get-CimInstance Win32_Process -Filter "Name='conductor.exe' OR Name='conductor-face.exe'"
 9044  conductor.exe  ...\conductor.exe run -p C:\Code\conductor\plans\divan\core.plan.json --headless --no-face --port 4317
24520  conductor.exe  ...\conductor.exe mcp-serve ... --run-id aa91682821c14666915c16317a4fc72c --repo C:/code/conductor --session 23
```

Both belong to this repo and this run; 9044 is `CONDUCTOR_PID`. No face is up. **No BookToCourse
process is live at this moment** — but that run is expected to share the machine (trap 3), so
**re-run this exact command before reinstalling**: the reinstall overwrites the binary both runs
execute.

Two things that were open questions at KS12.3 and are green now:

- **The courier file lock is handled.** `tools/install.ps1:78` calls `Stop-ConductorCourier` at step
  0 and `:95-99` puts it back on the new engine, warning by name if it does not come up. A courier
  that is not restarted keeps running yesterday's engine forever, which is the whole point of
  findings §6.4; the installer no longer lets that happen silently.
- **There is no migration skew.** `MigrationRunner.CurrentVersion` is **15** in the working tree
  (`src/Conductor.Core/Store/MigrationRunner.cs:11`) and **15** at `870786f5b17a`, the commit the
  installed engine was built from; `select * from schema_version` on the live store reads **15**.
  `git log 870786f5b17a..HEAD -- src/Conductor.Core/Store/Migrations/` is empty. Trap 18 does not
  bite this era — but the reinstall still belongs **after the run has ended**, because a live 9044
  holding an old image is exactly the shape that trap describes.

**What the owner types:** re-run the process check; when only dead runs remain, `tools/install.ps1`;
confirm `conductor version` matches the tag.

---

## 4. GREEN — the real courier, three commands and one already-solved risk

Measured through the **fresh build** (`dotnet run --project src/Conductor -- courier status`), never
the engine on PATH:

```
courier  C:\Users\shahi\AppData\Local\conductor\courier
token:   set (CONDUCTOR_TELEGRAM_TOKEN)
poll:    offset 0 (nothing acknowledged yet - the next poll takes everything still undelivered) - every 4s
projects: none  - `conductor courier allow --repo <path>`
chats:    none  - `conductor courier chat --id <chat-id>`
task:     not installed - `conductor courier install` registers it at your logon
running:  no    - nothing is polling for this machine - this build speaks protocol 2
loopback: none  - runs on this machine cannot push through it - secret absent
not ready: no chats are listed, so there is nobody to answer.
```

**The one risk worth pre-flighting was the Scheduled Task's environment**, because a logon-triggered
task inherits *persisted* user/machine variables, not whatever was set in some shell. Measured:

```
User scope set: True (len 46)      # [Environment]::GetEnvironmentVariable(...,'User')
Machine scope set: False (len 0)
Process scope set: True (len 46)
```

The real token is persisted at **User** scope, so the task will see it. No `setx` needed.
`Get-ScheduledTask -TaskName "*onductor*"` returns nothing — the courier has never been installed
here, so there is no stale task to collide with.

The loopback secret being absent is expected, not a gap: `CourierSecret.cs:24-25` creates and
protects it on first use, idempotently. It appears when the daemon first runs.

**What the owner types** (order matters — `install` on a courier with no chats registers a daemon
with nobody to answer; `docs/operating.md:193-220` is the written-up version of this):

```
conductor courier chat --id 99205495 --profile admin    # the owner's chat, per plans/divan/core.plan.json telegram block
conductor courier allow --repo C:/code/conductor        # and any other project it may file into
conductor courier install                               # scheduled task: logon trigger, restart-on-failure
conductor courier status                                # expect: task installed, running yes, protocol 2
```

**The consequence to accept before typing it, not after** (findings §6.9,
`docs/operating.md:212-215`): the day the courier owns the token, **in-run polling refuses to start**
on this machine and names the courier. Every plan on this machine — including
`plans/divan/core.plan.json`, whose `telegram` block is `enableTwoWay: true` with chat `99205495` —
pushes through the daemon or not at all. A repo that is not on the allowlist has its notes parked in
the dead-letter box (`conductor inbox parked`), not delivered. Allow every project you actually use
in the same sitting.

---

## 5. GREEN — the backfill, with the run id spelled out and two hazards named

Probed through the fresh build (`github sync --help`): `--backfill <RUN>`, `--repo`, `--dry-run`,
`--no-diary`, `--project <NUMBER>`, `--home <PATH>` all present as the checkpoint names them.

**The run id to pass is `aa91682821c14666915c16317a4fc72c`** — read from the live `mcp-serve` command
line above and confirmed in the store:

```
$ sqlite3 -readonly <store> "select substr(run_id,1,12), plan_name, status from runs order by rowid desc limit 5"
aa91682821c1 | Divan - the chancellery: inbox, courier, and the record that gets out | running
9491891fe700 | Karvansara edge - gates that can't be gamed, and the courier          | needs_human
9647f1b80d18 | Karvansara core - the open door                                       | Aborted
8cefa5de8f16 | Sarban face - the watcher and the surfaces                            | Completed
e9e21d10aedf | Sarban core - the engine says what it knows                           | Completed
```

Hazards, both measured:

1. **The slug is ambiguous three ways now.** All of these live in one store directory,
   `conductor-karvansara-core---the-open-door-308cfb9b` — Divan shares the karvansara store. A bare
   slug, repo name or prefix argument cannot pick one era. **Pass the full run id.**
2. **Do not run the backfill twice — bug #79.** The read-only path uses `GithubMap.Transient()`, so
   nothing about pass 1 survives the process; a second pass inside the API's replica lag re-creates
   issues it cannot see yet. Measured live at DV6.1: pass 2, seconds later, created 4 more issues.
   Rehearse with `--dry-run`, then run it **once** for real and read the result.
3. `--project` will refuse: the machine token still lacks `project` scope (bug #80). The one-command
   unblock is `gh auth refresh -s project`, and it is the owner's to run or to skip.

The edge run is still `needs_human` in that listing — **KS12.3's backfill never ran either.** If the
owner wants the whole GitHub record, that is a second backfill with run id `9491891fe700...`, and it
has the same once-only rule.

**What the owner types, after the run has ended and after the reinstall:**

```
conductor github sync --backfill aa91682821c14666915c16317a4fc72c --dry-run
conductor github sync --backfill aa91682821c14666915c16317a4fc72c
```

(`plans/divan/core.plan.json` sets `github.repo` to `shaahink/conductor`, so no `--repo` is needed.)

---

## 6. RED — the payesh merge is two merges and the first one conflicts

The handoff said "two merges, #2 then #3". Measured, it is worse than that: **#2 no longer merges.**

```
$ gh pr list --state all --json number,state,baseRefName,headRefName,mergeable
#3 OPEN  dv7/harvest-era-close  -> ks12/harvest-era-close   MERGEABLE
#2 OPEN  ks12/harvest-era-close -> main                     CONFLICTING
#1 MERGED ks01/harvest-dedup-refresh -> main
```

Why, exactly:

```
merge-base(main, ks12/harvest-era-close) = 43b59e4
$ git rev-list --left-right --count origin/main...origin/ks12/harvest-era-close
3	1
main gained:  51566dc (merge of PR #1), e9077b7, 6e5f395
files on main side:   scripts/anonymity.mjs  scripts/harvest.mjs  src/data/corpus.json  test/anonymity.test.mjs
files on ks12 side:   scripts/harvest.mjs  src/data/corpus.json
overlap:              scripts/harvest.mjs  src/data/corpus.json
```

PR #1 was merged to `main` *after* `ks12/harvest-era-close` was cut, and both sides edit the same two
files. `#3` sits two commits on top of `#2` (`git rev-list --left-right --count ks12...dv7` = `0 2`)
and inherits the problem.

**The resolution is mechanical, and the important half of it is that `src/data/corpus.json` is a
generated file.** Hand-merging it would be merging two snapshots of a recomputation. The path is:

```
cd C:/code/conductor-site
git checkout ks12/harvest-era-close && git rebase origin/main
#   scripts/harvest.mjs  -> hand-merge; the ks12 hunk adds NEEDS_DISPOSITION for `closed`,
#                           main's hunk is a different 18-line change from PR #1. Both are wanted.
#   src/data/corpus.json -> take EITHER side, then regenerate:
npm run harvest && npm test && npm run anonymity
git push --force-with-lease            # then merge PR #2
git checkout dv7/harvest-era-close && git rebase ks12/harvest-era-close
npm run harvest && npm test            # regenerate again on the final base
git push --force-with-lease            # then merge PR #3
```

A defensible alternative, if the owner would rather not carry two PRs: **#3's harvest already
recomputed the whole corpus** — 20 runs, 387 sessions, both closed eras — so squashing #2's
`harvest.mjs` change onto #3 and closing #2 loses no data. That is an editorial call, not a
mechanical one.

**`npm run anonymity` is RED on that branch and it is not caused by this era's work** (recorded at
DV7.2): 77 findings in the built output — 76 are the ordinary-noun repo name of bugs **#47/#41**, and
1 is **bug #83**, a plan *title* matched as wording via `src/components/FigureQueue.astro`. Neither
comes from the re-harvest. The owner merges into a red check knowingly or fixes the bugs first;
nothing here may make the check pass by relaxing it.

**Never push `main` from a session.** `payesh.vercel.app` auto-deploys from it (trap 15).

---

## 7. RED — the doc move is five files, because KS12.3's move never happened

`docs/dev/README.md` still lists `KARVANSARA-PLAN-2026-08-13.md` (line 13), `EDGE-TRACKER.md`
(line 14) and `CORE-TRACKER.md` (line 21) under **Current work**, and its own paragraph says
"KS12.3 is the checkpoint that performs the move this paragraph promises". It did not. So the move
commit at DV7.3 carries **both** eras or the index keeps lying.

Destination, per the convention in that same file: briefs to `docs/history/`, trackers to
`docs/history/archive/trackers/`. Both directories exist and hold the W-series and Sarban examples.

### 7a. What moves

| From | To |
|---|---|
| `docs/dev/KARVANSARA-PLAN-2026-08-13.md` | `docs/history/` |
| `plans/karvansara/CORE-TRACKER.md` | `docs/history/archive/trackers/` |
| `plans/karvansara/EDGE-TRACKER.md` | `docs/history/archive/trackers/` |
| `docs/dev/NEXT-ERA-FINDINGS-2026-08-23.md` | `docs/history/` |
| `plans/divan/TRACKER.md` | `docs/history/archive/trackers/` |

### 7b. What breaks the instant they move — every reference, swept

Divan's two files:

- `plans/divan/core.plan.json:71` `"tracker"`, `:72` `"planDoc"`, `:278` `readOrder` entry.
- `docs/dev/README.md:15` (findings row) and `:16` (tracker row) — both move out of **Current work**.
- `README.md:338` links the live tracker; `:339-341` link both karvansara trackers in the same
  paragraph, and that whole paragraph stops being true when there is no era in flight.
- `plans/divan/TRACKER.md:3` names the design doc — it travels with the file, repoint it in place.
- `docs/dev/adr/0008-the-courier-outlives-the-run.md:9` "Sourced from" — prose, but a stale path.

Karvansara's three, from KS12.3's sweep, re-verified today as still red:

- `tests/Conductor.Tests/SF7_1DocsMatchRealityTests.Karvansara.cs:116` —
  `Assert.Contains("plans/karvansara/CORE-TRACKER.md", current)`, where `current` is the
  **Current work** section only. Fails on the move.
- the same file `:118-128` — `Assert.True(File.Exists(...))` over
  `docs/dev/KARVANSARA-PLAN-2026-08-13.md` and `plans/karvansara/CORE-TRACKER.md`, message
  "docs/dev/README.md points at {relative} and it is not there."
- the same file `:109-114` — the assertion that the line containing **"design authority for current
  work"** names `KARVANSARA-PLAN-2026-08-13.md`. **This one is the interesting one**: after the move
  there is no open era, so nothing in the repo is the design authority for current work. The owner
  decides what that row becomes — `plans/karvan/lanes.plan.json` (authored, 0/23, unlaunched) is the
  only candidate on the board — and the test must be rewritten to whatever the answer is. It may not
  simply be deleted.
- `plans/karvansara/edge.plan.json:39` (`tracker`), `:40` (`planDoc`), `:230` (`readOrder`) and
  `plans/karvansara/core.plan.json:36` (`tracker`), `:37` (`planDoc`) plus its own `readOrder` entry.

Swept and cleared, so the owner does not have to look: no test resolves `plans/divan/TRACKER.md` or
`docs/dev/NEXT-ERA-FINDINGS-2026-08-23.md` as a path (`grep -rn` over `tests/` returns only
`DV6_3BoardPageTests.cs:366`, a `PlanDir: "plans/divan"` fixture string that opens nothing, and
`K4_2BudgetTests.cs:19`, which cites the **2026-08-04** findings doc, a different file).
`src/Conductor.Core/Integrations/Cloud/CloudCliFacts.cs:6` names the findings doc in a doc-comment
with no directory.

### 7c. Why it is one commit, and why it is last

Trap 13: if a plan's `tracker`, `planDoc` or `readOrder` is repointed without a reload, the next
session of that plan reads nothing. `plan reload` is on trap 0's forbidden list for this repo. **So
the move happens after the run has ended**, in a single commit containing the five `git mv`s, the
plan repoints, the two README rewrites, the ADR line and the test rewrite — and then the build and
`dotnet test --filter SF7_1` before pushing.

---

## The order to do it in

1. Rename `CHANGELOG.md:21` to the version; verify `sh tools/changelog-section.sh <version>` exits 0
   with a 204-line body. Commit on `feat/divan`.
2. Let this run finish. `git checkout master && git merge --ff-only feat/divan && git push origin master`.
3. Tag `v<version>` and push the tag; the guard job prints the release body it will use.
4. Re-run the `Get-CimInstance` process check; when clear, `tools/install.ps1`; confirm
   `conductor version` matches the releases page.
5. `conductor courier chat` → `courier allow` → `courier install` → `courier status`. Accept that
   in-run Telegram polling now refuses on this machine.
6. `conductor github sync --backfill aa91682821c14666915c16317a4fc72c --dry-run`, then **once** for
   real. Optionally the edge run `9491891fe700...` the same way.
7. payesh: rebase and re-harvest per §6, merge #2, then #3. Never push `main` by hand.
8. The move commit, all of §7 together, after the run has ended.

## Decisions only the owner can make

- **The version number.** Latest tag is `v0.4.1`; the installed engine reports
  `0.4.2-alpha.0.79`. Two eras of features, no breaking change declared in the CHANGELOG section,
  which reads as `v0.5.0` under semver — but nothing in the repo states the intended number and this
  runbook will not pick it.
- **Single release or split.** §2: edge is already on `master` untagged, so a single release is one
  rename and a split is two hand-cut sections. The plan says this call is never a session's.
- **Whether to squash payesh #2 into #3** (§6) rather than rebase both.
- **What "the design authority for current work" points at** once both eras are in `history/` (§7b).
- **Whether the Divan and Karvansara runs join the published payesh corpus.** Still excluded by
  `anonymise.json`; eleven runs remain outside it pending labels. Editorial, not mechanical.
- **Whether to `gh auth refresh -s project`** before the backfill (bug #80).

## Carried in red, unchanged by this session

- payesh `npm run anonymity`: 77 findings — bugs **#47/#41** (76) and **#83** (1). Pre-existing.
- analyzer-debt ratchet (bug **#60**): pragma-src 33 against a bar of 31, both added by KS4.4
  (05696d4). Stated honestly in the docs, not fixed, and the bar may not be moved.
- bug **#79** (backfill duplicates on a second pass), **#80** (no `project` scope), **#81**
  (followups.md has 91 rows for 55 ids), **#82** (SARIF has never had a 202), **#75** (a note keeps
  only its first line), **#76** (the courier delivers a file as text naming its path).
