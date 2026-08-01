# Field notes — driving `sk-fleet round four` with conductor (2026-07-30, live)

**This file is being written while the run is going**, unlike
[FIELD-NOTES-2026-07-29-sk-platform.md](FIELD-NOTES-2026-07-29-sk-platform.md), which was
reconstructed after the fact. Same project (`C:/code/sk-platform`), a different plan: seven stages,
25 checkpoints, Deliver→Fix-only workflow with no QA sessions, every stage on `claude-opus-5`.

Run `3dd4564a`, engine started 23:24 on 2026-07-29. Written by the supervising agent, watching from
outside the engine. Each finding says how it was observed. Ordered by when it was found, not by
severity — this is a live log, and I would rather append than re-rank.

---

## 1. `conductor task --in-progress` reports a transition it silently refused

**Severity:** medium — the CLI says the board moved when it did not.

**How it was found.** Investigating why `K1.4` read IN PROGRESS with no session behind it (see #2), I
read the write path. `SqliteRunStore.MarkCheckpointInProgress` is deliberately strict — *"Parity with
the SQL it replaces: TODO → IN PROGRESS only; never reopens a DONE row"* — and returns without
emitting when the row is anything else. `TaskCommand.cs:82-83` calls it and then prints
`checkpoint <id> → IN PROGRESS` unconditionally, with no read-back:

```csharp
store.MarkCheckpointInProgress(runId, settings.InProgress);
AnsiConsole.MarkupLine($"[yellow]checkpoint {settings.InProgress} → IN PROGRESS[/]");
```

**Why it matters.** Sessions run with the context reset between them and are instructed to mark their
own start before editing. On a row a previous session (or a human on the board) already moved, the
agent gets a success line, believes the board shows work in flight, and no event exists. On a **DONE**
row it is worse: the CLI prints `→ IN PROGRESS` while the row stays DONE, which is the one direction
where an agent might reasonably conclude the checkpoint had been reopened for it.

**Suggested fix.** Report the post-fold status, exactly as `POST /tasks/update` already does
(`ControlPlaneServer.Tasks.cs:29-35` folds the event and answers with `actual`). Two ingresses, one
of which tells the truth — the honest one is already written.

---

## 2. A human kanban move is one-way from the CLI: there is no `--todo`

**Severity:** medium — a mis-drag on the board is not correctable with the documented verbs.

**What happened.** At 00:00:42 local the owner moved `K1.4` (a site-template docs checkpoint nobody
had started) from TODO to IN PROGRESS on the Face kanban. That is a real, correctly-attributed write
— event `seq 48`, `TaskStatusChanged`, `"source": "human"` — and it is exactly what the board is for.
The problem was undoing it:

- `conductor task` offers `--done` and `--in-progress` only; `--in-progress` accepts `todo` rows only.
- `TaskGraph.IsValidTransition` *permits* `("in_progress", "todo")`, and `TaskWrites.ValidStatuses`
  includes `todo`, so the model has no objection — only the CLI has no verb.

Recovery was a hand-rolled POST against the control plane, reading port and token out of
`.conductor/control-plane.json`:

```powershell
$c = Get-Content .conductor\control-plane.json | ConvertFrom-Json
Invoke-RestMethod -Method Post -Uri "$($c.baseUrl)/tasks/update" `
  -Headers @{ 'X-Conductor-Token' = $c.token } -ContentType 'application/json' `
  -Body '{"taskId":"K1.4","status":"todo"}'
# → {"ok":true,"taskId":"K1.4","status":"todo","order":0}
```

**What the stray status does and does not do.** Worth stating precisely, because the answer was not
obvious from the outside and it decided whether to intervene at all:

- **No control-flow effect.** The active checkpoint is `preTrack.ForStage(stage).FirstOrDefault(c =>
  !c.IsDone)` (`SessionRunner.Mcp.cs:179-182`) — the first non-DONE row, so `K1.3`, regardless of what
  `K1.4` says. Stage advance gates on all rows DONE plus the engine's own battery. `CurrentTask` is
  per-checkpoint and picks sub-tasks, not checkpoints.
- **Every view changes.** `task --list`, `tasks`, `REPORT.md` (`🔄 IN PROGRESS`) and the regenerated
  `TRACKER-R4.md` row all show it, and the stage label can read `partial`.

So the cost is not to the engine, it is to the next session — which reads the board cold, with no
memory of the run, and would have found a checkpoint that looked half-done and had no work behind it.
This project has already been bitten by that once: the tracker handoff for this very run warns *"run.db
carries the killed session, so K1.1 may read IN PROGRESS on the board — trust `conductor task
--list`, mark your own start."* A one-way board makes that warning permanent furniture.

**Suggested fix.** `conductor task --todo <id>` (and, while there, `--blocked`/`--skipped`), sharing
`TaskWrites.BuildStatusChange` with the other two ingresses so the vocabulary cannot drift. It is the
same event the board already emits; only the operator's half is missing.

---

## 3. `conductor log` cannot read its own log while the engine is running

**Severity:** medium — the structured-log query tool is unavailable precisely while a run is live.

**Evidence.** At 23:59, mid-session:

```
> conductor log --limit 15
error: System.IO.IOException: The process cannot access the file
'…\.conductor\logs\conductor-20260729.json' because it is being used by another process.
   at System.IO.File.ReadLines(String path)
   at Conductor.Commands.LogCommand.Execute(…) in …\Commands\LogCommand.cs:line 69
```

The engine holds the file open for append; `File.ReadLines` asks for a share mode the writer does not
allow, so the read fails on Windows. Post-run it works, which is the one time the log matters least.

**Suggested fix.** Open explicitly with `FileShare.ReadWrite | FileShare.Delete` (a `FileStream` +
`StreamReader` instead of `File.ReadLines`), and treat a torn last line as skippable — a JSONL tail
read during an append can land mid-write. `run.db` reads already work fine concurrently (`sqlite3
"file:…run.db?mode=ro"` and every `conductor status` call prove it), so the log is the outlier.

---

## 4. `bg status` lists the agent session's pid; `bg logs` on it says no such log

**Severity:** low — a wrong turn, not a fault.

**Evidence.** `conductor bg status` includes the live agent as `agent:stage:2:session#2 … running`,
so the obvious way to watch a session is `conductor bg logs 18220` — which answers *"No log file
found for '18220'"* and then prints 67 unrelated log names. The session's actual stream is
`.conductor/logs/session-002.jsonl` (Claude Code stream JSON, live and current), and its prompt is
`session-002.prompt.md` beside it.

Also worth recording: that row's `Runtime` column read `-1694s` — a negative runtime for a live
process, presumably a UTC-vs-local subtraction. The session jsonl timestamps are UTC while
`bg status`, `status` and the log lines are local, which is a one-hour offset in this timezone and
cost me a minute of thinking a session had been idle since 23:42.

**Suggested fix.** Either point `bg logs <pid>` at the session stream for agent rows, or have the
`bg status` table name the file for each row. And compute that runtime in one timezone.

---

## 5. What worked

- **The board move was attributable in one query.** `source: "human"` on the event, next to the
  agent's own `K1.3` claim, is what made a 30-second diagnosis possible: two writes, two different
  provenances, no ambiguity about who moved what. Without the `source` field this finding would have
  started with "did the engine do this?".
- **`POST /tasks/update` answers with the post-fold status**, so an illegal move is visibly a no-op
  rather than a silent one. It is the contract #1 should copy.
- **`run.db` is readable concurrently and the event log is complete.** Everything in #2 was
  established from outside the engine, mid-session, with a read-only SQLite connection and no risk to
  the run.

---

## Closure ledger (SF7.1, 2026-08-01)

All four findings closed. `9024e57`'s own body names this log: "both of round-four's complaints fall
out of that."

| # | Finding | Stage | Commit | What closed it |
|---|---|---|---|---|
| 1 | `--in-progress` reports a transition it silently refused | SC5.3 | `9024e57` | `conductor task` owned a private write path; every move now goes through `IRunStore.ApplyTaskStatus` and reports the post-fold status, so a refused move is visibly a no-op. `--done` had the same hole and is fixed with it |
| 2 | A human kanban move is one-way from the CLI | SC5.3 | `9024e57` | `--todo`, `--blocked` and `--skipped` exist, so undoing a mis-drag no longer needs a hand-rolled HTTP POST against the control plane |
| 3 | `conductor log` cannot read its own log while the engine runs | SC2.4 | `87d7fcd` | Both `log` and `bg logs` asked for `FileShare.Read`, which on Windows does not permit the writer's Write handle. The share mode now admits the live writer |
| 4 | `bg status` lists the session pid; `bg logs` says no such log | SC5.4 | `58bf293` | `bg logs` points at the session stream for agent rows, and the runtime column is computed in one timezone — the UTC-vs-local subtraction that made a live session look idle since 23:42 |
