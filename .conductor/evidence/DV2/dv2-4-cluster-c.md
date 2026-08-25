# DV2.4 — cluster C: state and verdict, and the close of the sweep

Session #5 of the Divan plan, stage DV2, 2026-08-25. Branch `feat/divan`.

Cluster C as the DV2.1 triage ledger drew it: `#67`, `#68`, `#71`, `#69`, `FU-F1-06`
(`.conductor/evidence/DV2/dv2-1-triage-ledger.md`, the cluster C FIX table).

**Every row is closed. Two of them turned out to be already fixed in `src/` and the triage's premise
for them was stale — both are recorded here with the measurement, and both now carry the proving
test they never had. One of the three real defects was deeper than the ledger said.**

---

## §1 — the five rows

| row | verdict | what landed | commit |
|---|---|---|---|
| `#67` stale-rebase squash guard | **FIXED** | refuse a stale rebase; assert ancestry after any abort | `8c23aaf` |
| `#68` budget restart-at-zero | **FIXED** | the budget reaches the store on every exit path | `4866902` |
| `#71` first-write FK (karvan `#27`) | **already fixed by KS0.3 — now PROVEN** | 3 tests: the hazard, the fix, the ordering | `4866902` |
| `#69` 429-as-backoff | **FIXED — two stacked defects, one undiscovered** | share-mode on the raw tail + the empty-envelope gate + reset-time backoff | `73f05d0` |
| `FU-F1-06` `UpdateRunStatus` | **already fixed by KS0.2 — now PROVEN end to end** | a real engine parks, the row reads `paused` | `6aa028b` |

All four bug rows closed with `conductor bug fix` alongside their regression tests.

---

## §2 — what was measured, per row

### `#67` — the squash could rewind the branch, silently

Seam `src/Conductor.Core/Git.cs:287-306` (was: an unconditional `git rebase --abort`).

The staleness test is **HEAD's attachment**, not `orig-head` parsing: for the whole of a rebase git
holds the rebased branch at its starting point and replays onto a DETACHED HEAD. So a repo with a
rebase state directory *and* a HEAD attached to a branch is not mid-rebase — the state is litter
from a process that died, the branch has moved on under it, and the abort resets that branch to a
sha from before the work. One `symbolic-ref`, no dependency on a `.git` layout git does not promise,
and no sync file IO (which in `Conductor.Core` is `MA0045 = error`, and the file-level pragma the
codebase uses for it would add analyzer-ratchet debt against an already-red `#60`).

Second guard, at the other end: after any abort, the stage's own start head must still be an
ancestor of HEAD. That covers the case the first guard cannot see — a dead rebase whose HEAD is
still detached — and it is what turns a silent rewind into a refusal.

`src/Conductor.Core/Git.Rebase.cs` — new, holds `StaleRebaseReason` and its reasoning.
Tests: `tests/Conductor.Tests/DV2_4StaleRebaseGuardTests.cs` (3).

**Inversion probe** — `.conductor/evidence/DV2/dv2-4-inversion-67.log`. With both guards disabled,
both new tests fail, and both fail as **`NothingToSquash`** — the exact silent-advance signature the
field measurement recorded ("nothing to squash — among 2 commit(s)", HEAD back 28 commits, stage
advanced). The third test — a genuine conflicted rebase — stays green under the inversion, which is
what proves the guard did not simply delete the recovery.

### `#68` — `maxRunCostUsd` was a per-PROCESS cap

`RunContext.PersistBudget()` (`RunContext.cs:285-291`) writes the four counters into `RunState` **in
memory only**. `RunContext.Save()` is what reaches the store. The `--once` return
(`RunLoop.cs:465-469`) — the exit the watch supervisor and every scripted rig take — had no `Save()`.

Measured by the new harness test *before* the fix, which is the reproduction:

```
Error Message:
   a --once exit persisted PerRunCostUsd=0 — a restart would resume from zero
```

with the live counters holding the session's cost at the same moment. `RestoreBudget()` reads
exactly those persisted fields, so zero there IS the per-process cap.

