# Conductor — Karvan core - the engine knows what it did and what it cost run report

_Updated 2026-08-05 05:59 UTC · branch `feat/karvan` · HEAD `ad6f2de`_

**Status:** Idle — stage K1 used all 8 attempts without completing — inspect and `conductor resume` (or `conductor skip`) · advisor: DNS failure on machine (ENOTFOUND github.com) is blocking K1.3 push; network connectivity must be restored and commits pushed before K1.4 can proceed. [6h 29m ago, 23:29:12Z]
**Stage:** K4 — Token truth - measure it before shrinking it · attempts used 1
**Checkpoints:** 15/32 done · **Sessions run:** 16 · **Cost:** $164.1981 (agent $164.1009 + gates $0.0972) · **Tokens:** 2,315,872 in / 1,146,551 out
**Confirmed phases:** K1, K2, K3

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| K1 | The ledger stops lying | ██████████ 4/4 | confirmed ✓ |
| K2 | The architecture becomes navigable | ██████████ 4/4 | confirmed ✓ |
| K3 | Conductor remembers | ██████████ 3/3 | confirmed ✓ |
| K4 | Token truth - measure it before shrinking it | ██████████ 4/4 | gating… |
| K5 | The result contract and the channels | ░░░░░░░░░░ 0/4 | todo |
| K6 | The surfaces read | ░░░░░░░░░░ 0/4 | todo |
| K7 | Ship the plan | ░░░░░░░░░░ 0/2 | todo |

