# KS12.1 — closure ledger evidence (2026-08-19, session 21)

The ledger itself is the durable artifact and lives in `.conductor/followups.md`, section
**"KS12.1 closure ledger — 2026-08-19"**. This file is the proof that it is *complete*: the mechanical
diff between what the stores say and what the ledger names, plus the raw output of every measurement
the ledger cites.

---

## 1. The diff the contract asks for — empty on the side that matters

Bug ids were read straight out of both stores (read-only SQLite opens, `mode=ro`) and out of the
ledger's own table rows (`^\|\s*\*{0,2}#(\d+)`), then compared:

    LIVE store open      : 15, 18, 19, 21, 23, 37, 38, 39, 40, 41, 42, 43, 45, 46, 47, 48, 49,
                           51, 52, 53, 54, 55, 56, 57, 58, 59, 60          (27)
    KARVAN-only open     : 24, 27, 31, 35                                   (4)  <- invisible to
                                                                                 `conductor bug list`
    LEDGER rows          : 15,18,19,21,23,24,27,31,35,37,38,39,40,41,42,43,44,45,46,47,48,49,
                           50,51,52,53,54,55,56,57,58,59,60                 (33)

    DIFF  store-open NOT in ledger      : []        <- nothing dropped
    DIFF  ledger rows that are not open : [44, 50]  <- intentional: the "Bugs closed by this era" table

