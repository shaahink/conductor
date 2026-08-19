# KS12.3 - owner runbook, pre-flighted

**Session 23, 2026-08-19. This artifact does NOT claim KS12.3.** KS12.3 is owner-only and this
session verified that rather than inheriting it from the handoff. What is delivered here is the
pre-flight: every precondition the owner will hit, measured today, so the release cannot fail
halfway. Two of the five are RED right now and would have stopped the release after it started.

## Why no session can do KS12.3

Not a judgement call - the plan says so, and every sub-action has a named owner-only trap.

| Evidence | What it establishes |
|---|---|
| `plans/karvansara/edge.plan.json:142` | `"ownerGate": true` on stage KS12. The stage parks on the owner by construction. |
| promptExtra trap 1 | merge / tag / release / reinstall: "The owner reinstalls at the KS11 ownerGate park (their option) and at KS12.3, **never a session**." |
| promptExtra traps 0 and 5 | `github sync --backfill` of THIS run is "the closing act" - the run is not closed while a session is inside it, so a backfill now mirrors an unfinished run. The board mirror is the engine's job. |
| promptExtra trap 15 | the payesh PR: "Work there on a branch, open a PR, and stop; **the owner merges at KS12.3**." |
| promptExtra trap 13 + trap 0 | the tracker move needs the plan updated and reloaded in the same change, and `plan reload` is on trap 0's forbidden list for this repo. |

## Pre-flight results - 2 RED, 3 GREEN

### 1. RED - the tag would be refused before a single platform compiled

`.github/workflows/release.yml` runs `tools/changelog-section.sh` as the first job of a tag build
and uses its output verbatim as the release body. The extractor (`tools/changelog-section.sh:39-49`)
matches a heading `## [<version>]` exactly. `CHANGELOG.md:22` still says `## [Unreleased]`.

Measured today:

```
$ sh tools/changelog-section.sh 0.5.0
changelog-section: no section for 0.5.0 in CHANGELOG.md.
  Expected a heading '## [0.5.0] - <date>' with at least one line under it.
  Sections found:
## [Unreleased]
exit=1

$ sh tools/changelog-section.sh Unreleased
exit=0   body: 112 lines, opens at "### Added"
```

**The fix is one line and it is the owner's, because it carries the version number.** Rename
`CHANGELOG.md:22` from `## [Unreleased]` to `## [<version>] - 2026-08-19`, then re-run
`sh tools/changelog-section.sh <version>` and expect exit 0 with a 112-line body. That body is what
the world reads on the releases page.

The CHANGELOG preamble (`CHANGELOG.md:14-16`) also says to re-run `conductor budget` and `conductor
money` when renaming, because a section that quotes a run's score is quoting a dated measurement.
**Checked: this section quotes none.** The only figures in the 112 lines are design claims (`~98%`
of session cost is cache read, `0.15%` more tokens, the CA rule numbers) - no run total, no dollar
figure. So the rename does not carry a re-measure obligation this time.

### 2. RED - moving the trackers turns the docs pin red in the same instant

`docs/dev/README.md:44-45` already names the destination: the brief and both trackers move together,
brief to `docs/history/`, both trackers to `docs/history/archive/trackers/`. But the move is **not**
a `git mv`. `tests/Conductor.Tests/SF7_1DocsMatchRealityTests.Karvansara.cs` pins the old locations
twice over, and both assertions fail the moment the files move:

- line 116 - `Assert.Contains("plans/karvansara/CORE-TRACKER.md", current)` - the Current-work
  section of `docs/dev/README.md` must literally name that path.
- lines 119-127 - `Assert.True(File.Exists(...))` over `docs/dev/KARVANSARA-PLAN-2026-08-13.md` and
  `plans/karvansara/CORE-TRACKER.md`, with the message "docs/dev/README.md points at {relative} and
  it is not there."

So the move is one commit containing all four of: the `git mv`s; the `docs/dev/README.md`
Current-work table rewritten to the new paths (its row at line 17 is explicit that the tracker
"stays here rather than in `history/` only until KS12.3 moves both trackers together"); this test
updated to the new paths; and the two plan files repointed -
`plans/karvansara/edge.plan.json:39` (`tracker`), `:40` (`planDoc`), `:229-230` (`readOrder`) and
`plans/karvansara/core.plan.json:36,37,225-226`. Do it after the run has ended, so no plan reload is
needed.

