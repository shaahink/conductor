# Dogfood runbook — read this first if the run looks stuck, dead, or wrong

Not design docs and not progress. This is what a fresh session — any model, including a cheap one —
needs to know to diagnose a live or dead `conductor run` the way we've been doing it by hand. Keep
this updated when a new failure class shows up; it's a living doc, not a snapshot.

> Written during the Maestro (M) era, so it names that era's files: the design doc was
> `docs/history/MAESTRO-PLAN.md` and the tracker `MAESTRO-TRACKER.md` (now
> [`docs/history/archive/trackers/`](history/archive/trackers/)). Substitute the current pair — see
> [`docs/README.md`](README.md). The triage itself is era-independent.

## The mental model

`conductor run -p plans/conductor-maestro.plan.json` is ONE process tree: the .NET engine, an
HTTP+SSE control plane (`http://127.0.0.1:4317` by default, next free port if taken), and the Face
(`face-go`, a self-contained Go binary spawned as a child process). The engine is built FROM THIS BRANCH —
deliberately, so a regression in Conductor's own code is caught on the very next run, not months
later (see plan.json's header comment).

**The coding agent is OpenCode running DeepSeek** (`opencode run -m deepseek/deepseek-v4-pro`), a
separate process spawned per session — not Claude Code, not this conversation. When you see tool
calls and commits happening, that's DeepSeek, driven by whatever prompt `templatesDir` compiled.

## Where the truth lives

| File | Tells you |
|---|---|
| `.conductor/state.json` | Current stage, session counter, `status` (idle/running/...), `updatedUtc`. **Staleness is the signal**: if `updatedUtc` is far behind wall-clock and no `conductor.exe` process is running, it died without a clean save. |
| `.conductor/logs/conductor-YYYYMMDD.log` | The structured engine log (Serilog). Tail it first. If it stops mid-session with no `cancelled — saving state` or `session #N exited` line, that's an *unclean* death (see Known gaps below). |
| `.conductor/logs/crash-*.log` | Added 2026-07-12 (commit `05e18ff`). Best-effort forensic dump from `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException` / Spectre's exception handler. If one exists near the time of death, that's your root cause. If none exists, the process was likely killed abruptly (see Known gaps). |
| `.conductor/logs/session-NNN.jsonl` / `.prompt.md` | Raw per-session agent I/O and the exact compiled prompt sent. |
| `.conductor/REPORT.md` | Human-readable stage/checkpoint progress snapshot, regenerated each session. |
| `.conductor/followups.md` | Tracked bug ledger, git-tracked, `owning stage` column — the mechanism for "log this so the right stage's session reads it." |
| `.conductor/run.db` | SQLite: sessions, gates, ledger, pid tracking. `conductor task --list` / `conductor log --query ...` read it. |
| `MAESTRO-TRACKER.md` | Checkpoint status table, re-seeded into `run.db` on every startup — don't trust hand-edits to it. |
| `git log` / `git status` | The only durable record of real progress. State files can be stale; commits can't. |

## Diagnostic procedure (what we actually did, twice)

1. **Is it alive?** `tasklist | grep -i -E "conductor|node|opencode"` (or Task Manager). No
   `conductor.exe` = the run is not running, regardless of what the Face window shows.
2. **Read the log tail.** `.conductor/logs/conductor-YYYYMMDD.log`, last ~40 lines. Look for the
   last line before silence.
3. **Check for a crash dump.** `ls .conductor/logs/crash-*.log`, newest first, near the time the
   log went quiet.
4. **Check `state.json`.** `status`, `updatedUtc`, whether `pendingResume` is set. A present
   `pendingResume` means it went through the clean cancellation path (Ctrl+C was caught). Its
   *absence* alongside a stale `updatedUtc` means it didn't — window closed, or a crash bypassed
   even the `finally` unwind.
5. **Check the working tree.** `git status --short`. A crashed session commits nothing until it
   finishes a checkpoint — uncommitted changes are real in-flight work, not corruption. Check
   whether it still builds (`dotnet build src/Conductor/Conductor.csproj` — build the app project
   alone if the test project is mid-edit) before assuming anything is broken.
6. **Don't assume the ratchet gate failing means something you did is wrong.** A half-finished
   split (e.g. deleting `Commands.cs` mid-way through creating per-command files) will fail the
   test-count floor honestly — that's the gate doing its job on unfinished work, not a regression.
7. **Resume**: `conductor run -p plans/conductor-maestro.plan.json`. The next session's prompt
   template already tells the agent to run `git status`/`git log`, re-read the tracker handoff,
   call `ledger_list`, and finish or safely revert whatever was in flight.

## Known gaps (real, not yet fixed)

- Crash-log safety net (commit `05e18ff`) tells you a crash happened and where, but doesn't
  prevent one or recover in-flight work beyond what git already has.

## Fixed so far (context for "didn't we already deal with this?")

| Symptom | Root cause | Fix |
|---|---|---|
| Face never appears / TUI never attaches | `ProcessSupervisor.ReapOrphans()` ran at startup right after spawning the Face and killed it as a false-positive "orphan" — it never checked its own in-memory tracked-process table. 100% reproducible, ~150–400ms after every launch. | `d0c977c` |
| QA ran after every single delivery session, not once per phase | `ShouldVerify()` was unconditional (`kind == Deliver`), no plan-level knob. | `8e0186c` — added `PlanConfig.VerifyEachDelivery` (default true); Maestro sets it `false` |
| No stage-end audit/fix pass | `audit.enabled` was unset (= disabled) in the plan | `d0c977c` — `"audit": { "enabled": true, "enableParallel": false }` |
| `conductor status` / Face `G` key did nothing useful | `statusAgent` was unset (= disabled) | `35435f6` — enabled, pointed at `deepseek/deepseek-chat` (cheap) |
| Silent process death with zero trace | No crash-log path independent of the DI-built logger; console-only exception output invisible under the Face's alt-screen | `05e18ff` |
| **Closing the terminal window/tab killed the run ungracefully** — state unsaved, no resume queued, indistinguishable from a crash after the fact. This runbook used to say "always stop with Ctrl+C, never by closing the window". | `Console.CancelKeyPress` only catches `CTRL_C_EVENT`/`CTRL_BREAK_EVENT` — not `CTRL_CLOSE_EVENT` (window/tab close), logoff or shutdown. | `c8f9b56` (W3.3) — `ConsoleCtrlRails` wires all three into the same graceful stop and **blocks inside the OS handler until the save completes** (Windows kills on return, so returning early makes the save decoration). Proven end to end by `tools/w3/window-close.ps1`, which posts a real `WM_CLOSE` to a real run's console window: 18/18, process dead 0.42s later, session recorded `Interrupted`, lock released, and the next `conductor run` resumed and finished the work. Closing the window is now a supported way to stop a run. |

## Current plan-level config (`plans/conductor-maestro.plan.json`) and why

- `gatePolicy: "perPhase"` + `verifyEachDelivery: false` + `audit.enabled: true` (sequential, not
  parallel) → Deliver sessions chain within a stage; QA happens once, at stage-end (full battery +
  one audit/fix session), not after every checkpoint.
- `statusAgent.model: "deepseek/deepseek-chat"` → cheap on-demand status reports, separate from the
  heavier delivery model.
- `stageSlackFactor: 2` (in `limits`) → a stage's attempt ceiling is `sessions × 2` before it
  escalates (e.g. M1's `sessions: 4` → 8 attempts).

## Cheat sheet

```
dotnet build src/Conductor/Conductor.csproj          # app only, skips a possibly-mid-edit test project
dotnet test Conductor.slnx --no-build                # full suite
powershell -File tools/gates/ratchet.ps1              # the anti-cheat gate, standalone
conductor status -p plans/conductor-maestro.plan.json # instant, straight from run.db, no model call
conductor status --deep -p ...                        # adds an LLM narrative on top (slow, opt-in)
conductor doctor -p plans/conductor-maestro.plan.json # what will happen on resume, no agent spawned
conductor task --list -p plans/conductor-maestro.plan.json
conductor log --query "stage=M1 and outcome=fail" -p plans/conductor-maestro.plan.json
git log --oneline -10 && git status --short
```
