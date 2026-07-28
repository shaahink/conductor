# W3.3 — the window close, proven by closing a window

**Driver:** `powershell -File tools/w3/window-close.ps1 -Keep` (~90s, no credentials, Windows only)
**Result:** 18/18 checks PASS, 2026-07-28.

W3.3 shipped the CTRL_CLOSE/LOGOFF/SHUTDOWN rail and then said so honestly in the tracker: the
OS-delivery half could not be automated, and the checkpoint carried a `HUMAN:` note asking for *one
manual ✕ on a live run before W5.2*. This closes that note without a human clicking anything.

## Why it looked unautomatable

`GenerateConsoleCtrlEvent` — the only API a process can use to synthesise a console control event —
emits `CTRL_C_EVENT` and `CTRL_BREAK_EVENT` and nothing else. There is no call that makes Windows
deliver `CTRL_CLOSE_EVENT` to a process. That is not an oversight in the API; a close event means
"your console is going away", and the console host is the only party entitled to say it.

So W3.3 gated the two halves separately and said the join was unproven:

- the handler body, driven directly (close/logoff/shutdown all stop the run; the handler provably
  does not return before the save signals; Ctrl+C is left to `CancelKeyPress`), and
- the clean park + resumable `run.db`, gated live on the same cancellation path.

Both were green. What no test could show was that a real close reaches the first half at all.

## What actually delivers the ✕

The way in is not to synthesise the event but to cause it: post `WM_CLOSE` to the run's console
window, which is byte for byte the message the window manager sends when the ✕ is clicked, and let
the console host decide what to do with it. It turns it into `CTRL_CLOSE_EVENT` for every attached
process — the real path, with the real 5-second kill deadline.

One wrinkle nearly hides this. On Windows 11 the default terminal is ConPTY-hosted, and
`GetConsoleWindow()` there hands back a hidden `PseudoConsoleWindow` stub that no message can
close — post to it and precisely nothing happens, which reads exactly like "the rail does not
work". Launched through `conhost.exe`, the same run gets a genuine `ConsoleWindowClass` window and
`WM_CLOSE` follows the classic path. The driver asserts on the window class before it posts, so a
future run on a machine where this changes fails loudly instead of silently proving nothing.

Both hosts end at the same handler. What differs is only whether a test can reach the doorbell.

## What the driver does

1. Scaffolds a hermetic scratch repo and takes a two-stage markdown document to a drivable plan
   through `conductor init --from-idea` — the ordinary W4.1/W4.2 path, no hand-written plan JSON.
2. Stands in a deliberately **slow** agent (`tools/w3/slow-agent.ps1`) that announces itself on
   disk and then sits in the middle of its work until a release file appears. The marker is the
   driver's proof that a session is genuinely in flight: closing a window with no live session
   proves nothing, because there is nothing to lose.
3. Starts `conductor run --headless --no-face` under `conhost.exe`, so it owns a real console
   window, and finds the engine pid beneath it.
4. Waits for the marker, then posts `WM_CLOSE` and times how long the process takes to die.
5. Reads the evidence back off disk.
6. Releases the agent and **resumes the run for real**, letting it finish the work the ✕ interrupted.
7. Runs the whole thing again with a hard kill instead of a close, as a negative control.

## The evidence

| What | Observed |
|---|---|
| The window is a real one | class `ConsoleWindowClass` (not the ConPTY stub) |
| `WM_CLOSE` posted | true |
| Process ended | 0.42 s after the post |
| Graceful, not instant | > 0.2 s — something ran before the process died, and well inside the 4 s grace |
| No orphaned agent | the agent child is gone with its parent |
| Lock released | `.conductor/conductor.lock` absent afterwards |
| Session recorded | `sessions` row #1 `Deliver` / **`Interrupted`** / `ended_utc 2026-07-28T12:42:03Z` |
| Event log closed off | last events `SessionFinished`, `TokenDelta`, `SessionStarted`, `StageEntered`, `TaskAdded`×2 |
| run.db intact | reopens and queries clean |
| Genuinely resumable | the next `conductor run` prints `resuming run`, exits 0, and finishes `3/3 checkpoints` |

The timing is worth dwelling on. Windows kills the process the moment the handler returns, so the
rail's entire job is to finish saving *inside* that window — a return that is too fast is the
failure mode, not a success. 0.42 s is the shape you want: long enough to have done the save,
nowhere near the 4 s grace.

## The negative control

Every check above is a claim that the graceful path left something behind. On its own that is
unfalsifiable — perhaps any dying run leaves that trail. So the driver runs the identical scaffold
a second time and hard-kills the engine at the same moment:

- the lock is **still there** (releasing it is the rail's work), and
- `SELECT … FROM sessions` returns **no rows** — no `Interrupted` record, because nothing ran to
  write one.

That is §7.5's accidental-✕ data loss, reproduced on demand. The difference between the two columns
is the rail.

## Observations, not defects

- **The rail's own messages die with the window.** `console window closed — stopping the run and
  saving state` and `state saved — run conductor run again to resume` go to `AnsiConsole`, i.e. to
  a console that is in the act of closing. They are unreadable by construction, so the operator
  never sees the reassurance the rail was written to give. Mirroring them into
  `.conductor/conductor.log` is a four-line change and was written and then reverted here: the
  sync `File.AppendAllText` trips `MA0045` (severity `error`), and the only clean suppression would
  take the pragma count from 37 to the ratchet's ceiling of 38 — the last slot, spent on a log line.
  Left for whoever next has a reason to touch that ceiling. The driver asserts on the disk state
  instead, which is stronger evidence anyway.
- **`MarkdownPlanParser.LooksStructured` requires ≥ 2 stage headers.** A one-stage plan document is
  reported as "This text isn't a structured plan" and needs an advisor to import. That is the
  documented heuristic (`MarkdownPlanParser.cs:32-40`), not a bug, but it is surprising from the
  outside — the message names prose, and the document was not prose. Cost twenty minutes here.

## What this does not prove

- **Logoff and shutdown.** The handler treats `CTRL_LOGOFF_EVENT` and `CTRL_SHUTDOWN_EVENT`
  identically to close and is gated on all three directly, but only close is driven end to end.
  Proving the other two means ending a Windows session, which no test should do to its host.
- **The ConPTY path.** Windows Terminal's ✕ reaches the same handler by a different route
  (`ClosePseudoConsole`), and this driver deliberately takes the classic route because it is the
  one a test can post a message to. The handler is the same code either way.
- **Anything about a real model.** Like every W1–W5.1 gate, the agent here is a token-free script.

## Running it

```powershell
powershell -ExecutionPolicy Bypass -File tools/w3/window-close.ps1 -Keep
```

A console window appears on the desktop for a few seconds and is closed programmatically — that is
the test, not a side effect. Windows only, and not a CI test: a runner has no window station to
close. `-EvidenceOut <path>` writes a machine-readable summary; `-SkipControl` drops the negative
control; `-Keep` keeps both scratch repos.
