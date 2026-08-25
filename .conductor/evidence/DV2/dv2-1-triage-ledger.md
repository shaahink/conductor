# DV2.1 — the triage ledger: every known defect, dispositioned

*Measured 2026-08-25, session #3 of the Divan run, at commit `83427b4` on `feat/divan`.
Fifty defect rows from three sources. Each carries exactly one disposition: **FIX** (cluster + the
regression test that must exist) or **DEFER** (a named owner + a reason). No row is dropped.*

## How the tables were measured

The bugs table was read from the **live** store named by `.conductor/state-pointer.json` —
`%LOCALAPPDATA%/conductor/runs/conductor-karvansara-core---the-open-door-308cfb9b/run.db` — through a
`cp` snapshot queried with `sqlite3` (trap 18: no fresh build ever opened the live store for write).
The pre-import copy at `.conductor/run.db` was read the same way, alongside it, to recover the rows
the state-home split lost. `.conductor/followups.md` was swept case-insensitively for open rows, not
by the `grep -c OPEN` line count that produced the strand doc's figure.

**One trap this artifact fell into and climbed out of, because the next session will meet it.** The
store is in WAL mode. A snapshot that copies `run.db` alone and leaves `run.db-wal` behind reads
**stale**: the first pass of the measurement file below reported "28 open" *after* this checkpoint
had already committed twelve rows — the writes were still in the 3 MB WAL. A snapshot must copy
`run.db`, `run.db-wal` and `run.db-shm` together. It is the same shape of error as reading the wrong
store file, and it produces the same symptom: a confident number that is quietly out of date.

| store | rows | open | ids |
|---|---|---|---|
| live (`state-pointer.json`) — at triage time | 49 | **28** | 1–23, 36–61 (24–35 absent) |
| live — after this checkpoint filed 12 | 61 | **40** | 1–23, 36–73 (24–35 still absent as ids) |
| imported copy (`.conductor/run.db`) — frozen pre-split | 35 | **11** | 1–35 |

## §1 — the 41/44/60 numbering, resolved against the real table

**`#44` is not dangling.** The row exists in the live store and is **`fixed`**, stage `KS9`:
*"ratchet gate is RED before this session: 43 analyzer suppressions against a ceiling of 38"*. The
strand doc's *"no open row carries it"* is literally true and misleading — the row is **closed**, not
missing. It is **not** superseded by `#60`: they are two different measurements, on two different
branches, against two different bars. `#44` (43 vs 38) was cleared at KS9; `#60` (33 vs 31) is a
later regression on `feat/karvansara-edge` that `feat/divan` stacks on. Both numbers are real.

**`#41` and `#60` both exist and are both open** — `#41` payesh anonymity on the generic word
"website" (KS10), `#60` the analyzer-debt ratchet (KS4). Neither is a phantom.

**The count discrepancy has a single cause, and it is not arithmetic.** The strand doc's first draft
reported **11** open bugs. Eleven is exactly the open count of the **imported copy** (measured above),
so that draft resolved the store by the repo-local path. The live store held 28. This is bug `#45`'s
sibling failure mode — *which* store a verb resolves — and it is why this ledger states its store
resolution in full before its first number.

**The arithmetic of this ledger:** 28 live-open + 4 recovered from the imported copy + 8 field
defects newly filed + 2 field defects recorded-not-filed + 8 open followup rows = **50 rows**. Field
item 2 is bug `#38` and is counted once, in the live-open set. 14 rows are FIX; 36 are DEFER.

**The `#46` recovery is four rows, not six.** Bug `#46`'s own title names `#24/#27/#31/#35`; the
strand doc's prose widened that to `#16/#20/#24/#27/#31/#35`. Measured: `16` and `20` **are** in the
live store and both are `fixed` — they were never lost. The genuinely absent rows are the four the
bug names, and this checkpoint refiled all four into the live store with their provenance in the
detail field (new ids `#70`, `#71`, `#72`, `#73`). The id range 24–35 stays a permanent gap; twelve
ids, of which eight were already-`fixed` karvan rows that need no home.

## §2 — corrections this triage made to the strand doc

Three, all measured, all cited. The strand doc is a starting map, not a verdict.