(The diff above was taken before this session filed **#61**, which the ledger also names — see §7.
Re-running it now reports 28 open in the live store and 34 ledger rows, and the two diffs stay the
same: nothing open missing, #44 and #50 the only deliberate extras.)

**Nothing open is missing from the ledger, and both extra rows are deliberate closure records.**
Raw `conductor bug list --all`: `.conductor/evidence/KS12/ks12-1-bug-list-raw.txt`.

## 2. The store split KS10.1 found is unchanged — re-measured, not restated

| store | ids held | open | schema |
|---|---|---|---|
| `…/runs/conductor-karvansara-core---the-open-door-308cfb9b/run.db` (**live** — sarban ×2, karvansara-core, karvansara-edge) | 1–23, 36–60 (48 rows) | 27 | v14 |
| `…/runs/conductor-karvan-core-…-b4640aef/run.db` | 24–35 (12 rows) | 4 | v12 |
| `C:/code/conductor/.conductor/run.db` (pre-K3.1 leftover) | — | — | present on disk, written by nothing |

Bug **#46** is therefore unchanged: #24, #27, #31 and #35 are open in a store no karvansara session
opens, so no edge prompt ever carried them. This table is still the only thing moving them forward.

*(One reading differs from KS10.1's table, and it is recorded rather than smoothed over: KS10.1 wrote
karvan's store as holding ids **1–35**; opened today it holds **24–35**, twelve rows. The four open
ones are the same four, so nothing the ledger depends on changes — but the earlier row count did not
reproduce, and a ledger that quietly matched it would be the exact failure this file exists to catch.)*

## 3. Bug #44 — the row the contract names, and it closed inside this era

`bugs.updated_at` for #44 is **2026-08-19 02:04:07**, status `fixed`. That timestamp is inside the
edge run's window (started 2026-08-18 18:09 UTC) and lands on stage **KS6**, commit `0cb514d`
*"the analyzer-debt ratchet, and 14 pragmas that were guarding nothing"*.

KS10.1 assigned #44 to **the owner** with this reasoning, quoted from its own ledger: *"raising the
ceiling is the one move a session may not make, so this is a decision, not a task."* That reasoning
was right and the outcome went the other way: KS6.2 did not raise the ceiling, it **retired fourteen
suppressions that were guarding nothing**, which took the count under the bar and then let the bar
tighten from 38 to 31. Recorded because it is a reusable result — an "owner decides" row can
sometimes be closed by making the decision unnecessary.

## 4. The gate this era leaves red — run, not described

    $ powershell -NoProfile -File tools/gates/analyzer-debt.ps1
    analyzer-debt: bar is the MINIMUM over the last 25 commits that touched a measured file (25 found)
    analyzer-debt: pragma-src           bar=31   now=33   unjustified=0
    analyzer-debt: pragma-tests-tools   bar=0    now=0    unjustified=0
    analyzer-debt: suppressmessage      bar=0    now=0    unjustified=0
    analyzer-debt: nowarn               bar=0    now=0    unjustified=0
    analyzer-debt: severity-downgrade   bar=15   now=17   unjustified=0   (count not ratcheted)
    analyzer-debt: severity-blanket     bar=0    now=0    unjustified=0
    analyzer-debt: TOTAL                bar=46   now=50   unjustified=0
    analyzer-debt: rules-enforced       bar=50   now=50   un-enforced=0

    ANALYZER-DEBT GATE FAILED:
      * SUPPRESSIONS ROSE - kind 'pragma-src' went 31 -> 33 (#pragma warning disable under src/).
        The bar of 31 was set by commit 9707af7a1, and committing this does not move it.
    exit=1

    $ powershell -NoProfile -File tools/gates/ratchet.ps1
    complexity-budget: OK - all three rules enforced, every project budgeted, nothing loosened.
    RATCHET GATE FAILED - the bar was lowered:
      * ANALYZER SUPPRESSIONS ABOVE CEILING (33 > 31).
    exit=1

The two suppressions above the bar, located by `git diff 9707af7..HEAD -- src`, are **both MA0045**
and both added by KS4.4 (`05696d4`):

    +#pragma warning disable MA0045 // one small sidecar written at attempt creation; async buys
                                    // nothing and the caller is sync
    +#pragma warning disable MA0045 // teardown is a synchronous finally-block concern; the sleep is
                                    // injectable and bounded

`unjustified=0` on every kind, so the tool agrees they are argued rather than dumped. **Neither script
is a gate in `edge.plan.json`** — that battery is engine build/test and face build/vet/test — so this
is a repo bar the PR template names, not a red run battery. It is stated rather than fixed because
the only legitimate close is to make those two helpers genuinely async, which is KS4's code; raising
the bar is the move a session may not make. Filed as **#60**.

Direction of travel, for whoever reads this next: #44's predecessor state was **43 against a ceiling
of 38**; this branch is **33 against 31**. The debt fell by ten while the bar tightened by seven.

## 5. The three KS5 gaps — re-measured today, at file and line

| gap | what was opened | verdict |
|---|---|---|
| the Face's `tokens cap` row quotes the plan file | `face-go/internal/tui/tab_home.go:662-664` — `if lim := m.plan.doc.Limits.MaxRunTokens; lim != nil && *lim > 0 { rows = append(rows, hRow("tokens cap", …` | **STILL OPEN**, unchanged by edge. The cost row at `:609` reads `MaxRunCostUsd` from the same document |
| `approve` lost `--yes` / `--force` | `src/Conductor/Commands/ApproveCommand.cs` — `Settings` declares exactly two `[CommandOption]`s: `--amount <USD>` and `--tokens <N>` | **STILL OPEN**, unchanged by edge |
| owner-gate + lowered cap spends a session before parking | `src/Conductor.Core/Orchestration/RunLoop.Budget.cs:49` — `if (_ctx.State.Status == RunStatus.AwaitingOwner) return true;`, under a comment saying any other awaiting-owner reason outranks the cap **deliberately**, so the check cannot rewrite somebody's decision into a request for money | **AS DESIGNED**, and the design is written at the line. Closed as a decision |

## 6. Commands that produced every figure

    # budget / money re-measure - fresh build, and NEVER against the live store (see 7)
    python  <sqlite3.backup of the live run.db to %TEMP%\ks12-budget-copy\run.db>
    dotnet run --project src/Conductor --no-build -- budget --json "%TEMP%\ks12-budget-copy\run.db"
    dotnet run --project src/Conductor --no-build -- money  --json "%TEMP%\ks12-budget-copy\run.db"

    # the bug list the ledger is diffed against
    conductor bug list --all -p plans/karvansara/edge.plan.json

    # both stores, read-only
    python -c "sqlite3.connect('file:///<db>?mode=ro', uri=True)"   # bugs, schema_version, runs

    # the two bars, run rather than quoted
    powershell -NoProfile -File tools/gates/analyzer-debt.ps1
    powershell -NoProfile -File tools/gates/ratchet.ps1

## 7. What this session did NOT do to the run — and it is the reason #45 is still worth its severity

KS10.1's closing section records that it broke the driving engine: `budget --json` through the fresh
build migrated the live store v13 → v14, the PATH engine supported 13, and `conductor note`, `task`
and `bug` all died mid-session. That is bug **#45**, filed `high`, still open.

**It would have happened again today, one version along.** Measured, not assumed:

    src/Conductor.Core/Store/MigrationRunner.cs:11   public const int CurrentVersion = 15;   # this branch
    git show master:…/MigrationRunner.cs             public const int CurrentVersion = 14;   # what 0.4.1 ships
    conductor --version                              0.4.1+12741973f209                      # the engine driving this run
    schema_version in the live run.db                14

`MigrationRunner.Run` migrates on **every** `RunDb` construction including read-only reporting verbs,
and refuses a newer store at `MigrationRunner.cs:27-29`. So the fresh build's `budget` would have
taken the live store to 15 and locked the driving engine out of it.

The workaround costs nothing and is the recommendation for every future era-close: take a
`sqlite3.backup` copy and hand it to `budget` as a **positional db path**. The live file is never
opened for write; the copy is migrated to v15 and discarded.

One trap for whoever repeats it: **`CONDUCTOR_RUN_DB` does not redirect `budget`.** Despite being
documented as the highest-precedence override (`StateHome.cs:27-29`), that verb resolves the run by
repo path first and answers:

    no runs to measure for C:\Code\conductor. a budget is measured from a run's own sessions;
    try conductor budget --repo all or point it at a database: conductor budget path/to/run.db

The positional path is the only seam that works. Worth a bug of its own if the next era wants
`CONDUCTOR_RUN_DB` to mean what its own doc comment says.