Fixed in the run loop's `finally`, not at the `return`s that happened to be missing it: a new exit
path arrives every era. Same argument as `EnsureRunRow` for `#27`, and for the same reason — the
ordering is what was fragile. A store that cannot be written logs a warning rather than throwing out
of teardown.

*Note for a later era:* `RunLoop` sits ON the CA1506 coupling ratchet at 183/183. `BestEffort.Run`
could not be used here because introducing that one type failed the build at 184.

**Inversion probe** — `.conductor/evidence/DV2/dv2-4-inversion-68-71.log`.

### `#71` (karvan `#27`) — already fixed, never proven

KS0.3 landed `RunContext.EnsureRunRow()` (`RunContext.cs:348-355`) and `Save()` calls it *before*
`SaveRunState`. The triage carried the row as open because the row was recovered from a pre-split
store, not because the code was unfixed.

What it had no test for is that the hazard is real. Three now, and none of them vacuous
(`tests/Conductor.Tests/DV2_4FreshStoreFirstWriteTests.cs`):

1. with **no** `runs` row the first `run_state` write is genuinely rejected and LOST — the FK is
   enforced (`SQLitePCLRaw.bundle_e_sqlite3` defaults `foreign_keys` on) and `SaveRunState` swallows
   the rejection, so nothing throws and nothing is stored;
2. with the row ensured first the same write survives on the same brand-new database;
3. the **ordering** inside `Save()` is pinned by source rule — because an ordering is what rotted
   the first time.

The end-to-end half rides in the `#68` harness test: that database is created by the run itself, and
its first state write is read back.

### `#69` — the 429 storm, two stacked defects

The triage named one seam. There were two, and the undiscovered one is the load-bearing half.

**(1) The raw tail was always empty.** `SessionRunner.LastRawTail` used `File.ReadAllText`, which
opens with `FileShare.Read`. Every caller runs while `AgentSession` still holds the *same file* open
for WRITING (`AgentSession.cs:195`, disposed only at teardown), so Windows refused the read, the
`IOException` was swallowed by the existing `catch`, and the tail came back empty — always. The
classifier has been matching a blank string for as long as this code has existed.

Measured with a probe log line placed immediately before the classifier:

```
[22:27:22] DIAG exit=1 said=True err=False rt=[] tail=[] evid=[ ]
```

on a session whose raw log's last line was `Claude AI usage limit reached - try again in 45m` (the
same test asserted the phrase WAS in the file). Now a `FileStream` with `FileShare.ReadWrite`:
another handle is writing this file and that is fine — a tail of a live log is allowed to be a
snapshot.

**(2) The tail was gated on `ResultText == null`.** A refused CLI does not answer that way. It
returns a result envelope whose text is EMPTY and whose cost is zero — which is exactly why the
field log line could print `$0.00` at all. Now `IsNullOrWhiteSpace`, and it is still the only case
that reaches the tail, so a session that produced real text is judged on its text alone.

**(3) And the wait is the backend's, when the backend gave one.** `ProviderText.ResetWait` reads the
Claude CLI's `limit reached|<unix second>`, an HTTP `Retry-After`, and English `try again in 4h 32m`
/ `resets in 45m`. Clamped to 12 hours; a reset already in the past answers null rather than "wait
zero", which would be the same storm one level down. No reset named keeps the plan's flat
`backoffMinutes`. Extracted into `BackoffWindow` because `RunAsync` sits ON the CA1502/CA1505
ratchet and two inline ternaries failed the build.

Tests: `DV2_4RateLimitBackoffTests.cs` (14, the parser and the clamps) and
`HarnessTests.RateLimit.cs` — a fake agent reproducing the field shape exactly (a parsed line so
`ResultText` is the empty buffer rather than null, zero cost, the refusal on the raw stream, exit 1).
It asserts what the run actually lost: `LimitBackoff`, status `Backoff`, `ConsecutiveBackoffs` 1,
the backoff line naming 45m from the backend — and **`AttemptsThisStage` still 0**.

