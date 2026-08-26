# DV7.1 — the internal record: what was measured, and with what command

Session #21, 2026-08-26. Every claim in the three artifacts this checkpoint landed is reproduced here
with the command that produced it. Nothing below is read off a doc comment.

Artifacts claimed:

- `docs/dev/adr/0008-the-courier-outlives-the-run.md` (new) + a second addendum on
  `docs/dev/adr/0005-push-only-remote-observability.md` + the ADR index row in `docs/dev/README.md`
- `ARCHITECTURE.md` reconciled, incl. a new top-level section for the courier and the inbox
- `.conductor/followups.md` — the DV7.1 closure ledger section
- `docs/dev/TOKEN-BUDGET-TUNING.md` §13, and `plans/divan/core.plan.json` corrected in place
- raw: `dv7-1-budget-raw.txt`, `dv7-1-budget-divan.json` (this directory)

---

## 1. The store, opened safely (trap 18)

```
$ grep -n "CurrentVersion = " src/Conductor.Core/Store/MigrationRunner.cs
11:    public const int CurrentVersion = 15;
$ conductor --version                     # the engine driving this run
0.4.2-alpha.0.79+870786f5b17a.dirty
$ git show 870786f5b17a:src/Conductor.Core/Store/MigrationRunner.cs | grep "CurrentVersion = "
11:    public const int CurrentVersion = 15;
```

**Both sides v15 — the hazard did not arm.** Measured anyway against a copy:

```
$ sqlite3 "<state-home>/runs/conductor-karvansara-core---the-open-door-308cfb9b/run.db" \
    ".backup 'C:/Users/shahi/AppData/Local/Temp/dv71/run-backup.db'"
$ sqlite3 <copy> "select * from schema_version;"        -> 15
```

The live run.db was never opened for write. A scratch state home was built beside the copy —
`catalogue.json` with the one `runDb` path rewritten — and `budget` pointed at it with `--home`.