1. **Ledger-3 item 4 names the wrong symptom.** `POST /telegram/test` does **not** crash on an empty
   list: `TelegramService.cs:231` guards with `if (_cfg.AllowedChatIds.Count == 0) return new
   TelegramTestOutcome(false, …, "there is no chat to send it to")` **before** the
   `AllowedChatIds[0]` index at `:237-238`. The real defect is a **false failure**:
   `TelegramConfig.ResolvedChats()` (`Models/TelegramConfig.cs:33-49`) is `Chats` ∪ `AllowedChatIds`,
   so a chats-only plan has `ChatCount > 0` while `AllowedChatIds.Count == 0` — the endpoint reports
   "no chat" on a plan that delivers perfectly, and the `[0]` index would pick the wrong chat if it
   ever ran. Same raw-vs-resolved seam as item 3, which is **confirmed as written** at
   `TelegramService.Lifecycle.cs:53`. Both fix by reading `ResolvedChats()`.
2. **Ledger-3 item 8 is not "no 429 handling exists".** `SessionRunner.cs:411` already calls
   `_queueResume(rec, "usage/rate limit backoff", false, false)`. DV2.4 must **measure which path**
   the field-observed 19-second retry loop actually takes before writing a fix; "the engine already
   has `backoffMinutes`" is a seam that is partly wired, not an absence.
3. **A second, independent truncation was found while triaging, and it is in no ledger.**
   `BugsBattery` caps at **12** rows (`PromptBattery.Knowledge.cs:51`, `:70-76` — this run's rows
   first, carried rows fill the remainder) and renders **no "N more" line**. At triage time 28 bugs
   were open and 12 reached the prompt: **16 were invisible to every session**; after this
   checkpoint, 40 are open and **28** are invisible. This is a different defect from the
   `BatteryGroup` byte cap and DV2.2's "rendered notice" must cover both. Filed as `#63`.

A fourth measurement, recorded because it is a live consequence of this checkpoint: filing 12 rows
under **this** run means this run's own bugs now claim all 12 battery slots, so the carried
karvansara rows — including `#15`, `#21` and `#55`, which are DV2.2's work — **stop appearing in the
prompt**. That displacement is real, it is `#63` demonstrating itself, and it is why the next three
sessions must take their row ids from **this file**, not from the open-bugs battery.

## §3 — the ledger

Disposition is **FIX ‹cluster›** or **DEFER → ‹owner›**. `src` is the source ledger:
`L1` = bugs table, `L2` = `followups.md`, `L3` = the field log (item number).

### FIX — cluster A, prompt composition (DV2.2). Outranks the rest: DV3 lands a battery on this seam.

| id | src | defect | regression test that must exist |
|---|---|---|---|
| `#62` | L3-1 | `BatteryGroup.Render` truncates the **concatenation** at `_maxBytes` (`PromptBattery.cs:41-62`), so a grown ledger deletes the open-bugs battery whole — the "added FIRST so the byte cap never truncates them away" comment at `PromptBuilder.cs:296-311` is false for every battery but the first | a grown ledger (enough entries to blow the cap) still renders `### open bugs` in full; per-battery shares asserted, not just total bytes |
| `#63` | new | `BugsBattery` drops every open bug past the 12th with no notice (`PromptBattery.Knowledge.cs:51,70-76`) | 40 open bugs → the rendered section names how many were not shown |
| `#15` | L1 | a composed prompt over ~8191 chars silently stops a cmd.exe agent and the run reports success | a prompt over the ceiling is refused or split, never silently dropped; the run does not report success |
| `#21` | L1 | nothing warns when a plan's packs push the composed prompt past the argv ceiling | a plan whose packs cross the ceiling emits a named warning at preflight |
| `#55` | L1 | doctor's argv lint under-measures the real spawn by 350–500 chars — it misses the battery, the tail sections and the orchestrator's own flags | the lint's number equals the argv `SessionComposer` actually spawns, asserted against a composed spawn |

`#55` is **moved into cluster A by this triage** (the stage notes list only `#15` and `#21`). Reason:
`#21`'s warning is only honest if `#55`'s measurement is, and both live in `DoctorCommand.PromptSemantics`.
The stage notes permit a row to move between clusters; none is lost.

### FIX — cluster B, channels (DV2.3). Stub seam, scratch tokens only.

