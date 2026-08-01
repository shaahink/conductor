---
name: run-conductor
description: Build, run, and drive Conductor — the .NET orchestrator (`conductor` CLI) + Go face TUI. Use when asked to start/run conductor, smoke-test it end-to-end without burning tokens, build the engine or face, drive a plan loop, run `doctor`/`status`, or screenshot the dashboard.
---

Conductor is a long-running .NET orchestrator (`conductor.exe`, a Spectre.Console CLI) that spawns
**agent sessions** to drive a mega-plan, plus an optional Go/Bubble-Tea companion TUI (`face-go`).
You cannot smoke-test the engine against a real agent without spending model tokens — so the agent
path here is **`.claude/skills/run-conductor/driver.ps1`**, which stands in the token-free fake agent
(`tools/fake-agent.ps1`) and drives the real engine through a full session loop against a throwaway
repo, then asserts on the result.

All paths below are relative to the repo root (`C:\code\conductor-baton`). This is a **Windows /
PowerShell** project (SQLite `run.db`, PowerShell gates, `.exe` outputs) — commands are PowerShell,
not bash.

## Prerequisites

- **.NET SDK 10** (`dotnet --version` → `10.0.301` here) — builds/runs the engine.
- **Go 1.26+** (`go version` → `go1.26.5 windows/amd64`) — only needed to build the `face-go` TUI.
- **git** on PATH — the engine shells out to it, and the driver scaffolds a scratch repo.

No agent CLI (opencode/claude) is needed for the driver — that is the whole point of the fake agent.

## Build

Engine only (fast, what the driver uses by default — produces
`src\Conductor\bin\Debug\net10.0\conductor.exe`):

```powershell
dotnet build src\Conductor\Conductor.csproj -c Debug --nologo -v q
```

Full install (Release publish of the engine **and** the Go face, then a `conductor` shim on PATH):

```powershell
powershell -File tools\install.ps1
```

Face only:

```powershell
cd face-go ; go build -o bin\conductor-face.exe .\cmd\conductor-face\
```

## Run (agent path) — the driver

One command builds the engine if needed, scaffolds a hermetic scratch repo + toy plan + tracker,
runs `doctor` → `run --dry-run` → the real `run --headless` loop (fake agent) → `status`, and prints
a PASS/FAIL summary. **No network, no model calls, no touching the real `plans/`.**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\skills\run-conductor\driver.ps1
```

Ends with `DRIVER: PASS` and exit code 0. The scratch repo is deleted on exit unless you pass
`-Keep`.

| flag | what it does |
|---|---|
| `-Keep` | keep the scratch repo and print its path (`plan` + `.conductor\REPORT.md`) |
| `-Mode <m>` | fake-agent scenario: `success` (default), `no-commits`, `true-red`, `stall`, `limit` |
| `-Sessions <N>` | max agent sessions the loop runs (default 2) |
| `-Exe <path>` | use a specific `conductor.exe` (default: the repo Debug build) |
| `-NoBuild` | fail instead of building if the exe is missing |

What a passing run proves (this is Conductor's trust model in action): the fake agent hand-edits the
tracker row to `DONE` and commits, and the engine re-verifies independently rather than believing it.
The claim signal is the **work graph** — what `conductor task --done` or MCP `task_update` wrote
during the session — not the markdown (`VerdictEngine.cs`, W1.3). A tracker-only flip is not silently
honoured: it is **flagged and accepted via the transition fallback**, so an old-habit agent still
makes progress but does it loudly, with a `WARNING: … flipped DONE only in the tracker markdown … —
accepted via the transition fallback` and a `legacy-claim` row in the ledger.

Session #2 becomes a `Verify` session; the fake agent detects the verify prompt and answers with
`{"score":95,"findings":[],"verdict":"PASS"}` → `verifier passed (95/80)` and the workflow cycles back
to deliver.

> **Re-capture this transcript when you next run the loop.** An earlier revision of this page quoted a
> `— discarded` warning and `newly DONE []` / `checkpoints 0/2`. The engine has not behaved that way
> since W1.3 added the fallback: it emits different text and *counts* the flip. The exact console
> output of a `-Mode success` run at the current revision has not been re-recorded, so it is described
> above rather than quoted (SF7.1).

To inspect the artifacts after a run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\skills\run-conductor\driver.ps1 -Keep
```

### Driving individual verbs directly

Against any plan (`-p`). Verified this session:

```powershell
$exe = "src\Conductor\bin\Debug\net10.0\conductor.exe"
& $exe doctor  -p plans\conductor-maestro.plan.json          # <1s health check
& $exe run     -p <plan> --dry-run                            # print the next session prompt, spawn nothing
& $exe run     -p <plan> --headless --max-sessions 2          # the real loop, plain line output, no TUI
& $exe status  -p <plan> --no-llm                             # read run.db back, fast + offline
```

