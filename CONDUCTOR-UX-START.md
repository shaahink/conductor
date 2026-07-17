# Conductor UX (U-series) Phase Tracker

**Plan:** Conductor UX (U-series) | **Branch:** `feat/foreman` | **Design doc:** docs/CONDUCTOR-UX.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: session #8 (Deliver, U2). Killed once mid-session; nothing was lost (it had only READ, tree
was clean). **STAGE U2 IS CLOSED, 3/3.**
qa: s7's U2.1 claim **audited against fresh artifacts and CONFIRMED** — verb grouping matches the
spec verb-for-verb, `⚠` on unsafe rows, confirm reads `abort — kill session + stop conductor. y/N`,
both its new tests pass, and every control send routes through the confirm path (no destructive
hotkey bypasses it). Nothing over-claimed.
done: **U2.2** (`c8ff55f`) Report is now a rendered report — header/progress/stages/sessions
digest/gates/verifier scores from `/state`+`/sessions`, scroll-only. **U2.3** (`8749704`) Dev tab
(`d`) = the moved SQL console (unchanged, tests moved with it) + run internals + per-session
token/cost stats. `GET /sessions` now serves per-session cost+tokens, SUMMED via correlated
subqueries (s7's warning was right: `costs` holds many rows per session — a JOIN triples every
figure; 4 new tests are shaped to fail on a join, not just on a wrong number).
gate: green — build 0w/0e, **897/897**, ratchet OK (826 tests / 38≤38 pragmas / archdebt 0,
nothing weakened), face-go build/vet/test green + gofmt clean. Artifacts `.conductor/gate-u22.out`,
`.conductor/gate-u23.out`.
traps: **contention is probabilistic, not deterministic** — 897/897 passed twice WHILE the
DevContext2 suite ran, and bug #3 passed too; a green run does not clear those flakes, a red one
does not prove a defect. Inspect the box first, and never `Stop-Process dotnet` (it would kill
another repo's suite + a live web server). Bug #2 is real and still bites: `conductor bg` logs are
BOM-only 3 bytes for anything slow — redirect to your own file. Do NOT put double quotes or `>` in
`conductor note` text (shim re-splits); call the exe, not the scoop shim.
next: **U3** (`U3.1` themes → `U3.2` glitch pass → `U3.3` transcript vibe). U3.1 turns
`widgets/style.go`'s palette into a `Theme` + `ApplyTheme(name)`; note U2.2 added `infoStyle` to
view.go's shared var block and exported `widgets.StageGlyph`/`GateGlyph` (Report and the sidebar now
share ONE vocabulary — a second copy is what made finished stages render `○` in Report). Read the
ledger's two rendering traps first: measure RENDERED lines not slice elements, and gutter labels
must be < homeLabelW(11). And assert the RENDER, not the state — a state-only assertion passed a
pane whose scroll did nothing.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 11 |
| Done | 0 |
| Claimed (unconfirmed) | 6 |

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
| U2.1 | palette groups (Run/Stage/Danger) + consequence-naming confirms | DONE | 26a4194 | face-go/internal/tui/testdata/golden/palette.golden + palette_confirm.golden + help.golden · go build/vet/test green, gofmt clean · QA'd by s8 against fresh artifacts: confirmed |
| U2.2 | Report tab is a visual run report (progress, stages, sessions, gates, scores) | DONE | c8ff55f | .conductor/gate-u22.out (build 0w/0e · 897/897 · ratchet OK 826/38/0 · face-go green) · goldens report + report_scrolled + dev |
| U2.3 | Dev tab: SQL console moved + run internals + per-session token/cost stats | DONE | 8749704 | .conductor/gate-u23.out (build 0w/0e · 897/897 · ratchet OK 826/38/0 · face-go green) · golden dev_scrolled |

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
