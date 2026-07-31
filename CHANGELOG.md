# Changelog

All notable changes to Conductor are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**This file is not decoration — it is load-bearing.** The version a binary reports is derived from
the `v*` git tags by MinVer (`src/Conductor/Conductor.csproj`), and `.github/workflows/release.yml`
refuses to publish a tag whose section is missing from this file, using the section as the release
body. So: add to `[Unreleased]` as you work, and rename it to the version when you tag.

Between releases, `conductor version` answers with a tag-height prerelease such as
`0.1.1-alpha.0.54+1c2330f5a47e` — patch bumped, `alpha.0.<commits since the tag>`, plus the commit
it was built from. It orders above `0.1.0` and below `0.1.1`, and it is unique per commit.

## [Unreleased]

## [0.2.0] - 2026-07-31

The Sarban core era: the engine says what it knows. Truthful surfaces, board correction verbs,
detach, structured transcripts — and the engine now knows what version it is and can update itself.

### Added

- `conductor version` and `GET /version` report the semver, the git commit, whether the tree was
  dirty at build time, the build date, and **which binary answered** — stamped by the build itself
  rather than typed into a source file, so "is this run using stale engine code" has an answer.
- Automatic tag-height versioning. The csproj no longer carries a hand-typed version number; the
  `v*` tags are the single source of truth, and a release binary answers with its own tag.
- `conductor update` — checks the latest release, and swaps this binary for it. It verifies the
  download against the release's `SHA256SUMS.txt`, then **runs the downloaded engine and asks its
  version** before replacing anything, and swaps by rename so a failure puts the old binary back.
  It **refuses while a run is live**, because every task claim and background start during a session
  spawns the engine again — a mid-run swap means the back half of a session runs on different code.
  `--check` looks without installing.
- `doctor` gained an `update` line: which engine is running and whether a newer release exists. Never
  a failure (an offline machine is not a broken one), memoised for six hours in a user-level cache so
  it costs nothing, and switchable off with `--no-update-check` or `CONDUCTOR_NO_UPDATE_CHECK`.
- Releases now publish a `SHA256SUMS.txt` manifest alongside the platform archives.
- `CHANGELOG.md` (this file), enforced at release time by `tools/changelog-section.sh`.
- `conductor task --blocked-until`, `--todo`, `--blocked`, `--skipped` and `--amend`, so the board
  can be corrected rather than only appended to.
- `conductor run --detach`: the engine takes its own process group, prints its pid and control-plane
  URL, and survives the shell that launched it.
- Per-session digests on `/sessions` — tool mix, files touched with counts, claims, and background
  work as a storyline — built from structured tool events rather than truncated JSON blobs.
- A `RUN-SUMMARY.md` at the end of a run; `report` and `status` work offline from `run.db`.

### Fixed

- The release build could never have compiled. `-p:PublishSingleFile=true` — the flag every platform
  in `release.yml` publishes with — enables the single-file analyzer, and two reads of
  `Assembly.Location` were IL3000 errors under `TreatWarningsAsErrors`. Since that workflow only runs
  on a `v*` tag push, nothing had ever exercised it. Both reads now use `AppContext.BaseDirectory`.

### Changed

- `install.ps1` and `install.sh` print the version they replaced and the version they installed
  (before → after), so an operator can confirm a rebuild actually took. `install.ps1` gained
  `-SkipShim`.
- Telegram starts on every run path, and `/telegram/status` carries a derived `willDeliver` verdict
  instead of leaving delivery to be discovered at the end of a run.
- `conductor status` no longer reports a healthy run as interrupted while a gate is executing.
- Gate verdicts wait for the session's tracked background children, retry a failed required gate
  once, and judge the work rather than the environment.
- `doctor` fails on configurations that used to fail silently at runtime: a model set without the
  model token, unknown `RunIf`/`SkipIf` tokens, literal braces in prompt text, zero-gate stages.
- History squashing runs after the stage's final state write, works on a dirty tree, reports real
  counts, and un-marks the stage when it fails instead of claiming success.

## [0.1.0] - 2026-07-28

### Added

- First public release. Conductor is a stateful execution environment for days-long AI software
  engineering: a .NET orchestrator (`conductor`) that drives a plan of stages and checkpoints one
  verifiable session at a time, and a Go TUI (`conductor-face`) that watches it.
- `conductor demo` — a complete plan driven to a confirmed finish with no credentials and no spend.
- Self-contained release archives for linux-x64, linux-arm64, macos-arm64, macos-x64 and
  windows-x64, each holding the engine and the Face side by side; neither .NET nor Go is needed to
  run them.
- `tools/install.ps1` and `tools/install.sh` for building and installing from source.