The authoritative verb list is `Program.cs` (or `conductor <verb> --help`) — see Gotchas, the
README's list has drifted.

## Run (human path) — the live dashboard + face

`conductor run` (no `--headless`) draws a live Spectre dashboard and auto-spawns the `face-go` TUI as
a child. Ctrl+C is always safe. Both are **terminal** apps — run them in a real terminal (Windows
Terminal / WezTerm), not a redirected pipe (see Gotchas). The face offline demo:

```powershell
face-go\bin\conductor-face.exe --demo    # fully offline, synthetic data, no engine needed
face-go\bin\conductor-face.exe --help     # flags: --demo, --url, --host, --port
```

## Test

```powershell
dotnet test Conductor.slnx
```

## Gotchas

- **README staleness (verb list, `--no-dashboard`, Runtime files/Trust model sections) fixed
  2026-07-17 (U0.3).** `conductor --help`/`Program.cs` is still the authoritative source going
  forward — re-check before trusting the README again if it's been a while. Still trust
  `Program.cs` (`AddCommand<...>`) over any doc if they ever disagree.
- **Runtime state is a SQLite `run.db` (schema v7), not `events.jsonl`/`state.json`.** A real
  `.conductor\` after a run holds: `run.db`, `REPORT.md`, `conductor.log`, `lessons.md`, `logs\`,
  `sessions\`, `.gitignore`. Assert on `run.db`. `state.json` is a legacy carrier the live run loop
  never writes (only a couple of standalone verbs like `conductor gate` still do); `RunContext.Save()`
  only ever calls `SqliteRunStore.SaveRunState` (run.db).
- **`run --dry-run` on a plan that's parked or complete loops instead of printing a prompt.**
  `plans\conductor-maestro.plan.json` is the self-referential plan, currently `NeedsHuman` at 30/30
  with a `HUMAN:` token in its handoff — a dry-run against it re-emits `NEEDS HUMAN` forever (kill it).
  dry-run only prints a prompt when there's a *next session* to run; use a fresh/TODO plan (what the
  driver scaffolds).
- **Command descriptions are Spectre markup — escape literal brackets.** `conductor --help` used to
  crash partway (`Could not find color or style '--all'`) because a description contained `[--all]`;
  it's `[[--all]]` now. Any new description with bracketed text needs the same doubling.
- **Claims come from the work graph, not the markdown — and a tracker hand-edit is flagged, not
  silently honoured.** `conductor task --done` / MCP `task_update` is the claim. An agent that instead
  edits the tracker to `DONE` still advances the checkpoint, via the W1.3 **transition fallback**, but
  the engine logs `WARNING: … flipped DONE only in the tracker markdown … — accepted via the
  transition fallback` and writes a `legacy-claim` ledger row naming it. The tracker is a generated
  view of `run.db`; treat a flip there as a legacy signal that will be reported on, not as a way to
  claim work quietly. (Corrected at SF7.1 — this bullet used to say such edits are *discarded* and the
  checkpoint does not advance, which the engine has not done since W1.3.)
- **`face-go` is a real Bubble Tea TUI — you cannot "screenshot" it by piping stdout.** Redirecting
  its output yields only terminal-negotiation bytes (`[?2026$p...`), not a frame, even with
  `FACE_FORCE_TTY=1` (that bypasses the interactive-terminal *check*, but a pipe still has no size).
  The engine's capturable surface is the `--headless` line output + `status`/`report` tables, which
  the driver drives and asserts on. To see the face rendered, run it in a real terminal.
- **`fake-agent.ps1` (and `driver.ps1`) must stay ASCII-only.** Windows PowerShell 5.1 reads a
  BOM-less UTF-8 script as ANSI; a stray non-ASCII byte silently tears a string literal and the
  harness fails without a clear error.
- **The self-referential Maestro plan wants the branch build.** Drive
  `plans\conductor-maestro.plan.json` with `dotnet run --project src\Conductor -- run -p ...` (or a
  fresh publish), not the globally installed snapshot.

## Troubleshooting

- **`Unexpected option 'version'` from `conductor --version`**: there is no `--version` verb. Run a
  real verb (`conductor doctor -p <plan>`).
- **`doctor` warns `agent opencode not found`**: only means the plan's default agent CLI isn't
  installed; irrelevant to the driver (its toy plan uses `powershell`, so doctor is green there).
- **Scratch commit fails `Author identity unknown`**: the driver sets repo-local `user.email`/`name`;
  if you scaffold by hand, do the same. Ensure `git` is on PATH.
