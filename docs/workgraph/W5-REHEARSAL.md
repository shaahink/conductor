# W5.1 — the credential-free dress rehearsal

**Status: PASS** (27/27 checks). One `conductor.exe` process, started once, took a markdown document
describing three stages of work to a finished run — 10 sessions, 6/6 checkpoints confirmed, exit 0,
and a `RunFinished` event in `run.db`. That event had never existed before this checkpoint.

Reproduce it (no credentials, no model, ~90 seconds):

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/w5/rehearsal.ps1 -Keep
```

`tools/w5/rehearsal.ps1` is the driver, `tools/w5/agent.ps1` the token-free agent, and
`tools/w5/advisor.ps1` the token-free advisor. The permanent regression gates for what it found are
`tests/Conductor.Tests/W5RehearsalTests.cs`.

## Why an out-of-process driver

W1–W4 each proved a mechanism with an in-process test. Those tests build a `ConductorHost` and call
`Orchestrator.RunAsync` directly, which is exactly how W2.1 got burned: every MCP wire test was green
while a live `claude` session could not reach a single conductor tool, because the test client we
wrote was more forgiving than the real one. So this rehearsal touches no engine class. It drives the
shipped binary, moves every lever over the real HTTP control plane with the run's own write token,
claims through `conductor task --done` from inside the worker, and reads its verdict back out of
`run.db` with `conductor report --query`.

That decision paid for itself immediately: both defects below are invisible to a single-session test,
and one of them is invisible to *any* in-process test that does not go through HTTP.

## What it drives

```
scratch git repo
  -> conductor init --from-idea TOY-PLAN.md        (W4.2 + W4.1: idea -> drivable plan)
  -> stand in a fake agent + fake advisor           (the only hand edit, and it is the harness's)
  -> conductor doctor                               (work coverage ok before anything runs)
  -> conductor run --headless --paused              (ONE process; never restarted)
       levers while paused : card context . per-card QA dials . plan edit
       resume
       levers in flight    : stage-level card add . AI split . confirm children
                             . QA dial on a card two stages ahead . card context
  -> the run finishes by itself                     (RunFinished, exit 0)
```

No tracker table is authored anywhere by hand. `TOY-PLAN.md` is a plain design document with
`## T1 — …` headings and `- **T1.1** …` bullets; everything on the board comes from parsing it.

## The evidence

Ten sessions, and the shape of them is the point:

| # | kind | stage | outcome | claimed |
|---|---|---|---|---|
| 1 | Deliver | T1 | Advanced | T1.1 |
| 2 | Verify | T1 | Progress | — |
| 3 | Deliver | T1 | Advanced | T1.2 (`qa: off`) |
| 4 | Deliver | T2 | Advanced | T2.1 (`qa: verify`) |
| 5 | Verify | T2 | Progress | — |
| 6 | Deliver | T2 | Advanced | **T2.2** (added mid-run) |
| 7 | Verify | T2 | Progress | — |
| 8 | Deliver | T3 | Advanced | T3.1 (dial flipped `off` mid-run) |
| 9 | Deliver | T3 | Advanced | T3.2 |
| 10 | Verify | T3 | Progress | — |

```json
{ "type": "runFinished", "status": "Completed", "sessions": 10,
  "checkpointsDone": 6, "checkpointsTotal": 6, "seq": 80 }
```

`RunFinished` is the last non-`TokenDelta` event in the log, so the run ended deliberately rather
than being read as idle or interrupted afterwards. `CheckpointConfirmed` events show the verdict
engine confirming claims on its own evidence, not merely recording them.

Read the session table against the QA dials and criterion 5 is legible in one glance. T1.1 carried no
dial and was verified (session 2). T1.2 said `off`, and session 3 is followed straight by session 4's
delivery — no verify. T2.1 said `verify` and got session 5. T3.1's dial was flipped to `off` *while
the run was executing*, two stages before the engine reached it, and session 8 has no verify after
it. Same stage, same plan, different pipeline per card. And session 10 exists at all only because of
defect 3 below.

## The five criteria

| # | Criterion | How the rehearsal shows it |
|---|---|---|
| 1 | Plan in → Kanban out | `TOY-PLAN.md` → `init --from-idea` → five cards on `GET /tasks`, `doctor` work-coverage ok, zero hand-authored rows |
| 2 | Everything stays in sync | `POST /plan/edit` renames stage T3 mid-run; the new title reaches `GET /state` with no restart **and** the regenerated tracker; no `TODO` row survives the run |
| 3 | Prompt blocks visible and real | context attached to T1.1 comes back in `GET /prompt/blocks`'s `promptSection`, and a context attached to T3.2 *mid-run* is found verbatim in the `prompt.md` the delivering session received |
| 4 | Add work in flight | stage-level card added at session 1's boundary → split by the advisor → both children confirmed → the plan's declared work carries it → session 6 delivers it. No restart |
| 5 | Pipeline control per task | the table above: `off`, `verify`, and a dial flipped mid-run, all honoured within one stage and one plan |

## What it found

Three engine defects, all fixed here. The first two are fatal to an unattended run; the third is a
hole in the anti-cheat story that the first fix uncovered.

### 1. The engine scheduled on the declaration, not the graph

W1 settled that the graph is the runtime truth and declared work is only a declaration. Every reader
moved except the run loop, which kept taking checkpoint **status** from the progress provider.

On the markdown-table path that is invisible, because the tracker is regenerated from the graph after
every session and so agrees with it a moment later. An inline (`plan-checkpoints`) plan — which is
what *every* W4.1 import produces — has no write-back at all: its declared statuses read `TODO` for
the life of the run. The consequences compounded:

- the assignment policy re-picked a card the graph had already recorded as delivered;
- the prompt's card section then rendered **empty**, because that section reads the graph, where a
  done card is history rather than an instruction;
- the agent had nothing to deliver, twice, which the circuit breaker correctly called no progress;
- the run parked `NEEDS HUMAN` at `0/5 done` with one checkpoint actually delivered;
- `AllEffectivelyDone` could never become true, so `RunFinished` was unreachable.

Fix: `Core/Planning/WorkSnapshot.cs` — declared rows carrying the graph's status — and the engine
reads work state through it (`RunContext.ReadWork`). This is the same projection `GET /state` and
`GET /tasks` already served since W1.4; the fold moved into one place so the engine and the views
cannot drift. The declared read still supplies the row set before anything is seeded, still supplies
the handoff block, and is still what the verdict diffs a hand-edited tracker against — the W1.3
legacy-claim fallback is untouched.

Why W4.1's own live test missed it: it ran with `Once: true`. One session is exactly the horizon at
which the two sources still agree.

### 2. The plan reload skipped the control plane

`ApplyPlanReload` swaps the fresh plan into the context, the gates, the lanes and the dispatcher. The
HTTP server — which every Face surface reads — cached its own reference and was not on that list. A
plan edit reached the engine and the generated tracker while the TUI served the pre-edit plan for the
rest of the run. That is criterion 2 failing on the read side, and no in-process test could see it
because they all read `_ctx.Plan` directly.

Fix: `ControlPlaneServer.SwapPlan`, invoked from the reload boundary through an `onPlanSwapped`
callback wired in `ConductorHost`. Same shape as the four swaps already there.

Found alongside it: `RunContext.SwapPlan` rebuilt the prompt builder but not the progress provider,
and the inline provider captures its checkpoint list by value — so a card declared mid-run was
invisible to every declared read until the process restarted. Now rebuilt with the plan.

### 3. The last checkpoint's verification was skipped by completion

Fixing defect 1 broke `W1OneProjectionTests.VerdictFlip_…`, and the reason it broke was the third
defect rather than the fix. The completion branch's guard named `PendingFix` and `PendingResume` but
not `PendingVerify` or `PendingAudit`. That was harmless only because done-ness used to lag a tracker
regeneration behind the claim, so a queued verify always got its turn before the loop noticed the plan
was finished. Reading the graph directly removes the lag — and the run then closed over the top of the
verification it had just queued.

The blast radius is precisely the plan's **last** checkpoint, in every run: claimed, gate-green,
confirmed, and the only card in the plan that nobody independently checked. The rehearsal showed it
plainly — before the fix, session 9 delivered T3.2 and the run finished; after it, session 10 is
T3.2's verify and the run finishes after that.

Fix: the guard consumes what the run still owes before it closes. `tests/…/W5RehearsalTests.cs`
gates it directly, and the W1.4 test passes unchanged — its intent (observe the board while parked at
a session cap, before the next session) is restored rather than adjusted.

## Observations, not defects

Worth carrying into W5.2 rather than fixing blind.

- **The prompt does not name the checkpoint the engine will judge the session against.** It names the
  stage ("DELIVER the next incomplete checkpoint(s) of stage T3 only") and instructs the agent to read
  the tracker. The engine, meanwhile, has a definite answer: `assignment.Items`, which also decides
  the per-item QA dial. The agent re-derives that answer by parsing a markdown table, and if it
  derives a different one the two disagree silently. It cost this rehearsal one debugging cycle —
  `tools/w5/agent.ps1` now reads the tracker exactly as instructed. A real model would too, so this
  is not a live bug; but naming the claimed items in the prompt would make the contract explicit
  instead of conventional. Candidate follow-up, deliberately not taken inside W5.1.
- **The generated tracker is a tracked file the engine rewrites after the verdict**, so a run with
  `report.commit: false` reports `dirty` after otherwise clean sessions. Cosmetic here (the note is
  informational), but it is noise a real run should not have to read past.
- **`report --query` is the honest way to assert on a finished run**, with one trap: the `events`
  table's `type` column holds the CLR event name (`RunFinished`), not the JSON discriminator
  (`runFinished`). Querying the wire name returns "no rows", which reads exactly like a defect.

## Harness notes

Kept here because each one cost a cycle and will cost the next person the same.

- `ProcessStartInfo.ArgumentList` does not exist under Windows PowerShell 5.1 (.NET Framework) — it
  is null, and adding to it throws. Use `.Arguments` with quoted paths.
- A `Process` from `Start-Process -PassThru` does not reliably surface `ExitCode`; it reads as empty,
  which is indistinguishable from a crash. The rehearsal starts the run through the .NET API and
  drains both pipes on background threads (a full pipe buffer would deadlock a run that logs for
  minutes).
- A driver whose assertions all live inside a `try` reports PASS when it throws before the first
  check — nothing had failed, because nothing had run. There is now a `catch` that fails explicitly.
- Engine artifacts (`.conductor/`, logs) must be git-ignored in the scratch repo, or the untracked
  files read to the verdict engine as "dirty after a green session": a true observation about a
  problem the harness invented.
- `tools/w5/agent.ps1` and `tools/w5/advisor.ps1` are ASCII-only, like `tools/fake-agent.ps1`: PS 5.1
  reads a BOM-less UTF-8 script as ANSI and one non-ASCII byte tears the next string literal.

## What this does not prove

W5.1 is the dress rehearsal; W5.2 is the performance. Deliberately still open:

- A **real model** doing real work, where prompts are interpreted rather than pattern-matched, gates
  genuinely fail, and the verifier disagrees. `HUMAN:` — the owner starts and pays for that run.
- The W3.3 window-close rail (`GenerateConsoleCtrlEvent` cannot synthesise `CTRL_CLOSE`, so the
  OS-delivery half is unautomatable) still wants one manual ✕ on a live run.
- The W3.2 auth smoke test's paid one-token ping against a real CLI.
