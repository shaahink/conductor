# Contributing

Thanks for looking. Conductor is a small, opinionated codebase with unusually strict gates, so this
page is mostly about **how work is verified** — that is the part that will surprise you.

## The short version

1. Read [`README.md`](README.md) → **Requirements** and **Platform**. Windows is the supported host.
2. Build and run the gate battery before you change anything, so you know the tree was green when
   you started.
3. Make the change.
4. Run the gate battery again. All four commands, in order.
5. Open a PR. CI runs the same battery on `windows-latest` plus a cross-platform compile job.

## The gate battery

```powershell
dotnet build Conductor.slnx
dotnet test  Conductor.slnx
cd face-go; go build ./...; go vet ./...; go test ./...
powershell -File tools/gates/ratchet.ps1     # exact path — a wrong path exits 0 and proves nothing
```

`.github/workflows/ci.yml` runs exactly these. If they drift apart, CI stops being evidence.

### Warnings are errors

`Directory.Build.props` sets `TreatWarningsAsErrors` and turns analyzers on at `latest`. A build
that warns does not build. Fix the warning; do not suppress it.

### The ratchet

`tools/gates/ratchet.ps1` is an anti-cheat gate. It does not check whether the code is *right* — the
tests do that. It checks whether the **bar moved down**:

- the test count may never fall below the recorded floor, and tests may not be deleted;
- analyzer suppressions (`#pragma warning disable`) may never exceed the recorded ceiling;
- `TreatWarningsAsErrors` may not be turned off;
- architecture debt (`tests/Conductor.Tests/architecture-baseline.json`) may only shrink, and no new
  file may be added to the over-ceiling list;
- gate commands in `plans/` may not be changed, and **the gate scripts themselves may not be edited**
  — you do not get to edit the referee.

If you genuinely need to raise a ceiling, say so in the PR description and explain why the rule is
wrong *here*. It is a human decision, not a diff.

The interlock is deliberate: `tests/Conductor.Tests/ArchitectureTests.cs` enforces the design
(file-size and type ceilings, layering), and the ratchet makes deleting those tests impossible,
because the test count may never fall. Neither is escapable without the other noticing.

## What a good change looks like

- **Evidence, not assertion.** This project's whole thesis is that a claim is worth nothing without
  an independent check. A PR that says "fixed" and a PR that adds the test which fails before and
  passes after are not the same PR.
- **Test the behaviour, not the implementation.** The suite leans heavily on *live* tests: real
  child processes, a real HTTP control plane, a real `run.db`. That is on purpose — see below.
- **Match the surrounding code.** Comment density in particular: this codebase explains *why*, at
  length, where the reason is non-obvious, and says nothing where the code is plain.

### A note on in-process tests

Twice now, a suite that was entirely green hid a defect that only appeared when the shipped binary
was driven from outside itself (see the W2 and W5 sections of
[`CONDUCTOR-WORKGRAPH.md`](CONDUCTOR-WORKGRAPH.md)). A harness we wrote ourselves is too lenient to
be evidence for anything agent-visible or process-visible. If your change touches the wire — the MCP
server, the control plane, the prompt an agent actually receives — prefer a test that goes through
the real surface.

`tools/w5/rehearsal.ps1` drives the real binary end to end with **no credentials and no spend**
(~90 seconds). It is the cheapest way to find out whether you broke the loop.

## Testing without spending money

- `tools/fake-agent.ps1` impersonates an agent CLI's stream-json and can simulate success, stall,
  red gates, and usage limits.
- `powershell -File tools/w5/rehearsal.ps1 -Keep` is the full dress rehearsal.
- `powershell -File tools/w3/window-close.ps1` proves the console-close rail by posting a real
  `WM_CLOSE` to a real run's window, with a hard-kill negative control
  ([write-up](docs/workgraph/W3-WINDOW-CLOSE.md)). Windows only, and interactive — a console window
  appears on the desktop and is closed programmatically. Not part of CI: a runner has no window
  station to close.
- `cd face-go; ./bin/conductor-face.exe --demo` explores the whole dashboard offline.

No test in this repo should require an API key.

### Golden frames

`face-go/internal/tui/testdata/golden/*.golden` are byte-for-byte snapshots of rendered TUI screens.
After an intentional layout change:

```powershell
cd face-go
go test ./internal/tui/ -run TestGolden -update
```

Read the diff before committing it — that is the review. The README's demo GIF used to be assembled
from these same frames; it is now a live recording of the binary (`tools/demo/make-demo-gif.ps1`),
so re-record it after a layout change you want the README to show.

## Commits and PRs

- Conventional-commit prefixes (`feat:`, `fix:`, `docs:`, `ci:`, `test:`, `chore:`), with the
  checkpoint id when there is one: `feat(workgraph): W4.2 - one command from an idea to a plan`.
- Say what the change *does* and what would have caught it going wrong. Keep the body wrapped.
- One logical change per PR where you can manage it.

## Project layout

```
src/Conductor/        the engine + CLI (net10.0)
  Core/               orchestrator loop, gating, store, planning, hosting
  Commands/           one file per CLI verb
  Models/             PlanConfig and friends
face-go/              the dashboard (Bubble Tea) — see face-go/STYLE.md before touching the UI
tests/                one xUnit project; live tests spawn real processes
tools/                install, gates, fake agent, rehearsal, demo generator
plans/                Conductor's own plans (it drives itself)
examples/             ready-to-run plans that are NOT part of the engine
docs/                 see docs/README.md for the index
```

Architecture decisions live in [`docs/baton/adr/`](docs/baton/adr/). If you are changing something
an ADR settled, amend the ADR in the same PR.

## Reporting bugs

Open an issue with: what you ran, what you expected, what happened, and — if a run was involved —
the tail of `.conductor/conductor.log` plus `conductor status` output. Security issues go to
[SECURITY.md](SECURITY.md) instead, not to the issue tracker.
