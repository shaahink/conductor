# CONDUCTOR-UX — U-series tracker (resume here)

Read order: `AGENTS.md` → `docs/CONDUCTOR-UX.md` (the spec — read your stage's section fully) →
this tracker → `face-go/STYLE.md` for any Face work.

## Handoff  (overwrite this block)

last: (none) — tracker authored 2026-07-16 from the owner's playground dogfood feedback; no
U-series session has run yet.
stage: **U0 NOT STARTED**.
gate: not run for this era (repo is green at authoring time: dotnet 849/849, go test ok, driver PASS).
next: **U0.1** — plan discovery in `PlanSettings.ResolvePlanPath` (see docs/CONDUCTOR-UX.md §U0.1).

## Rules

- One checkpoint landed with proof beats three claimed. Commit per checkpoint
  (`feat(cli): U0.1 …` / `feat(face): U1.1 …`), update your row (Status, Commit, Evidence).
- Never build on red. Gate battery: `dotnet build Conductor.slnx` + `go build ./...` (fast),
  `dotnet test` + `go test ./...` (full), driver.ps1 (truth). PowerShell files stay ASCII.
- Goldens are the eyes: visual checkpoints ship with reviewed golden frames
  (`go test ./internal/tui/ -run TestGolden -update` only after reading the frame).
- Do not touch: planner seams (P-series, done), the playground repo, plans/ other than this era's.

## Checkpoints

Status in TODO / IN PROGRESS / DONE / BLOCKED.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| U0.1 | plan discovery: -p optional, cwd/plans scan, picker, friendly errors | TODO | | |
| U0.2 | `conductor journey`: itinerary with stages, gates, human moments, resume state | TODO | | |
| U0.3 | gateless plans proven + resume story documented (README) | TODO | | |
| U1.1 | Home landing tab: Server / Run / Workspace / Next-steps panels, demo parity | TODO | | |
| U1.2 | workspace identity in the top bar (repo basename, full path on Home) | TODO | | |
| U2.1 | palette groups (Run/Stage/Danger) + consequence-naming confirms | TODO | | |
| U2.2 | Report tab is a visual run report (progress, stages, sessions, gates, scores) | TODO | | |
| U2.3 | Dev tab: SQL console moved + run internals + per-session token/cost stats | TODO | | |
| U3.1 | curated themes (mocha/latte/nord/gruvbox), --theme, live switch, persisted | TODO | | |
| U3.2 | golden glitch pass at 3 sizes — each fix noted in evidence | TODO | | |