| id | src | defect | regression test that must exist |
|---|---|---|---|
| `#38` | L1 / L3-2 | `getUpdates` 409 conflict loop: two engines share one token, inbound control dies with no line naming the cause | a stubbed 409 is detected, logged naming the other consumer, and backed off — not retried hot |
| `#64` | L3-3 | the started log counts `AllowedChatIds` and says "will deliver nothing" on a chats-only plan (`TelegramService.Lifecycle.cs:53`), contradicting `/telegram/status` | a chats-only plan starts with no false warning; the line's number equals `ChatCount` |
| `#65` | L3-4 | `POST /telegram/test` reports "no chat to send it to" on a chats-only plan and would index the wrong chat (`TelegramService.cs:231,237-238`) — see §2.1, the symptom is a false failure, not a crash | a chats-only plan's test endpoint sends, and to a resolved chat |
| `#66` | L3-5 | `report push failed:` logs an empty reason | a stubbed push failure logs a non-empty, named reason |

### FIX — cluster C, state and verdict (DV2.4), then the close.

| id | src | defect | regression test that must exist |
|---|---|---|---|
| `#67` | L3-6 | the stage-boundary squash aborts a **stale** rebase left by a killed session and rewinds the branch (measured: HEAD back 28 commits, run `d6fd22ba`); seam at `Git.cs:187-191` | a seeded stale `rebase-merge` is refused (park, not abort); after any abort the pre-squash tip is asserted still an ancestor of HEAD |
| `#68` | L3-7 | budget counters restart at zero on every engine process start — `maxRunCostUsd` is a per-**process** cap, unbounded across restarts | a `--once` exit then a restart restores non-zero cost/tokens from the store |
| `#71` | L1 (recovered `#27`) | a brand-new `run.db` logs `FOREIGN KEY constraint failed` on the first `run_state` write | a fresh store's first `run_state` write logs no FK failure |
| `#69` | L3-8 | a 429 storm burns a stage's attempt budget in minutes and ends `NEEDS HUMAN` — see §2.2, a backoff seam already exists at `SessionRunner.cs:411` | a 429 carrying a reset time classifies as backoff once, not as one `AgentError` per retry; attempts are not consumed |
| `FU-F1-06` | L2 | `UpdateRunStatus` exists nowhere in `src/`, so a run ending `NeedsHuman`/`Paused`/`AwaitingOwner` still reads `status='running'` — the "immortal running" record | a run ended in each non-terminal status reads that status back from the store |

DV2.4 also closes the ledger: every FIX row above closed with `conductor bug fix <id>` in the same
commit as its regression test, and every DEFER row below carrying its named owner.

### DEFER — with a named owner and a reason

