# SF5.2 — the supervisor plan block

**Checkpoint.** A supervisor plan block runs a configured command on wake with the brief on stdin;
`operating.md` carries the wake / don't-wake table and the standing-order pattern.

**Commit.** `4efedac` (code, tests, docs) · this file and the drive script.

Acceptance was declared in the ledger before any edit. Each line below is what that acceptance said
must be true, and the artifact that shows it.

---

## 1. The block runs on wake, with the brief on stdin — no `--hook` on the command line

Driven live: `.conductor/evidence/SF5/SF5.2-supervisor-drive.ps1`, transcript
`.conductor/evidence/SF5/SF5.2-live-drive.log`, raw captures under `live52/`. Two scratch rigs under
`%TEMP%\sarban-proofs`, each with its own plan, `.conductor` and `--no-control-plane`; the binary
under test is this working tree's build (stamped in the transcript), never the `conductor` on PATH.

```
watch P pid 24616: no --hook, plan supervisor must run
watch P exit 0   engine exit 0
plan supervisor received stdin : True
  supervisor (plan.supervisor) - running, brief on stdin, up to 2m
  supervisor exit 0 in 1.1s
```

What the plan's supervisor read on its stdin (`live52/supervisor-stdin.json`, 34 lines, parses):

```json
{ "reason": "run-ended", "firedFrom": "event", "plan": "SF51WatchRig_sf52a",
  "runId": "b50d46df…", "status": "Completed", "checkpoints": "2/2", "spendUsd": 0.01,
  "standingOrders": "You MAY approve an owner gate whose checkpoint has an evidence path. You MUST escalate anything that spends money or merges to master.",
  "stages": ["T0 1/1 done", "T1 1/1 done"], "suggest": ["conductor status", "conductor report"] }
```

The timeout on the stderr line is **2m** — `supervisor.timeoutMinutes` from the block, not the
`--hook-timeout` default of 10. The block is self-contained or it is not a place you can keep a
supervisor.

## 2. Precedence is measured

| Case | Expected | Measured |
|---|---|---|
| No block, no `--hook` | nothing runs, nothing said (SF5.1 unchanged) | unit: `No_supervisor_block_and_no_hook_runs_nothing_and_says_nothing` |
| Block only | block runs, its own timeout | live watch P above |
| Block + `--hook` | `--hook` wins | live watch H: `supervisor (--hook) - running … up to 10m`, `hook-stdin.json` written |
| `enabled: false` / blank command | declines, and says WHICH way | unit theory `A_block_that_cannot_supervise_says_which_way_it_failed` |

Both watches attached to the same run and both woke; the fires ledger recorded **1** fire, not 2 —
`--hook` is a deliberate one-off by an operator at the keyboard and does not spend the plan's fuse.

## 3. The standing orders travel with the brief

`standingOrders` above came out of the plan file and arrived on the supervisor's stdin. This is the
whole standing-order pattern: orders kept in the prompt that started the loop are orders the agent
reading this brief cannot see. Unit-pinned both ways —
`Standing_orders_from_the_plan_are_in_the_brief_the_supervisor_reads`, and
`No_orders_means_no_key_rather_than_an_empty_one` (unset, blank, and block-disabled all omit the key).

## 4. The hourly fuse survives process death

The shipped shape is a shell loop, so every wake is a **fresh** `conductor watch` process and an
in-process counter would reset on the very event it exists to bound. Rig B: `maxPerHour: 1`, with one
fire left on `.conductor/supervisor-fires.log` by an earlier process before the watch was spawned.

```
watch R exit 0   (it must still WAKE -- the fuse silences the supervisor, not the watch)
supervisor ran despite the cap : False   (must be False)
brief still printed on stdout  : 34 lines
  supervisor not run - rate limited: 1 supervisor fire(s) this hour, cap 1
    (raise supervisor.maxPerHour, or read the brief yourself - a supervisor hitting
     this is usually a run stuck on one cause)
```

The watch still woke and still printed its brief: the fuse silences the **supervisor**, never the
watch. A skipped fire always says so on stderr — silence would read identically to a supervisor that
ran and had nothing to say, and those are opposite situations. Fail-open is deliberate and pinned
(`An_unreadable_ledger_leaves_the_run_supervised_rather_than_silent`): a corrupt counter must never be
the reason nobody is watching.

## 5. `docs/operating.md`

New section under §3, **"Unattended supervision — the night watch (SF5)"**: the wake table (seven
reasons, each with its first moves), the **don't-wake** table (usage-limit backoff, stall backoff,
session churn, gate PASS, a single phase RED — the half that makes the verb worth having), the
supervisor block with a worked example, and the standing-order pattern. `watch` also joins the
Monitoring command table. Section numbers were left alone on purpose: `AGENTS.md` cites
`operating.md` §4 and §7.

## Tests

`tests/Conductor.Tests/SF5_2SupervisorTests.cs`, 16 tests — precedence (5), the fuse (5), the orders
(3), plan-file round trip through `Validate()` (1), and one end-to-end that runs a real process and
reads the brief back off its stdin (1).

```
dotnet test --filter "SF5_2Supervisor|SF5_1Watch|WatchBrief"
Passed!  - Failed: 0, Passed: 49, Skipped: 0, Total: 49, Duration: 17 s
```

Nothing was weakened to get here: no test deleted, skipped or relaxed, no gate touched.