Swept and cleared, so the owner does not have to look:
`plans/karvansara/contracts/KS9-10.json` mentions the paths in contract prose only, nothing resolves
them. `src/Conductor.Core/Planning/PlanResolution.cs:9`,
`tests/Conductor.Tests/KS0_3PlanResolutionTests.cs:9` and
`tools/ks0/ks0-3-bug20-plan-resolution.ps1:5` point at `plans/karvan/` - the previous era, not this
one. `tests/Conductor.Tests/KS7_2HookGroundTruthTests.cs:238` carries the edge tracker path inside
an `InlineData` command string that is parsed, never opened.

### 3. GREEN - the merge cannot conflict

```
$ git merge-base --is-ancestor master feat/karvansara-edge   -> true
merge-base == master tip == ff3a987e2aa32b3e42de4a941632f92e2919e31d

$ git rev-list --left-right --count master...feat/karvansara-edge
0	76
```

master has not moved since the branch left it. 76 commits ahead, 0 behind: a fast-forward, zero
conflicts possible. Measured without checkout and without push.

### 4. GREEN - the backfill verb exists with the flags the checkpoint names

Probed through the **fresh build**, not the engine on PATH
(`dotnet run --project src/Conductor -- github sync --help`):

```
--backfill <RUN>    Push this run's whole board and diary. Run id, prefix, slug, repo name, or a path to a run.db
--repo <REPO>       Mirror INTO this repository, overriding the plan's github.repo
--dry-run           Reconcile and report what would change, writing nothing
--no-diary          Board only: skip the run issue and its per-session comments
--project <NUMBER>  Also mirror a Projects v2 board (needs a token with the 'project' scope). Refuses without it
```

**The run id to pass is `9491891fe700463ba0d876c06280cce2`** - read from the live `mcp-serve`
command line of this run. Note the run.db slug is
`conductor-karvansara-core---the-open-door-308cfb9b`, i.e. edge shares core's store, so a bare slug
argument is ambiguous between the two eras; pass the run id. `--dry-run` exists - rehearse with it
first. `--project` will refuse: the gh token on this machine lacks `project` scope.

### 5. GREEN, but re-check at the time - no foreign run is live

The precondition is "no other conductor run live on this machine". Measured now:

```
$ Get-CimInstance Win32_Process -Filter "Name='conductor.exe' OR Name='conductor-face.exe'"
19832  conductor.exe       ... run -p plans/karvansara/edge.plan.json     <- THIS run (CONDUCTOR_PID)
37932  conductor.exe       ... face
5132   conductor-face.exe  --url http://127.0.0.1:4317
1208   conductor.exe       mcp-serve ... --repo C:/code/conductor --session 23
```

All four belong to this repo. No BookToCourse process is up at this moment - but that run is
expected to share the machine (trap 3), so **re-run this exact command before reinstalling**; the
reinstall overwrites the binary both runs execute.

## The order to do it in

1. Rename `CHANGELOG.md:22` to the version; verify `sh tools/changelog-section.sh <version>` exits 0.
2. Fast-forward `master` to `feat/karvansara-edge`, push.
3. Tag `v<version>` and push the tag; the guard job prints the release body it will use.
4. Re-run the process check above; when clear, `tools/install.ps1`; confirm `conductor version`
   matches the releases page.
5. `conductor github sync --backfill 9491891fe700463ba0d876c06280cce2 --dry-run`, then for real.
6. Merge payesh PR shaahink/payesh#2.
7. The move commit, all four parts together (section 2), after the run has ended.

## Decisions only the owner can make

- **The version number.** Latest tag is `v0.4.1`. Edge added features and the CHANGELOG section
  declares no breaking change, which reads as `v0.5.0` under semver - but nothing in the repo states
  the intended number, so this runbook will not pick it.
- **Whether the two Karvansara runs join the published payesh corpus.** They are still excluded by
  `anonymise.json`. That is editorial, not mechanical.

## Carried in red from KS12.2, unchanged by this session

- analyzer-debt gate exits 1 (bug 60): pragma-src 33 against a bar of 31. Stated honestly in the
  docs, not fixed, and the bar may not be moved.
- payesh `npm run anonymity` is red on the word "website" (bug 41, pre-existing).