| id | src | defect | owner | reason |
|---|---|---|---|---|
| `#18` | L1 | bottom bar hard-clips a pane's contextual help, no ellipsis | next era, face lane | Divan has no face stage; cosmetic, and the face lane owns the clamp idiom |
| `#19` | L1 | session digest never records a claim — `Claims` needs an MCP `task_update`, every session claims through the CLI | next era, verdict lane | one change with `#52`; both are the digest's claim counting, and neither is in cluster C's named acceptance |
| `#23` | L1 | CI Windows battery flakes on `McpBgStatus_CallsAnUninspectablePidRunning_NotDead` | next era, CI lane | GH-runner-only; not reproducible on this machine, so a fix here would be unfalsifiable |
| `#37` | L1 | `history --json` misses non-terminal baton rows | next era, store lane | same family as `FU-F1-06`; revisit once `UpdateRunStatus` exists and non-terminal rows are writable |
| `#39` | L1 | an interrupted session leaves a non-terminal `running` session row at $0.00 no verb can clear | next era, store lane | the **session**-row twin of `FU-F1-06` (the **run** row). Natural follow-on once DV2.4 lands the run-status writer |
| `#40` | L1 | verdict counts satellite-repo commits by **anyone** as the session's own work | next era, verdict lane | Divan declares one satellite (`conductor-site`) and no stage before DV7 touches it, so the blast radius is nil this era |
| `#41` | L1 | payesh anonymity fails closed on the generic word "website" | next era, payesh lane | **watch:** DV7.2 re-runs the harvest end to end and will hit this. If it blocks that proof it becomes DV7.2's, not a new row |
| `#42` | L1 | catalogue repair cannot collapse a duplicate in the **live** store | next era, store lane | refusing a live store is correct; the fix is an out-of-band path, which is a design call, not a patch |
| `#43` | L1 | 4-digit import ids pass the progress provider, fail `EvidenceArtifact`'s reader | next era, import lane | no import runs this era |
| `#45` | L1, **high** | any verb from a newer build silently migrates the live `run.db` and locks the running engine out | next era, store lane | mitigation is in force and load-bearing (trap 18, in every prompt of this run). The engine fix is a store-version guard, which is a store-lane change with its own compatibility story |
| `#46` | L1 | bugs do not survive a state-home split | next era, store lane | **its data loss is repaired by this checkpoint** — the four rows it names are back in the live store as `#70`–`#73`. What remains is the engine defect: the split must carry the ledger |
| `#47` | L1, **high** | payesh anonymity: a private repo whose whole name is an ordinary noun makes the check unfalsifiable | next era, payesh lane | same lane and same watch as `#41` |
| `#48` | L1 | `face` with no live run silently attaches to another repo's run | next era, face lane | acute while two conductors share this machine — but the guard belongs with the face's run resolution, not in a sweep |
| `#49` | L1 | `KS1_2StagesFromFoldTests.DerivedStatusMatchesTheStatusSurface_ForEverySeededRun` flakes under full-suite parallel load | next era, test-infra lane | a flake in the net, not in the engine; fixing it under time pressure is how a real failure gets muted |
| `#51` | L1, **high** | a restricted permission posture silently breaks the run's own claim path unless the allow list names it | next era, doctor lane | the posture itself is the owner's call; what a session can add is a doctor check, and doctor's prompt surface is `#55`'s file this stage |
| `#52` | L1 | digest `Claims` counts a claim attempt that **failed** | next era, verdict lane | pairs with `#19`, one change |
| `#53` | L1 | `cache_creation` TTL split (5m vs 1h) dropped — a rate-based cost model would misprice the write half | next era, cost lane | no rate-based model exists yet; the defect bites the model that has not been built |
| `#54` | L1 | MSBuild node reuse serves a stale analyzer config — `Conductor.Planning` fails MA00xx contradicting `.editorconfig` | next era, build lane | twin of `#57`, one root cause, one fix |
| `#56` | L1 | `ControlPlaneServer` coupling 240 — the largest single CA1506 tightening available (240 → 134 in one split) | next era, architecture lane | a refactor, not a defect; DV3/DV4 add to this file and should be measured before it is split |
| `#57` | L1 | `dotnet build` flaps red on reused MSBuild nodes; case-sensitive `.editorconfig` match drops severity overrides; `-nr:false` fixes it | next era, build lane | workaround known and documented; twin of `#54` |
| `#58` | L1 | `FailureCircuitBreaker.ParseFailingGates` matches glyphs the summary never emits (`OK`/`FAIL`/`warn`/`cached`/`-`/`REGRESSION`/`MUTANTS`) | next era, verdict lane | real, and it makes the breaker's histogram parse dead code — but it is a third verdict-lane row and cluster C is already five |
| `#59` | L1 | `dotnet run --project src/Conductor` inside a bg child fails MA0016/MA0051/MA0006 that `dotnet build` never produces | next era, build lane | same family as `#54`/`#57`; **watch:** trap 2 makes every live proof this era use `dotnet run`, so if it blocks one it becomes that checkpoint's |
| `#60` | L1 | analyzer-debt ratchet RED on `feat/karvansara-edge`: pragma-src 33 against a bar of 31 | **the owner**, with the debt reduction in the next era's analyzer lane | the edge handoff states the bar may **not** simply be moved, and moving a ratchet ceiling is the one move this run forbids outright. Paying the debt down to 31 is a lane; raising the bar is the owner's |
| `#61` | L1 | `CONDUCTOR_RUN_DB` does not redirect the measuring verbs — `budget` resolves by repo first and reports "no runs to measure" | next era, store lane | **watch:** DV7.1 re-runs `budget` against a backup copy and this is exactly the path it needs. If it blocks DV7.1 it becomes DV7.1's |
| `#70` | L1 recovered | `AgentConfig.Merge` silently drops `Env` — a stage-level agent override wipes plan-level `agent.env` (`Models/AgentConfig.cs:36-48`) | next era, engine lane | recovered to the store this checkpoint; also `followups.md:471`. No Divan plan overrides an agent per stage, so it cannot bite this era |
| `#72` | L1 recovered | face: `bubbles/textarea` blocked by string key dispatch; `widgets.TextArea` clips every line but the caret's | next era, face lane | K6.4 measured it as a K6.3-sized refactor across 220 call sites; a lossy string→`KeyPressMsg` adapter would bite later |
| `#73` | L1 recovered | `tools/w3/window-close.ps1` and `tools/sf1/sf1-2-live-proof.ps1` read the pre-K3.1 `run.db` path and write scratch runs into the operator's **real** state home | next era, tooling lane | neither rig is in CI and neither can be driven end to end from here; an unverified blind edit to a rig nobody runs is how this rotted the first time |
| `L3-9a` | L3 | the auth preflight prints "inconclusive" noise by design | next era, doctor lane | **not filed as a bug row on purpose** — see the note below the table |
| `L3-9b` | L3 | `journey` cannot show gate-to-stage scoping, so a scoped gate is unverifiable before its stage closes | next era, CLI lane | same — recorded here, not filed |
| `FU-B0-1` | L2 | MA0045 sync-over-async engine | next era, async/harden lane | B2 kept the run loop synchronous by choice; the async pass is a lane, not a sweep item |
| `FU-B1-1` | L2 | `ScriptProvider` stdout/stderr split | next era, async/harden lane | same lane, same pass |
| `FU-B1-2` | L2 | cancellation token through `IProgressProvider.Read` | next era, async/harden lane | same lane, same pass |
| `FU-B2-3` | L2 | event-log recovery (explicitly *not* process control) | next era, recovery lane | the row's own text asks to be re-homed to a recovery lane; this triage does that and no more |
| `FU-KS5-tokens` | L2 | the Face's `tokens cap` row quotes the plan **file**, not the run's effective ceiling (`face-go/internal/tui/tab_home.go:662-664`; the cost row at `:609` has the same defect) | next era, face lane | Divan has no face stage; two lines, but they need a face golden rebaseline in its own commit (trap 10) |
| `FU-KS5-approve` | L2 | `approve` lost `CtlCommand`'s `--yes` and `--force` (`ApproveCommand.Settings` declares only `--amount` and `--tokens`) | next era, CLI lane | a CLI surface change wants the docs-match-reality pins moved with it, which is DV7.2's machinery, not DV2's |
| `FU-B11-3` | L2 | real-credential cTrader owner-gated path | **the owner**, permanently | owner-gated **by its own row**, which states no future triage should re-home it. Real credentials and real money are outside what any agent in this era may do. Recorded as deferred-to-owner and untouched |