The field record this reproduces, from this repo's own log
(`.conductor/logs/conductor-20260815.log:2154-2280`, run `9647f1b8`): `session #N exited (code 1,
0m, $0.00)` every ~19 seconds, no "usage limit detected" line anywhere, attempts 2→8 gone in three
minutes, the breaker firing on "identical failure pattern (AgentError ×2)", the advisor 429ing too,
and the stage ending NEEDS HUMAN over an account limit that would have cleared itself.

**Inversion probe** — `.conductor/evidence/DV2/dv2-4-inversion-69.log`: with both halves reverted the
harness test fails `Expected: LimitBackoff / Actual: AgentError`. Each half was also measured alone
during development: with (2) fixed and (1) still broken the test was red; with (1) fixed and (2)
reverted it is red again. Neither half is decoration.

### `FU-F1-06` — already closed, and the ledger said so

The triage row reads "`UpdateRunStatus` exists nowhere in `src/`". Measured: it does.
`IRunStore.UpdateRunStatus` (`Store/IRunStore.cs:38`), `SqliteRunStore.Sessions.cs:74`, called from
`RunContext.SyncRunStatus` (`RunContext.cs:384-392`) on every `Save()`. `.conductor/followups.md:197`
already records the row **CLOSED (`15627b9`, KS0.2)** — the "11 OPEN" count DV2.1 flagged as a
`grep -c` artefact is what carried it into the ledger.

What nothing pinned was the **route**: `KS0_2RunRecordTests` covers the writer and the vocabulary at
the store seam, but no test drove a real engine into a park and read the row back. Now one does
(`HarnessTests.Budget.cs`): the harness starts a run paused, waits for the park, and queries
`runs.status` — `paused`, with no `ended_utc`, because a resumable run has not ended.

**Inversion probe** — `.conductor/evidence/DV2/dv2-4-inversion-fu-f1-06.log`: with the
`SyncRunStatus` call disabled the test fails `Expected: paused / Actual: running` — the
immortal-running record the row was filed for.

---

## §3 — the ledger, closed

Cluster C is the last FIX cluster of the sweep. Across DV2.2–DV2.4 every row the DV2.1 triage
dispositioned **fix-this-stage** is now closed:

| cluster | rows | state |
|---|---|---|
| A, prompt composition (DV2.2) | `#15`, `#21`, `#55`, battery truncation | closed |
| B, channels (DV2.3) | `#38`, `#64`, `#65`, `#66` | closed |
| C, state and verdict (DV2.4) | `#67`, `#68`, `#71`, `#69`, `FU-F1-06` | closed |

Every remaining row is **DEFER with a named owner**, recorded in
`.conductor/evidence/DV2/dv2-1-triage-ledger.md`'s DEFER table — each with an owner and a reason.
Nothing in this checkpoint moved a row between clusters and nothing dropped one.

Three dispositions this checkpoint touches, recorded so a later reader is not misled:

- `#71` and `FU-F1-06` were dispositioned FIX on a premise that turned out to be stale. They are
  closed as **already-fixed-now-proven**, not as new fixes. The distinction is in §2 and in the
  commit messages; the tests are real either way, and both were inversion-probed.
- `#39` (the *session*-row twin of `FU-F1-06`) and `#37` (`history --json` misses non-terminal baton
  rows) stay DEFER with their named owner. The ledger routed both to "next era, store lane" as
  natural follow-ons *once the run-status writer exists*. It does exist — it existed before this
  checkpoint — but neither is in cluster C's named acceptance, and inventing scope at the close of a
  sweep is how a sweep stops being one.
- `FU-B11-3` is owner-gated by its own row and was not touched.

---

## §4 — how to re-run any of this

```
dotnet test Conductor.slnx --filter "FullyQualifiedName~DV2_4"
dotnet test Conductor.slnx --filter "FullyQualifiedName~HarnessTests"
```

Full suite for this checkpoint: `.conductor/evidence/DV2/dv2-4-full-suite.log`.

The inversion-probe discipline (invert the fix in source, re-run the scoped filter, keep the log,
`git checkout --` to restore) came from DV2.3's handoff and earned its keep twice here: it is what
proved `#67`'s two guards fail *as `NothingToSquash`*, and it is the reason `#71` and `FU-F1-06`
have tests that have been red rather than tests that have only ever been green.
