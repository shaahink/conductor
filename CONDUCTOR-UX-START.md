# Conductor UX (U-series) Phase Tracker

**Plan:** Conductor UX (U-series) | **Branch:** `feat/foreman` | **Design doc:** docs/CONDUCTOR-UX.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: owner-driven from Claude Code (the engine run died — session #12 hung 5.5h past its 90m
timeout, then #13 hit an expired OAuth token; engine process gone). Finished U3.3 part 2 by hand.
**STAGE U3 IS CLOSED, 3/3. THE U-SERIES PLAN IS COMPLETE (U0..U3 all done).**
done: **U3.3 part 2** (`43c1e4b`) agent-terminal vibe. Transcript reads like the driving CLI: tool
lines name-first (bold name, dim arg), results indented under their call, thinking collapsed to the
current thought + `+N lines (T to expand)` tail. Glyphs follow the RESOLVED provider off /state —
claude `●/⎿/✻`, opencode `◆/└/◇`, neutral house set for `""`/`text` (never a guess). Footer strip
(model · elapsed · tokens · cost). `ctrl+c` double-tap to quit (first arms + hint toast; `q`
unguarded), `esc` backs out one layer. Goldens pin BOTH renderings (agent + agent_opencode).
gate: build 0w/0e, **918/918**, ratchet OK (832 tests / 38≤38 / archdebt 0, nothing weakened), go
build/vet/test green + gofmt clean. `.conductor/gate-u33p2.out`. Part-1 seam `providerLabel()` and
new `glyphsFor()` both treat `""` as unknown-older-engine, NOT "not claude".
next: **plan is closed — no U-work remains.** If reviving the engine: re-auth Claude first
(`claude setup-token`), then it will still misdispatch (bug #6). Two engine bugs surfaced this run,
both worth filing before the next headless run relies on the timeout.
bugs filed: **#8 (HIGH, NEW)** the 90-minute session timeout did not fire on a live clock — session
#12 ran **337m** before being killed at 13:02; 5.5h of wall time lost to a hung session. **#7**
(medium) `HostLoggingTests.DryRun…CorrelationProperties` order-dependent (896/897 once; passes
isolated). **#6** verifier-misdispatch still open — it dispatched Verify against U3 twice while the
work was undelivered/committed, and logged `newly DONE []` after 81m of real delivery.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 11 |
| Done | 0 |
| Claimed (unconfirmed) | 11 |

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
| U3.1 | curated themes (mocha/latte/nord/gruvbox), --theme, live switch, persisted | DONE | - | .conductor/gate-u31.out (build 0w/0e - 897/897 - ratchet OK 826/38/0) - commit 45e8fba - goldens palette+help - TestEveryThemeIsLegibleOnItsBase + TestContrastGateIsNotVacuous + TestLiveThemeSwitchRepaintsTheWholeFrame |
| U3.2 | golden glitch pass at 3 sizes, seeded from the spec's dogfood appendix | DONE | - | .conductor/gate-u32.out (build 0w/0e - 897/897 - ratchet OK 826/38/0 - go green) - commit 4fc2c61 - SIZES=ADD not replace: TestGoldenSizes keeps 80x24/120x30/200x50 (M5 gate, only wide coverage), glitch_sweep_test.go ADDS every-tab at 132x40/100x30/80x24 - fixes: Home clipped its Next steps at 100x30+80x24 and size_80x24.golden was PINNING that clipped page (now tiered+shed, 18/18 and 24/24, goldens size_80x24+size_120x30); appendix 5 kanban (cmdFetchTasks swallowed the error - now never-fetched/unreachable/empty distinguished + banner over a stale board, golden kanban_unreachable); appendix 6 timeline live rule (golden timeline_live + timeline footer); appendix 8 padBetween sacrifices left (goldens agent+search); TestFrameNeverExceedsWindowHeight never left Home so its transcript worst-case was never drawn - fixed |
| U3.3 | agent-terminal vibe: Claude Code-style transcript, provider-aware, footer strip | DONE | 43c1e4b | .conductor/gate-u33p2.out (build 0w/0e · 918/918 · ratchet OK 832/38/0 · go green · gofmt clean) · commit 43c1e4b · goldens agent + agent_opencode pin BOTH provider renderings · tool name/arg split, `+N lines (T to expand)` collapse tail, footer strip (model·elapsed·tokens·cost), ctrl+c double-tap + q unguarded + esc back-out · new tests: TestCollapsedThinkingShowsExpandTail, TestToolLineSplitsNameFromArg, TestGlyphsFollowProvider, TestTranscriptProviderSwitchesGlyphs, TestCtrlCDoubleTapToQuit + 2 |

## Dependencies

```
U0 → U1
U1 → U2
U2 → U3
```
