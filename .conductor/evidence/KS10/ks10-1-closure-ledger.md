# KS10.1 — closure ledger evidence (2026-08-15, session 23)

The ledger itself is the durable artifact and lives in `.conductor/followups.md`, section
**"KS10.1 closure ledger — 2026-08-15"**. This file is the proof that it is *complete*: the
mechanical diff between what the stores say and what the ledger names, plus the raw commands.

---

## 1. The diff the contract asks for — empty

Bug ids were read straight out of the stores (read-only SQLite opens, `mode=ro`) and out of the
ledger's own table rows (`^\|\s*#(\d+)\s*\|`), then compared:

```
LIVE store open      : 15, 18, 19, 21, 23, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46
KARVAN-only open     : 24, 27, 31, 35        <- invisible to `conductor bug list` in this repo
LEDGER rows          : 15,16,17,18,19,20,21,23,24,27,31,35,36,37,38,39,40,41,42,43,44,45,46

DIFF  store-open NOT in ledger      : []      <- nothing dropped
DIFF  ledger rows that are not open : [16, 17, 20, 36]
                                      ^ intentional: the "Bugs closed by this era" table
```

**Nothing open is missing from the ledger, and every extra row is a deliberate closure record.**

Raw `conductor bug list --all` output, taken through the fresh build (the PATH engine can no longer
open the store — see §4): `.conductor/evidence/KS10/ks10-1-bug-list-raw.txt`.

## 2. Where the bugs actually live — three stores, not one

| store | runs | bug ids | schema |
|---|---|---|---|
| `…/runs/conductor-karvansara-core---the-open-door-308cfb9b/run.db` (**live**) | sarban-core, sarban-face, karvansara | 1–23, 36–46 (34 rows) | v14 |
| `…/runs/conductor-karvan-core-…-b4640aef/run.db` | karvan-core | 1–35 | v12 |
| `C:/code/conductor/.conductor/run.db` (pre-K3.1 leftover, last written 2026-08-07) | sarban ×2, karvan-core | 1–35 | v9 |

Ids 24–35 are karvan's and are **absent from the live store**, so the sequence here jumps 23 → 36.
Four of them are still open (#24, #27, #31, #35) and **#35 appears in no prior ledger at all** —
K7.1's "the eleven still open ride into the next era in the database" did not hold. Filed as **#46**.

## 3. Commands that produced every figure

```powershell
# budget / money re-measure (KS10.1's other half) - fresh build, never the PATH engine
dotnet run --project src/Conductor -- budget --json > .conductor/evidence/KS10/ks10-1-budget-remeasure.json
dotnet run --project src/Conductor -- money  --json > .conductor/evidence/KS10/ks10-1-money-remeasure.json

# the bug list the ledger is diffed against
dotnet run --project src/Conductor -- bug list --all -p plans/karvansara/core.plan.json

# the three stores, read-only
python -c "sqlite3.connect('file:<db>?mode=ro', uri=True)"   # bugs, schema_version
```

The three KS5 gaps were each re-measured rather than restated; the file:line citations are in the
ledger. The `approve --yes` probe was run from a scratch cwd against a nonexistent plan path so that
no control file could be written to any live run:

```
error: Unknown option 'yes'.        exit=1
```

## 4. What this session did to the run — recorded because it changes the next one

`MigrationRunner.Run` (`src/Conductor.Core/Store/MigrationRunner.cs:21-45`) migrates the store on
**every** `RunDb` construction, including read-only reporting verbs. Running `budget --json` through
the fresh build — which KS10.1's acceptance requires, since the PATH copy is the mid-era snapshot —
took the live store v13 → v14. The driving engine is `0.4.1-alpha.0.49+9bf2742` (`CurrentVersion`
13), so every **new** PATH invocation now refuses at `MigrationRunner.cs:29`:

```
run.db schema version is newer (14) than supported (13). Use a newer Conductor build.
```

`conductor note`, `conductor task` and `conductor bug` all died mid-session. The claim went through
`dotnet run --project src/Conductor -- task --done … -p plans/karvansara/core.plan.json`, which
works. The run itself survived because `ConductorHost.cs:160` registers `IRunStore` as a singleton —
the supervisor holds its v13 connection and never re-migrates — but **a restart of that engine
before the owner reinstalls cannot reopen the store.** Filed as bug **#45**, severity high.