**Finding, and it supersedes KS12.1's workaround.** KS12.1's closure ledger says the positional-db
argument is "the only seam that works" for trap 18 and that `CONDUCTOR_RUN_DB` does not redirect the
verb (bug #61, still open). `budget --home <dir> --repo <repo>` is cleaner: it reads an entire
alternate state home, so the live file is not opened at all.

## 2. The budget, through the fresh build

```
$ dotnet run --project src/Conductor --no-build -- budget \
    --home C:\Users\shahi\AppData\Local\Temp\dv71\home --repo C:\code\conductor
$ dotnet run --project src/Conductor --no-build -- budget aa916828 --home ... --json
```

Divan, run `aa91682821c14666915c16317a4fc72c`, window sessions 2–20:

| | |
|---|---|
| cap / nudge | 35M / 31,728,841 (ratio 0.9065) |
| costed sessions | 18 |
| tokens | 376,894,713 |
| checkpoints | 20 |
| tokens/ckpt | 18,844,736 |
| floor | 237,814 |
| median closer | 27,692,133 · **max closer 35,285,722** |
| rollover | 1 / 18 (5.56%) |
| nudged | 7 · **nudgedAndEndedClean 6** |
| wrap-up | 1.43M / **2.42M** / 3.41M (n=5) |
| headroom | 3,271,159 = 1.4× wrap-up |
| **prescription** | **maxSessionTokens 42,000,000 · softBreakRatio 0.9** (nudge 37.8M, headroom 4.2M = 1.7×) |

Cost, from the same copy:

```
$ sqlite3 <copy> "select count(distinct session_number), sum(tokens_in), sum(tokens_out),
    sum(tokens_cache), round(sum(cost_usd),2), round(sum(wall_ms)/3600000.0,1)
    from costs where run_id='aa916828…';"
18 | 3803860 | 1595328 | 371495525 | 289.04 | 10.1
```

$289.04 / 20 ckpts = **$14.45 per checkpoint**; cache-read share **98.567%**. Per-stage figures and
their derivation are in TOKEN-BUDGET-TUNING §13's table.

**Where the measurement disagrees with the doc, and it does twice.** §12 prescribed 17.5M/$13.80 per
checkpoint; outturn 18.84M/$14.45 (+7.7%/+4.7%). And the edge prescription justified 35M/0.9 as
"headroom 3.5M is 2.0× the 1.74M measured wrap-up" — Divan's own wrap-up is 2.42M, so the real
headroom ran at 1.4×, under the ≥1.5× rule, for the whole era. `plans/divan/core.plan.json` is
corrected in place; its **limits are deliberately unchanged** because this run is mid-flight (trap 14).

**One caution on the verb's own output.** It prints "THE RAIL IS DELIVERED AND IGNORED: all 1 killed
sessions had already been nudged and not one of them stopped … here it converted zero." True of the
killed set; misleading about the rail, which converted **6 of 7**. Read `nudged` /
`nudgedAndEndedClean` in the JSON before acting on that sentence.

## 3. The closure ledger — every number

```
$ sqlite3 <copy> "select status, count(*) from bugs group by status;"    -> fixed 37, open 33  (70 rows)
$ sqlite3 <copy> "select id from bugs where updated_at>='2026-08-25';"   -> 25 rows touched this era
```

- **Closed by this era: 16.** Twelve on the Divan run (#62–#69, #71, #74, #77, #78) and four carried
  in from earlier eras (#15, #21 Sarban face; #38 Karvansara core — the getUpdates 409 conflict loop,
  closed by the courier owning the token; #55 Karvansara edge).
- **Open at the close: 33** — 9 filed this era (#70, #72, #73, #75, #76, #79, #80, #81, #82) and 24
  carried in.
- **The KS10.1 store split is closed.** karvan #24 → #70, #27 → #71 (fixed here), #31 → #72,
  #35 → #73, each with a `RECOVERED … where it is karvan bug #NN` back-reference in its detail
  (`select id,status,detail from bugs where detail like '%karvan bug%'`). Ids 24–35 are permanently
  absent from this store by design — re-minted, not moved. Bug #46 stays open as the class.

**Reconciled against the DV2 triage, and Ledger 1 is exact.**
`docs/dev/DIVAN-BUG-SWEEP-2026-08-25.md:23` enumerates *15, 18, 19, 21, 23, 37–43, 45–49, 51–61* = 28
ids. Recomputed: the 24 still open plus the 4 closed here (#15, #21, #38, #55) = **the same 28**, no
id in one and not the other. A naive `#\d+` scan of that document returns 27 and appears to drop #56 —
the sweep writes ranges without a `#` per member. The ranges are the enumeration; #56 is named.

**The one error found, and corrected in place.** Ledger 2 lists **FU-F1-06** as open and "the oldest
standing row a session can actually clear". `.conductor/followups.md:527` records it CLOSED at KS0.2
(`15627b9`) six days earlier. The tree agrees:

```
$ grep -rn "UpdateRunStatus" src/ --include=*.cs
Conductor.Core/Orchestration/RunContext.cs:391:        s.UpdateRunStatus(State.RunId, text);
Conductor.Core/Store/IRunStore.cs:38:    void UpdateRunStatus(string runId, string status);
Conductor.Core/Store/SqliteRunStore.Sessions.cs:74:    public void UpdateRunStatus(...)
```

pinned by `KS0_2RunRecordTests` and `KS0_2NoRunsUpdateOutsideTheStoreTests`. The followup ledger was
right, the sweep was wrong, and the sweep is struck through rather than deleted.

**Followup rows: 55 distinct ids across 91 table rows in 740 lines.** Taking each id's last row as its
verdict: 48 CLOSED, plus `FU-B3-2` (CLOSED at W3.3), `FU-OWNER-11` (CLOSED with a stated remainder)
and `FU-B10-2` (RETIRED as unanswerable) = **51 settled**. **Four carry forward, all the owner's**:
FU-B11-3 (owner-gated, not re-homed), FU-B2-3 (PARTIAL), FU-B11-2 (PARTIAL is final), FU-OWNER-14
(the reinstall clause, KS10.3 → KS12.3, inherited by DV7.3).

**The era wrote to `.conductor/followups.md` exactly zero times before this ledger** —
`git log feat/karvansara-edge..HEAD -- .conductor/followups.md` is empty.

**The closure section adds no `| FU-…` rows**, on purpose: bug #81 measured that the file's 91 rows
for 55 ids make the ledger mirror report phantom updates. Row count before and after the append:
**91 → 91**.

## 4. ARCHITECTURE.md — what was measured before it was written

| claim in the doc | as it stood | measured today | command |
|---|---|---|---|
| seams | "exactly **ten**" | **13** | `grep -rn "public interface I" src/Conductor.Core` |
| surfaces | "The **two** surfaces" | **4** (control plane, Face, MCP, courier) | reading the subsections + `CourierListener` |
| GitHub | "nothing is ever read back" | two reads, both write-shaping | `GithubRepoInfo.cs:5-6`, ADR-0005's own addendum |
| boundary rules | "**12** `[Fact]` rules" | **13** | `grep -c "\[Fact\]" tests/…/ArchitectureBoundaryTests.cs` |
| hosted services | "`TelegramService` is the only one today" | **still true** | `grep -rn "IHostedService" src/` → `ConductorHost.cs:121` is the only registration |
| the courier's listener | not mentioned | **not in the run process** | `grep -rn "new CourierListener" src/` → `CourierCommand.cs:276` only |

The last two matter together: the courier is a **process**, not a hosted service, so the "no
`IHostedService` running the loop" paragraph gained a pointer rather than a correction. Writing it the
other way round would have been the easy mistake.

Also corrected: KS11's messenger section used the word "courier" as a metaphor, which now collides
with a real namespace and a real verb.

## 5. Gates run

```
$ dotnet build Conductor.slnx                                          Build succeeded, 0 warnings, 0 errors
$ dotnet test Conductor.slnx --filter "FullyQualifiedName~Docs"        Passed! 42/42
$ dotnet test Conductor.slnx --filter "FullyQualifiedName~Architecture" Passed! 21/21
$ dotnet run --project tools/plan-lint -- plans/divan/core.plan.json   clean after the plan comment edit
```

Both suites were run **after** the ARCHITECTURE edits. No test, ceiling, golden or baseline was
touched.
