# SF7.2 — the release is tagged through the SC8 pipeline

Session 40. Owner had already closed the other two clauses on 2026-08-01 (recorded in the session-39
handoff): the merge (`8286d63`, verified below) and the reinstall waiver (a second conductor run is
live in `C:/Code/sk-studio`, so `tools/install.ps1` is not run this era). This session delivers the
remaining clause: the tag.

## 1. Merge, re-verified (not redone)

```
$ git log --oneline -1 origin/master
8286d63 merge: feat/sarban into master - the Sarban era
```

## 2. CHANGELOG cut

`## [Unreleased]` left empty; `## [0.3.0] - 2026-08-01` inserted directly beneath it, holding
everything that was under `[Unreleased]`. Pattern copied from the two prior cuts (`3991e7e` for
0.2.0, `0230036` for 0.2.2), not a heading rename.

Version: **0.2.2 → 0.3.0** (minor). The era's CHANGELOG entries are Added/Changed/Fixed only, no
breaking change; this mirrors the 0.1.0→0.2.0 minor bump for the prior era, and 0.2.1/0.2.2 were
patch cuts mid-era per the file's own note ("cut mid-era for the token-budget rails").

Dry-run of the exact check `release.yml`'s `guard` job runs:

```
$ tools/changelog-section.sh 0.3.0
<73 lines, the full 0.3.0 section — exit 0>
```

Commit (on `master`, via a scratch worktree so the dirty `feat/sarban` tree was untouched):

```
e897c2c chore(release): cut the 0.3.0 section - the Sarban face era ships
```

Pushed: `8286d63..e897c2c  master -> master`

## 3. Tag, pushed

```
$ git tag -a v0.3.0 -m "v0.3.0 - the Sarban face era" e897c2c
$ git push origin v0.3.0
 * [new tag]         v0.3.0 -> v0.3.0
```

## 4. `release.yml` ran for real, all green

`gh run watch 30710653729` — `guard` (changelog-section check), all 5 platform `build` legs
(linux-x64, linux-arm64, macos-arm64, macos-x64, windows-x64), and `attach to release` all
succeeded.

The linux-x64 leg's own assertion — the shipped **artifact**, not a second build, answering its own
`version` verb — is the proof the tag and the binary agree:

```
tag=0.3.0  binary=0.3.0+e897c2c7e1b0
```

## 5. The release exists and is complete

```
$ gh release view v0.3.0 --repo shaahink/conductor
url:     https://github.com/shaahink/conductor/releases/tag/v0.3.0
asset:   conductor-linux-arm64.tar.gz
asset:   conductor-linux-x64.tar.gz
asset:   conductor-macos-arm64.tar.gz
asset:   conductor-macos-x64.tar.gz
asset:   conductor-windows-x64.zip
asset:   SHA256SUMS.txt
```

Release body is the 0.3.0 CHANGELOG section (confirmed via `gh release view`).

## SF7.2 closure

Both remaining clauses of the checkpoint are now true: merge is on `master` (verified above), and
the release is tagged through the SC8 pipeline (v0.3.0, published, binary self-reports the tag).
The reinstall clause is waived for this run (recorded in the session-39 handoff and in
`.conductor/followups.md` as an owner-owed row) — not part of this checkpoint's closing evidence.

## Filed in passing (not blocking, not caused by this session)

Bug #23: `ci.yml`'s "windows - full gate battery" job is red on `master` right now —
`SF0_3PidsAndBackgroundWorkTests.McpBgStatus_CallsAnUninspectablePidRunning_NotDead` expects
`Unverifiable`, the GitHub-hosted Windows runner answers `Ours` or `Recycled` depending on the run.
Reproduces on commits that predate this session (`feat/sarban@3abe51c`, `master@8286d63` — the
merge commit itself, before any SF7.2 edit). `release.yml` has no `dotnet test` step, so this did
not block the v0.3.0 tag.
