# Conductor UX (U-series) Phase Tracker

**Plan:** Conductor UX (U-series) | **Branch:** `feat/foreman` | **Design doc:** docs/CONDUCTOR-UX.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: session #11 (FIX, U3). Session #10 was right: **U3 had never been delivered** — nothing to
verify. Delivered it from a clean tree instead. **U3.1 + U3.2 DONE, U3.3 PART 1 of 2.**
done: **U3.1** (`45e8fba`) `widgets.Theme` = 16 roles + registry (mocha/latte/nord/gruvbox),
`--theme` (one launch; bad name = exit 2), palette **Face** group switches live AND persists to
`os.UserConfigDir()/conductor-face/config.json`. **U3.2** (`4fc2c61`) glitch pass. **U3.3 part 1**
(`b69f761`) `/state.provider`, RESOLVED not raw; agent strip names the CLI.
gate: green at every commit — build 0w/0e, **918/918** (+21), ratchet OK (832 tests / 38≤38 /
archdebt 0, nothing weakened), go build/vet/test green + gofmt clean. `.conductor/gate-u31.out`,
`gate-u32.out`, `gate-u33.out`.
traps (NEW, cost me real time): **the goldens were pinning the bug** — `size_80x24.golden` had Home
missing its whole Next steps section since the day it was written and matched itself forever; a
golden proves a frame UNCHANGED, never CORRECT. Same shape twice more:
`TestFrameNeverExceedsWindowHeight` built 30 transcript events then asserted against **Home**
(never switched tabs), and `cmdFetchTasks` swallowed its error so a broken `/tasks` could not be
told from an empty board *at the wire*. Prefer an invariant (does the body fit `paneRows()`?) over a
pinned frame. Older traps still true: bug #2 (`conductor bg` logs are BOM-only 3 bytes — redirect to
your own file; inline `powershell -Command` after `--` gets re-split, use `-File`); no double quotes
or `>` in `conductor note`; call the exe, not the scoop shim; `CONDUCTOR_PLAN` must be set (7 plans).
next: **U3.3 part 2** — the wire is done, the presentation is not. Build: Claude-Code transcript
(`●` tool bullets, bold name + dim one-line arg, results indented under their call), thinking
dim-italic collapsed past ~3 lines with a `+N lines (T to expand)` tail, session footer strip
(model/elapsed/tokens/cost), `ctrl+c` double-tap to quit + single-tap hint toast, `esc` backs out
one layer, goldens for BOTH provider renderings. `providerLabel()` in tab_agent.go is the seam;
`""` = older engine = unknown, NOT "not claude". Then U3 is 3/3 and the plan is closed.
bugs filed: **#7** (medium) `HostLoggingTests.DryRun…CorrelationProperties` is order-dependent —
asserts the FIRST runId line in a globbed log is its own; a concurrent test's run fails it (hit
once, 896/897; passes 5/5 isolated; re-ran full suite green twice). Unrelated to U3, left unfixed.
**#6** (the verifier-misdispatch defect) is still open and will keep misdispatching every stage.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 11 |
| Done | 0 |
| Claimed (unconfirmed) | 8 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### U0 — Engine: start, resume, journey

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U0.1 | plan discovery: -p optional, cwd/plans scan, picker, friendly errors | DONE | fbdef79 | build:OK · face-build:OK |
| U0.2 | `conductor journey`: itinerary with stages, gates, human moments, resume state | DONE | fbdef79 | build:OK · face-build:OK |
| U0.3 | gateless plans proven + resume story documented (README) | DONE | fbdef79 | build:OK · face-build:OK |

### U1 — Face: landing page + workspace identity

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U1.1 | Home landing tab: Server / Run / Workspace / Next-steps panels, demo parity | DONE | db9244a | build:OK · face-build:OK |
| U1.2 | workspace identity in the top bar (repo basename, full path on Home) | DONE | db9244a | build:OK · face-build:OK |

### U2 — Face: controls, visual report, dev stats

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U2.1 | palette groups (Run/Stage/Danger) + consequence-naming confirms | DONE | - | commit 26a4194 · face-go/internal/tui/testdata/golden/palette.golden + palette_confirm.golden + help.golden · go build/vet/test green, gofmt clean |
| U2.2 | Report tab is a visual run report (progress, stages, sessions, gates, scores) | DONE | c8ff55f | build:OK · face-build:OK |
| U2.3 | Dev tab: SQL console moved + run internals + per-session token/cost stats | DONE | c8ff55f | build:OK · face-build:OK |

### U3 — Face: themes, agent-terminal vibe, glitch pass

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U3.1 | curated themes (mocha/latte/nord/gruvbox), --theme, live switch, persisted | TODO |  |  |
| U3.2 | golden glitch pass at 3 sizes, seeded from the spec's dogfood appendix | TODO |  |  |
| U3.3 | agent-terminal vibe: Claude Code-style transcript, provider-aware, footer strip | TODO |  |  |

## Dependencies

```
U0 → U1
U1 → U2
U2 → U3
```