**Why `L3-9a` and `L3-9b` were recorded but not filed as bug rows.** The strand doc marks them
"file-if-cheap". Filing is cheap in tokens and expensive in prompt: `#63` measures that only 12 rows
reach any session, so each cosmetic row filed evicts a load-bearing one. They are carried here, in a
committed artifact, with named owners — which is what "no row vanishes" asks for. The four recovered
karvan rows were treated the opposite way and **were** filed, because `#46` asks specifically for
their return to the store and restoring lost data is a different act from minting new rows.

## §4 — the count, closed

| bucket | rows | FIX | DEFER |
|---|---|---|---|
| L1, open in the live store at triage | 28 | 4 (`#15 #21 #55 #38`) | 24 |
| L1, recovered from the imported copy (`#46`) | 4 | 1 (`#71`) | 3 |
| L3, field defects newly filed | 8 | 8 (`#62`–`#69`) | 0 |
| L3, field defects recorded not filed | 2 | 0 | 2 |
| L2, open followup rows | 8 | 1 (`FU-F1-06`) | 7 |
| **total distinct defects** | **50** | **14** | **36** |

Field item 2 is bug `#38` and is counted once, in L1. `FU-OWNER-9` is **not** in this ledger: its own
row records it fully closed by SF0.3 (`c84ccfc`), with its remainder re-homed to bug `#16`, which the
live store shows `fixed`. The strand doc's "11 OPEN" followups is a `grep -c OPEN` line count —
`FU-B11-3` appears on three lines, `FU-OWNER-9`'s closure line contains the word, and one line is a
cross-reference to bug `#24`. Eleven lines, **eight** distinct open rows.

## §5 — what the next three sessions take from here

- **DV2.2** (cluster A, first — DV3 lands a battery on this seam): `#62`, `#63`, `#15`, `#21`, `#55`.
- **DV2.3** (cluster B): `#38`, `#64`, `#65`, `#66`. Read §2.1 before touching `#65` — the strand
  doc's symptom is wrong.
- **DV2.4** (cluster C, then the close): `#67`, `#68`, `#71`, `#69`, `FU-F1-06`. Read §2.2 before
  touching `#69` — a backoff seam already exists.
- **Take the ids from this file, not from the prompt's open-bugs battery.** §2's fourth measurement
  says why: this run's 12 new rows now fill all 12 battery slots and evict the carried karvansara
  rows that are DV2.2's own work. That eviction is `#63`, live.
