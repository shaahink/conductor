# SC2.1 — `status` never calls a healthy run interrupted

**Claim.** During the verdict window — the agent process gone, the engine working through the gate
battery — `conductor status` reports the run as running. When the engine is actually gone it still
says interrupted, including when a stale lock file names a pid the OS has since handed to someone else.

## What was wrong

`SessionFinished` is emitted by `RunLoop.cs:365`, *after* `SessionRunner.RunAsync` returns — and
`RunAsync` calls `VerdictEngine.EvaluateSessionAsync`, which runs the **whole gate battery** first
(`VerdictEngine.cs:247`, `RunGateBatteryAsync`). So for the entire battery — 1m38s for this repo's
`engine-full` alone — run.db holds a `SessionStarted` with no `SessionFinished`.

`StatusReportBuilder.RunHasLiveProcess` answered "is anything still alive?" from `store.GetAllPids`,
which tracks only **spawned children** (agents, `bg` jobs). The agent's row is closed the moment it
exits, and gate commands were never tracked there at all. Zero live pids → the interrupted branch →

    interrupted mid-session — #1 in T0 never finished; resume with `conductor run`

which advises starting a *second* engine against a live run.

## The fix

The engine already published its own liveness and nobody read it: `RunLoop` writes its pid to
`.conductor/conductor.lock` on start and deletes it on stop. `EngineLock` (new, `src/Conductor/Core/EngineLock.cs`)
now owns that file for both readers:

- it carries **pid + ISO start stamp**, because a pid alone is not an identity — a lock left by a dead
  engine names an id the OS may have recycled. `PidLiveness.LooksAlive` settles it exactly as it does
  for spawned pids (`Ours` or `Unverifiable` = alive; `Gone` or `Recycled` = dead);
- files written by an older engine hold a bare pid and still parse, degrading to the existence check
  the old `AcquireLock` did — so an in-flight upgrade neither loses the mutex nor fakes liveness;
- `RunLoop.AcquireLock`/`ReleaseLock` are now thin calls onto it, so the writer and the reader can
  never drift apart.

`StatusReportBuilder` consults it only after the pids table comes up empty, and says which kind of
liveness it found. Spawned-pid liveness also gained the start-time check (the seam is now
`Func<int, DateTime, bool>`, defaulting to `PidLiveness.LooksAlive`), so a stale unexited row cannot
fake *running* either — the same lie in the other direction.

## Live proof — `SC2.1-live-verdict-window.txt`

The fresh build (`src/Conductor/bin/Debug/net10.0/conductor.exe`, stamped 05:21:28) driving a scratch
repo with its own plan and its own `.conductor` under `TEMP/sarban-proofs/sc21` — never this repo.
Rig: an agent that commits and exits in ~1s, and one gate that sleeps 150s, so the window is wide
enough to sample. Scratch engine log: `SC2.1-scratch-engine.log`.

| time | fact |
|---|---|
| 05:21:49 | engine pid 37524 starts; lock file holds `37524` + `2026-07-31T04:21:49.09Z` |
| 05:21:50 | `session #1 exited (code 0)` — no `SessionFinished` will land until the battery ends |
| 05:21:50 | `gate slow-battery` starts — **the verdict window is open** |
| 05:21:51 / 05:22:02 / 05:22:13 | three `status` samples: `running — session #1 in T0: agent exited, engine still working (verdict and gates)` |
| 05:22:23 | engine hard-killed mid-battery (`Stop-Process -Force`, by exact pid) |
| 05:22:26 | stale lock still on disk naming 37524; `status`: `interrupted mid-session — #1 in T0 never finished; resume with conductor run` |

Both halves, live: the healthy window reads running, and a genuinely dead engine still reads
interrupted even though its lock file is still lying on disk.

## Negative control — `SC2.1-negative-control.txt`

Two of them.

1. **Before any production edit**, the new regression test was run against the unfixed engine:
   `StatusReport_VerdictWindow_IsRunning_NotInterrupted_WhenEngineAlive` failed with
   `Expected: "active" / Actual: "interrupted"` — 1 failed, 21 passed of 22.
2. **Reproducible, on the fixed tree**: blinding `StatusReportBuilder` to the lock file (pointing it at
   a directory that cannot exist) and rebuilding fails the same test with the same
   `Expected: "active" / Actual: "interrupted"`, while the three honest-interrupted tests keep passing —
   the fix is load-bearing for exactly one behaviour and suppresses nothing else. Restored: 29/29 green.
   (The artifact records that the script's own restore-rebuild was a no-op — `Copy-Item` restores the
   backup's older timestamp, so MSBuild skipped the compile — and that the rebuild was then forced.)

## Tests

`tests/Conductor.Tests/StatusCommandTests.cs`

- `StatusReport_VerdictWindow_IsRunning_NotInterrupted_WhenEngineAlive` — agent pid tracked **and
  closed**, engine lock written by `EngineLock.Write` naming the real live test process: kind `active`,
  and the verdict contains neither "interrupted" nor "conductor run".
- `StatusReport_RecycledEngineLockPid_IsStillInterrupted` — a live pid stamped years before it really
  started: still `interrupted`.
- `StatusReport_DeadEngineLockPid_IsStillInterrupted` — a pid spawned, waited on and reaped here:
  still `interrupted`, and the resume advice is still printed.

`tests/Conductor.Tests/EngineLockTests.cs` — round-trip, legacy bare-pid parse (with and without a
trailing newline), recycled pid reads dead, garbage file reads unheld, delete releases.

Fast loop: `dotnet test --filter StatusCommandTests|EngineLockTests` → **29 passed, 0 failed**;
`W3ProcessRailsTests|DoctorCommandTests|SC1TelegramStatusTruthTests` → **38 passed, 0 failed**.