<details> ✅<summary>K1 — The ledger stops lying (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K1.1 | A rolled-over session records the commits and claims it actually made, proven by a harness session driven to its ceiling, with a rollover still consuming no attempt and still not running the phase gate | ✅ DONE | [`93bbae5`](https://github.com/shaahink/conductor/commit/93bbae5) |
| K1.2 | The soft break is re-stated until it is obeyed, names the actual remaining budget, states the wrap-up order (claim first, handoff second), and the session record says whether it was delivered, re-delivered and obeyed | ✅ DONE | [`93bbae5`](https://github.com/shaahink/conductor/commit/93bbae5) |
| K1.3 | Three small untruths die as a class — the thinking-token column that is zero on all 125 rows, the lessons file that is a diary and repeats one entry twice, and a go.mod that calls a directly-imported package indirect while carrying two lipgloss majors | ✅ DONE | [`890ac38`](https://github.com/shaahink/conductor/commit/890ac38) |
| K1.4 | A spawned session sees conductor's task tools and the operator's own MCP servers, because the config merges instead of replacing, with the prompt-side deferred-tool fallback kept | ✅ DONE | [`36314be`](https://github.com/shaahink/conductor/commit/36314be) |

</details>

<details> ✅<summary>K2 — The architecture becomes navigable (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K2.1 | Conductor.Core holds the domain, orchestration and store with no Spectre and no HTTP hosting, Conductor is CLI plus hosting, the reference direction only points one way, and publish plus doctor plus the Face's discovery still work | ✅ DONE | [`b05efef`](https://github.com/shaahink/conductor/commit/b05efef) |
| K2.2 | Architecture tests in the ordinary suite fail the build when a boundary is crossed, each naming the offending type and the rule, landed together with K2.1 so the extraction is verified rather than asserted | ✅ DONE | [`b05efef`](https://github.com/shaahink/conductor/commit/b05efef) |
| K2.3 | The worst partial-file piles are split by responsibility — the thirty-file DTO pile becomes per-feature endpoint contracts — and one written file-organisation convention says where a new endpoint, event or partial belongs | ✅ DONE | [`b05efef`](https://github.com/shaahink/conductor/commit/b05efef) |
| K2.4 | The front door reads: a real ARCHITECTURE.md map, an AGENTS.md cut to current state with superseded handoffs archived and indexed, closed-era trackers out of the repo root, the divergent duplicate workgraph doc resolved to one file, and the docs indexes updated | ✅ DONE | [`8b38f1b`](https://github.com/shaahink/conductor/commit/8b38f1b) |

</details>

<details> ✅<summary>K3 — Conductor remembers (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K3.1 | State has a machine-level home with one catalogue keyed by repo and plan, an environment override, an idempotent migration that imports existing run.db files rather than orphaning them, and a per-run scratch dir that keeps the repo's tracked deliverables | ✅ DONE | [`707992f`](https://github.com/shaahink/conductor/commit/707992f) |
| K3.2 | conductor history lists and opens past runs read-only from the catalogue, and the Face's existing run picker offers them | ✅ DONE | [`ec1f158`](https://github.com/shaahink/conductor/commit/ec1f158) |
| K3.3 | Every run records the engine version, its commit, its dirty flag and a snapshot of the limits that governed it, and a dirty build warns at launch | ✅ DONE | [`e45fa11`](https://github.com/shaahink/conductor/commit/e45fa11) |

</details>

<details> ✅<summary>K4 — Token truth - measure it before shrinking it (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K4.1 | The engine records context size per turn — a high-water and a mean per session — derived from the stream, with the derivation checked against a session that can be estimated independently | ✅ DONE | [`ea49c8d`](https://github.com/shaahink/conductor/commit/ea49c8d) |
| K4.2 | conductor budget prints floor, wrap-up, cap, nudge-versus-floor and rollover rate and prescribes a correction, and it reproduces this repo's own two runs without being told the answers | ✅ DONE | [`1fcbd0b`](https://github.com/shaahink/conductor/commit/1fcbd0b) |
| K4.3 | conductor money answers what a project cost per checkpoint, per stage and per month, with cache-read share and the before-and-after windows that say what the cap bought, cross-checked against a hand-written query | ✅ DONE | [`20842e2`](https://github.com/shaahink/conductor/commit/20842e2) |
| K4.4 | Live session tokens, the distance to the nudge, a burn rate and a projection sit beside live money in the Face and on the wire, honest when no cap is set | ✅ DONE | [`b4ea829`](https://github.com/shaahink/conductor/commit/b4ea829) |

</details>

<details><summary>K5 — The result contract and the channels (0/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K5.1 | The session result has one format conductor owns — short headline, at most three outcome bullets, artefacts as links, evidence paths, explicit gaps — with the followup parser, the verdict parse and the session template moving in the same checkpoint and a legacy result degrading rather than throwing | ⬜ TODO | - |
| K5.2 | The five Telegram defects that make the feed unreadable are gone: one identity block from one source, the stage title beside the id, the structured result rendered instead of cut mid-word, a rollover that reports what it landed, and a progress line in every push | ⬜ TODO | - |
| K5.3 | Evidence is a first-class artifact — path, kind, checkpoint, session, sha, created-at — written as an event when an agent registers one or a watched directory gains a file, with non-text kinds first-class, a Face surface, and the existing free-text evidence field still working | ⬜ TODO | - |
| K5.4 | The message-composition layer ships owner-editable per-event templates, repo and branch and stage title and checkpoint in every push, commits and PRs as links, money with headroom, photo and document sending so evidence arrives, a thread per run, severity mapped to notify or silent, 4096-character chunking, and an ADR recording the push-only remote posture | ⬜ TODO | - |

</details>

<details><summary>K6 — The surfaces read (0/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K6.1 | An ADR fixes the TUI conventions — pager keys, focus model, help, one scroll idiom, viewport versus list versus table — after an actual read of glow, soft-serve, gh-dash and lazygit | ⬜ TODO | - |
| K6.2 | bubbles v2 is a declared dependency and Report and Knowledge scroll through a viewport, with the golden, frame-invariant and glitch-sweep tests green, any baseline regenerated in a separate rebaseline commit, and a captured frame of a long document scrolled to its end as evidence | ⬜ TODO | - |
| K6.3 | Each tab owns its own model, state, update and view, the root update becomes a dispatch instead of 826 lines and 80 cases, and the mnemonic map and the hand-maintained help legend change together | ⬜ TODO | - |
| K6.4 | One markdown renderer honours the active theme everywhere markdown belongs, the remaining primitive swaps the ADR calls for are done as far as the goldens allow, and anything deliberately left is named | ⬜ TODO | - |

</details>

<details><summary>K7 — Ship the plan (0/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K7.1 | The docs match the engine and this era's own measurements are written back — the cap's real score, the corrected nudge rule, and this run's figures produced by conductor budget rather than by hand — with every wrong claim corrected in place and a closure ledger naming an owner for everything still open | ⬜ TODO | - |
| K7.2 | feat/karvan is merged to master by the owner, the release is tagged through the existing pipeline, and the installed version matches the releases page | ⬜ TODO | - |

</details>

## Sessions

| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Overhead | Tokens |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | K1 | Deliver | 1 | 08-04 21:49 | 0:35 | Advanced | K1.1 K1.2 | 3 | engine-fast:OK · face-fast:OK | $18.0603 | $0.0083 | 219,310/95,366 |
| 2 | K1 | Deliver | 1 | 08-04 22:27 | 0:30 | Advanced | K1.3 | 6 | engine-fast:OK · face-fast:OK | $10.2462 | $0.0055 | 147,283/76,748 |
| 3 | K1 | Deliver | 1 | 08-04 22:59 | 0:02 | AgentError |  | 0 | engine-fast:cached · face-fast:cached | $0.0000 |  |  |
| 4 | K1 | Fix | 2 | 08-04 23:02 | 0:02 | AgentError |  | 0 | engine-fast:cached · face-fast:cached | $0.0000 |  |  |
| 5 | K1 | Deliver | 1 | 08-04 23:30 | 0:37 | Advanced | K1.4 | 3 | engine-fast:OK · face-fast:OK | $14.6250 | $0.0127 | 170,772/86,424 |
| 6 | K1 | Fix | 2 | 08-05 00:18 | 0:19 | Progress |  | 5 | engine-fast:OK · face-fast:OK | $6.9616 | $0.0089 | 105,085/54,557 |
| 7 | K2 | Deliver | 1 | 08-05 00:42 | 0:36 | Advanced | K2.1 K2.2 K2.3 | 5 | engine-fast:OK · face-fast:OK | $17.4644 | $0.0081 | 229,722/124,620 |
| 8 | K2 | Deliver | 1 | 08-05 01:21 | 0:16 | Advanced | K2.4 | 3 | engine-fast:OK · face-fast:OK | $11.1887 | $0.0053 | 159,355/68,728 |
| 9 | K3 | Deliver | 1 | 08-05 01:42 | 0:42 | Advanced | K3.1 | 5 | engine-fast:OK · face-fast:OK | $12.0714 | $0.0087 | 193,864/91,429 |
| 10 | K3 | Deliver | 1 | 08-05 02:26 | 0:36 | Advanced | K3.2 | 5 | engine-fast:OK · face-fast:OK | $17.6953 | $0.0049 | 205,048/111,704 |
| 11 | K3 | Deliver | 1 | 08-05 03:03 | 0:20 | Advanced | K3.3 | 2 | engine-fast:OK · face-fast:OK | $8.0198 | $0.0053 | 142,261/59,401 |
| 12 | K3 | Fix | 2 | 08-05 03:31 | 0:09 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $2.4999 | $0.0081 | 63,844/22,913 |
| 13 | K4 | Deliver | 1 | 08-05 03:47 | 0:22 | Advanced | K4.1 | 2 | engine-fast:OK · face-fast:OK | $9.1685 | $0.0045 | 146,429/65,841 |
| 14 | K4 | Deliver | 1 | 08-05 04:10 | 0:29 | Advanced | K4.2 | 2 | engine-fast:OK · face-fast:OK | $10.5312 | $0.0072 | 161,661/96,953 |
| 15 | K4 | Deliver | 1 | 08-05 04:40 | 0:23 | Advanced | K4.3 | 2 | engine-fast:OK · face-fast:OK | $8.8544 | $0.0051 | 169,401/80,290 |
| 16 | K4 | Deliver | 1 | 08-05 05:05 | 0:47 | Advanced | K4.4 | 5 | engine-fast:OK · face-fast:OK | $16.7142 | $0.0046 | 201,837/111,577 |

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-05 04:03:14  ▪ gate engine-fast pass [session]  (45.3s)
08-05 04:03:14  ▪ gate face-fast pass [session]  (4.1s)
08-05 04:03:15  • session #10 K3 → Advanced · done K3.2 · 5 commit(s)  (37m08s)
08-05 04:03:15  • session #11 K3 Deliver started (attempt 1/6)
08-05 04:24:58  ▪ gate engine-fast pass [session]  (47.2s)
08-05 04:24:58  ▪ gate face-fast pass [session]  (5.7s)
08-05 04:24:59  • session #11 K3 → Advanced · done K3.3 · 2 commit(s)  (21m43s)
08-05 04:31:33  ▪ gate engine-fast pass [phase]  (0.0s)
08-05 04:31:33  ▪ gate face-fast pass [phase]  (0.0s)
08-05 04:31:33  ▪ gate engine-full FAIL [phase]  (3m23s)
08-05 04:31:33  ▪ gate face-full pass [phase]  (7.2s)
08-05 04:31:33  • session #12 K3 Fix started (attempt 2/6)
08-05 04:42:42  ▪ gate engine-fast pass [session]  (49.7s)
08-05 04:42:42  ▪ gate face-fast pass [session]  (31.8s)
08-05 04:42:42  • session #12 K3 → Progress · 2 commit(s)  (11m08s)
08-05 04:47:06  ▪ gate engine-fast pass [phase]  (0.0s)
08-05 04:47:07  ▪ gate face-fast pass [phase]  (0.0s)
08-05 04:47:07  ▪ gate engine-full pass [phase]  (4m01s)
08-05 04:47:07  ▪ gate face-full pass [phase]  (20.6s)
08-05 04:47:07  ✓ checkpoint K3.3 confirmed
08-05 04:47:07  ▸ stage K3 confirmed  (2h04m51s)
08-05 04:47:07  ▸ stage K4 entered — Token truth - measure it before shrinking it
08-05 04:47:08  • session #13 K4 Deliver started (attempt 1/8)
08-05 05:10:22  ▪ gate engine-fast pass [session]  (41.6s)
08-05 05:10:22  ▪ gate face-fast pass [session]  (3.2s)
08-05 05:10:23  • session #13 K4 → Advanced · done K4.1 · 2 commit(s)  (23m15s)
08-05 05:10:23  • session #14 K4 Deliver started (attempt 1/8)
08-05 05:40:40  ▪ gate engine-fast pass [session]  (42.3s)
08-05 05:40:40  ▪ gate face-fast pass [session]  (29.2s)
08-05 05:40:41  • session #14 K4 → Advanced · done K4.2 · 2 commit(s)  (30m17s)
08-05 05:40:41  • session #15 K4 Deliver started (attempt 1/8)
08-05 06:05:04  ▪ gate engine-fast pass [session]  (47.0s)
08-05 06:05:04  ▪ gate face-fast pass [session]  (3.6s)
08-05 06:05:05  • session #15 K4 → Advanced · done K4.3 · 2 commit(s)  (24m23s)
08-05 06:05:05  • session #16 K4 Deliver started (attempt 1/8)
08-05 06:52:55  ▪ gate engine-fast pass [session]  (41.8s)
08-05 06:52:55  ▪ gate face-fast pass [session]  (4.6s)
08-05 06:52:56  • session #16 K4 → Advanced · done K4.4 · 5 commit(s)  (47m50s)
08-05 06:59:05  ▪ gate engine-fast pass [phase]  (0.0s)
08-05 06:59:05  ▪ gate face-fast pass [phase]  (0.0s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 16 · retries 3 (19 %) · overall Warn
⚠ [context-saturation] session #10: 23,623,416 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #1: 24,653,507 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #7: 24,094,247 context tokens (≥ 20,000,000)
⚠ [gate-oscillation] gate 'engine-full' flipped pass/fail 3x
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/karvan
working tree: M .conductor/REPORT.md, M plans/karvan/CORE-TRACKER.md
vs upstream: up to date
```

### Commits by session

- **s9 (K3 Deliver)** — 5 commit(s):
  - [`e2bc072`](https://github.com/shaahink/conductor/commit/e2bc072) docs(tracker): K3.1 closes - the handoff to K3.2
  - [`aa48c69`](https://github.com/shaahink/conductor/commit/aa48c69) docs(evidence): K3.1 evidence artifact
  - [`dd318d9`](https://github.com/shaahink/conductor/commit/dd318d9) fix(state): the run-start pointer records a derived resolution only, plus K3.1 evidence
  - [`1e29a13`](https://github.com/shaahink/conductor/commit/1e29a13) fix(tests): the last two fixtures open the database the plan resolves, not the one under .conductor
  - [`707992f`](https://github.com/shaahink/conductor/commit/707992f) feat(state): K3.1 - run.db leaves the working tree for a machine-level home
- **s10 (K3 Deliver)** — 5 commit(s):
  - [`ef8324c`](https://github.com/shaahink/conductor/commit/ef8324c) docs(evidence): K3.2 evidence artifact
  - [`884b3f2`](https://github.com/shaahink/conductor/commit/884b3f2) docs(evidence): K3.2 evidence artifact and the handoff to K3.3
  - [`567b22a`](https://github.com/shaahink/conductor/commit/567b22a) feat(face): K3.2 - the run picker lists what this machine remembers
  - [`f5de8a9`](https://github.com/shaahink/conductor/commit/f5de8a9) feat(cli): K3.2 - conductor history lists past runs and opens one read-only
  - [`ec1f158`](https://github.com/shaahink/conductor/commit/ec1f158) feat(history): K3.2 - a run database opens read-only, and the catalogue becomes a list
- **s11 (K3 Deliver)** — 2 commit(s):
  - [`7841cfb`](https://github.com/shaahink/conductor/commit/7841cfb) docs(evidence): K3.3 evidence artifact and the handoff to K4.1
  - [`e45fa11`](https://github.com/shaahink/conductor/commit/e45fa11) feat(history): K3.3 - every run records which engine produced it and under which limits
- **s12 (K3 Fix)** — 2 commit(s):
  - [`21e3f72`](https://github.com/shaahink/conductor/commit/21e3f72) docs(evidence): K3.2 fix evidence and the handoff to K4.1
  - [`398c38a`](https://github.com/shaahink/conductor/commit/398c38a) fix(arch): K3.2's files fit the type ceiling and the history verb tab-completes
- **s13 (K4 Deliver)** — 2 commit(s):
  - [`3fe10fe`](https://github.com/shaahink/conductor/commit/3fe10fe) docs(evidence): K4.1 evidence artifact and the handoff to K4.2
  - [`ea49c8d`](https://github.com/shaahink/conductor/commit/ea49c8d) feat(tokens): K4.1 - the engine measures how full the window ran, not just what it spent
- **s14 (K4 Deliver)** — 2 commit(s):
  - [`1ec436f`](https://github.com/shaahink/conductor/commit/1ec436f) feat(doctor): K4.2 - doctor warns when the cap is under the floor or the nudge under the median closer
  - [`1fcbd0b`](https://github.com/shaahink/conductor/commit/1fcbd0b) feat(budget): K4.2 - the engine measures its own token ceiling and prescribes the next one
- **s15 (K4 Deliver)** — 2 commit(s):
  - [`725f4de`](https://github.com/shaahink/conductor/commit/725f4de) docs(evidence): K4.3 evidence artifact and the handoff to K4.4
  - [`20842e2`](https://github.com/shaahink/conductor/commit/20842e2) feat(money): K4.3 - conductor money prices a run from its own ledger
- **s16 (K4 Deliver)** — 5 commit(s):
  - [`ad6f2de`](https://github.com/shaahink/conductor/commit/ad6f2de) feat(face): K4.4 - the demo computes its token rail instead of freezing one
  - [`60be019`](https://github.com/shaahink/conductor/commit/60be019) docs(evidence): K4.4 evidence artifact and the handoff to K5.1
  - [`2c265d5`](https://github.com/shaahink/conductor/commit/2c265d5) test(budget): K4.4 - the gauge measured on a real run, not on hand-written events
  - [`eafd49b`](https://github.com/shaahink/conductor/commit/eafd49b) feat(face): K4.4 - the Home ceiling row, and a gauge built to be multiplied
  - [`b4ea829`](https://github.com/shaahink/conductor/commit/b4ea829) feat(budget): K4.4 - the token rail goes on the wire beside the money one

## Phase handovers (audit)

- `.conductor/handovers/B0.md`
- `.conductor/handovers/B1.md`
- `.conductor/handovers/B10.md`
- `.conductor/handovers/B11.md`
- `.conductor/handovers/B2.md`
- `.conductor/handovers/B3.md`
- `.conductor/handovers/B4.md`
- `.conductor/handovers/B5.md`
- `.conductor/handovers/B6.md`
- `.conductor/handovers/B7.md`
- `.conductor/handovers/B8.md`
- `.conductor/handovers/B9.md`
- `.conductor/handovers/F0.md`
- `.conductor/handovers/F1.md`
- `.conductor/handovers/F2.md`
- `.conductor/handovers/F4.md`

## Last gate run

engine-fast:cached · face-fast:cached · engine-full:FAIL-retry · face-full:OK

<details><summary>engine-full — exit 1</summary>

```
[conductor] retried once (SC4.1): the first attempt exited 1 after 179s. Below is the SECOND run.
Determining projects to restore...
  All projects are up-to-date for restore.
  Conductor.Planning -> C:\code\conductor\src\Conductor.Planning\bin\Debug\net10.0\Conductor.Planning.dll
  Conductor.Core -> C:\code\conductor\src\Conductor.Core\bin\Debug\net10.0\Conductor.Core.dll
  Conductor -> C:\code\conductor\src\Conductor\bin\Debug\net10.0\conductor.dll
  Conductor.Tests -> C:\code\conductor\tests\Conductor.Tests\bin\Debug\net10.0\Conductor.Tests.dll
Test run for C:\code\conductor\tests\Conductor.Tests\bin\Debug\net10.0\Conductor.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
  Failed Conductor.Tests.K3_3ProvenanceTests.A_database_without_the_provenance_columns_still_reads [35 ms]
  Error Message:
   Microsoft.Data.Sqlite.SqliteException : SQLite Error 1: 'no such column: s.newly_done'.
  Stack Trace:
     at Microsoft.Data.Sqlite.SqliteCommand.PrepareAndEnumerateStatements()+MoveNext()
   at Microsoft.Data.Sqlite.SqliteCommand.GetStatements()+MoveNext()
   at Microsoft.Data.Sqlite.SqliteDataReader.NextResult()
   at Microsoft.Data.Sqlite.SqliteCommand.ExecuteReader(CommandBehavior behavior)
   at Conductor.Core.History.RunArchive.Query(String sql, ValueTuple`2[] parameters) in C:\code\conductor\src\Conductor.Core\History\RunArchive.cs:line 78
   at Conductor.Core.History.RunArchive.Sessions(String runId) in C:\code\conductor\src\Conductor.Core\History\RunArchive.cs:line 151
   at Conductor.Tests.K3_3ProvenanceTests.A_database_without_the_provenance_columns_still_reads() in C:\code\conductor\tests\Conductor.Tests\K3_3ProvenanceTests.cs:line 269
   at System.RuntimeMethodHandle.InvokeMethod(ObjectHandleOnStack target, Void** arguments, ObjectHandleOnStack sig, BOOL isConstructor, ObjectHandleOnStack result)
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)

Failed!  - Failed:     1, Passed:  1937, Skipped:     0, Total:  1938, Duration: 1 m 59 s - Conductor.Tests.dll (net10.0)
```
</details>

## Last session result

> SESSION-RESULT: K4.4 landed complete — live token headroom is on the wire and rendered beside live money. The wire gained a nested `StateDto.tokenHeadroom` block (tokens, cap, nudgeAt, toNudge, toCap, usedRatio, burnPerMinute, minutesToNudge/Cap, live) populated by `ControlPlaneServer.WithTokenHeadroom`; the Face renders it through `widgets.TokenGauge(*api.TokenHeadroomDto)`, a package-level function of one wire value so a lane-aware Face can call it per lane. The real defect closed was not the missing gauge but the wrong one any surface would have built: the soft-break rail counts cache-read (`SessionRunner.Mcp.cs:76`) and the wire's `sessionTokens*` triple does not, so at this project's 98…

## Tracker handoff

```
last: **K4.4 CLAIMED**, evidence `.conductor/evidence/K4/K4.4-live-token-headroom.md`. `StateDto.tokenHeadroom`
  is a nested block (tokens, cap, nudgeAt, toNudge, toCap, usedRatio, burnPerMinute, minutesTo*, live);
  Home renders it via `widgets.TokenGauge(*api.TokenHeadroomDto)` — a package-level func of ONE wire
  value, so a lane-aware Face can call it per lane. 25 tests + 2 goldens, ratchet exit 0 (now 1699).
trap closed: the rail counts **cache-read** (`SessionRunner.Mcp.cs:76`); the wire's `sessionTokens*`
  triple does NOT. At this project's 98% cache share a Face summing the visible three draws a gauge at
  1.25% for a session at 83% — proven on a real harness run: 150k visible vs 10.0M real vs a 12M cap.
  The 0.8 ratio was a literal in TWO places; `SoftBreak.DefaultRatio/EffectiveCap/Threshold` are now
  the only definitions and the rail, doctor and the wire all call them.
next: **K5.1 — the session result contract.** Note it rewrites the SESSION-RESULT template in the same
  commit, and trap 4 (literal brace tokens in a template kill the engine) goes live at K5.1.
red: none. bug #29 (K7.2 blocker) still open: on a COPY of this run.db the WRITE path dies on
  `duplicate column name: soft_break`; the read-only `RunArchive` path opens the same file fine.
gotcha for any harness test you add: a session ceiling lengthens the prompt and **cmd.exe truncates a
  command line at 8191 chars** — a cmd fake-agent fails as `AgentError` the instant a ceiling exists.
  Use PowerShell (K1.2's rig does). And the store drains events async, so `live` on `/state` right
  after `RunAsync` is load-dependent — assert numbers, not liveness.
```
