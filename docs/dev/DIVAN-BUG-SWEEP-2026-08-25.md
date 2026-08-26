# The Divan bug sweep — every known defect, in one place, before the era builds on top of them

*2026-08-25. The strand doc for stage DV2 of `plans/divan/core.plan.json`. The era that follows
(the inbox, the courier, the cloud verbs) lands on the prompt-battery seam, the Telegram service and
the run-state store — the three places most of these defects live. Sweeping first is not hygiene for
its own sake: it is clearing the ground the era pours concrete on.*

*Three ledgers feed this sweep. Two are in the repo already; the third is transcribed here because it
existed only in the operator's field notes and would otherwise be rediscovered at full price.*

---

## Ledger 1 — the `bugs` table: 28 open rows in the LIVE store (measured 2026-08-25)

**Resolve the store the way doctor does, never by the repo-local path.** The live run.db is in the
state home via `.conductor/state-pointer.json` (doctor's `state` line names it); the file at
`.conductor/run.db` is the pre-import COPY and answers every query with stale data. This document's
first draft made exactly that mistake and reported 11 open bugs; the live store holds **28**, ids
15–61 with gaps — and the gaps are themselves a filed bug (#46: the state-home split lost karvan's
#16/#20/#24/#27/#31/#35, which are still marked open ONLY in the imported copy; triage recovers them
from there).

Open in the live store today: **15, 18, 19, 21, 23, 37–43, 45–49, 51–61.** Surfaced to every
session by the open-bugs battery; close each with the `conductor bug` verb when its fix lands with a
regression test. The clusters the era cares about:

- **Prompt/argv class:** #15 (a composed prompt over ~8191 chars silently stops a cmd.exe agent),
  #21 (no warning when packs cross the argv ceiling), #55 (doctor's argv lint under-measures the
  real spawn by 350–500 chars — it misses the battery).
- **Channels:** #38 (the getUpdates 409 conflict loop — the field-observed defect below is THIS bug;
  fix it, do not re-file it).
- **Store truth:** #45 (any verb from a newer build silently migrates the live run.db and locks the
  running engine out — trap 18 exists because this happened), #46 (bugs lost in the state-home
  split, above), #61 (`CONDUCTOR_RUN_DB` does not redirect the measuring verbs), #39 (an interrupted
  session leaves a non-terminal 'running' row at $0.00 no verb can clear), #37 (`history --json`
  misses non-terminal baton rows), #42 (catalogue repair cannot collapse a live-store duplicate),
  #48 (`face` with no live run silently attaches to another repo's run).
- **Verdict honesty:** #40 (satellite commits by ANYONE count as the session's own work),
  #58 (`FailureCircuitBreaker.ParseFailingGates` matches glyphs the summary never emits),
  #52 (the digest counts a claim attempt that FAILED), #19 (digest claims need MCP task_update).
- **Build flakes:** #54/#57 (MSBuild node reuse serves stale analyzer config — red flaps),
  #59 (`dotnet run --project src/Conductor` inside a bg child fails MA00xx), #49 (fold-test flake
  under full suite), #23 (CI Windows battery flake).
- **Ratchets and payesh:** #60 (analyzer-debt ratchet RED on the edge branch: pragma-src 33 against
  a bar of 31 — the edge handoff says the bar may NOT simply be moved), #41 (payesh anonymity fails
  closed on the generic word 'website'), #47 (a private repo named an ordinary noun makes the check
  unfalsifiable).
- **KS7 leftovers:** #51 (a restricted permission posture silently breaks the run's own claim path),
  #53 (cache-write TTL split dropped from the cost model), #43 (4-digit import ids pass the
  provider, fail elsewhere).
- **Face lane:** #18 (bottom bar hard-clips contextual help).

One number stays unaccounted: older notes reference a "bug 44" (43 pragmas vs a ceiling of 38);
no open row carries it — triage confirms it fixed, superseded by #60, or refiles it.

## Ledger 2 — `.conductor/followups.md`: rows still marked OPEN

262 rows, 11 OPEN as of today. The ones a session can own:

> **Corrected at DV7.1, 2026-08-26.** Two things in this section did not survive re-measurement, and
> both are recorded in `.conductor/followups.md`'s DV7.1 closure ledger. **262 is exact** — it is
> every table row in that file, of which 91 are followup rows for 55 distinct ids. **11 is not
> reproducible** by any counting method (the file yields 7 / 10 / 4 / 23 depending on the pattern) and
> no commit has touched the file since, so the discrepancy is in the count, not the ledger. And the
> **FU-F1-06** bullet below is wrong, struck through rather than deleted so the correction is legible.

- ~~**FU-F1-06** — `UpdateRunStatus` exists nowhere in `src/`, so a run that ends `NeedsHuman`,
  `Paused` or `AwaitingOwner` still reads `status='running'` in the store. Re-verified with the scan
  widened to all of `src/`. The "immortal running" record, and the oldest standing row a session can
  actually clear.~~ **CLOSED at KS0.2 (`15627b9`) six days before this document was written**, and
  `.conductor/followups.md:527` says so. Measured at DV7.1: `UpdateRunStatus` is declared at
  `Store/IRunStore.cs:38`, implemented at `Store/SqliteRunStore.Sessions.cs:74`, called from
  `Orchestration/RunContext.cs:391`, and pinned by `KS0_2RunRecordTests` and
  `KS0_2NoRunsUpdateOutsideTheStoreTests`. No session acted on the re-opening, so nothing was lost —
  but a triage document that contradicts a closure ledger is how a closed row gets worked twice.
- The face `tokens cap` row quotes the plan **file**, not the run's effective ceiling
  (`face-go/internal/tui/tab_home.go:662-664`; the cost row at `:609` has the same defect).
- `approve` lost `CtlCommand`'s `--yes` and `--force` (`ApproveCommand.Settings` declares only
  `--amount` and `--tokens`).
- The B2 async-run-loop row and the recovery-lane re-home row: triage decides fix-here or
  defer-with-owner; neither is silently droppable.

**Not a session's to take, ever:** **FU-B11-3** (real-credential cTrader path) is owner-gated and
states so in its own row. Triage records it as deferred-to-owner and moves on.

## Ledger 3 — engine defects from the field (operator's log, transcribed 2026-08-25)

These were found while driving real runs (this repo and two others on this machine) and live in no
repo ledger until this sweep files them. Each entry carries what was measured; reproduce before
fixing, cite `file:line` in the evidence.

1. **The knowledge ledger silently starves the open-bugs battery.** `PromptBuilder.BatterySection`
   adds the ledger then open bugs "FIRST so the byte cap never truncates them away"
   (`PromptBuilder.cs:296-311`) — but `BatteryGroup.Render` truncates the CONCATENATION at
   `batteries.maxBytes` (default 2048, `PromptBattery.cs:53-61`), so a grown ledger deletes the bug
   list whole. Measured on a 45-session run elsewhere on this machine: `### open bugs` present in
   prompts 026/032, gone from 038 onward, 11 bugs open the whole time. The tell is a battery block
   ending in a bare ellipsis. Fix direction: per-battery budget shares (or per-battery truncation),
   and the rendered text says when a battery was dropped. This plan mitigates via
   `batteries.ledgerMaxEntries: 3` + `maxBytes: 6144`; the engine fix is this sweep's to land.
2. **`TelegramService` has no `getUpdates` 409/Conflict handling** (grepped: zero hits). Two engines
   polling one token fight and steal each other's updates with no log line naming the cause. At
   minimum: detect 409, log the other consumer's existence loudly, back off. **Already filed as
   bug #38** — fix that row, do not file a twin.
3. **False "will deliver nothing" startup warning on a chats-only plan.**
   `TelegramService.Lifecycle.cs:53` counts `_cfg.AllowedChatIds.Count` for the started log line
   while every other surface uses the resolved `TelegramConfig.ChatCount` (whose own doc comment
   names exactly this misreport as its reason to exist). `/telegram/status` disagrees with the log
   line; delivery actually works.
4. **`POST /telegram/test` indexes `_cfg.AllowedChatIds[0]`** (`TelegramService.cs:231`) — on a
   chats-only plan that is an empty list.
5. **`report push failed:` logs with an EMPTY reason** — the colon is followed by nothing, so a
   repeated report-push failure is undiagnosable from the log. Capture the reason.
6. **The stage-boundary squash can rewind the branch, silently.** The defensive `rebase --abort` of
   a `.git/rebase-merge` left by an earlier KILLED session aborts to the *stale* rebase's original
   head — measured 2026-08-14 (run d6fd22ba): HEAD moved back 28 commits, the engine read the
   truncated history as "nothing to squash — among 2 commit(s)" and advanced. Fix direction: before
   aborting, detect that the rebase state is stale (its head is not an ancestor-of/equal-to current
   HEAD) and refuse to touch it — park instead; after any abort, assert the pre-squash tip is still
   an ancestor of HEAD.
7. **The budget counter restarts at zero on every engine process start.** After a `--once` exit,
   `run_state` held cost/tokens as literal 0 while `overheadCostUsd` carried sub-cents ("restored
   budget: $0.00 …" printed only because the sub-cent field trips the non-zero gate). The persist
   calls are in the right order at `RunLoop.cs:407-417`; what is missing is the store save on that
   exit path. Consequence: `maxRunCostUsd` is a per-PROCESS cap, not per-window — unbounded across
   restarts.
8. **A 429/rate-limit storm is classified `AgentError` and burns a stage's attempt budget in
   minutes.** Measured 2026-08-13: `session #N exited (code 1, 0m, $0.00)` every ~19 seconds,
   attempts 2→8 consumed, the advisor also 429'd ("advisor unavailable — deterministic default"),
   turning an account limit into `NEEDS HUMAN`. Fix direction: classify a 429-with-reset-time as
   backoff (the engine already has `backoffMinutes`), never as a distinct failure per retry.
9. Minor, file-if-cheap: the auth preflight prints "inconclusive" noise by design; `journey` cannot
   show gate-to-stage scoping, so scoped gates are unverifiable before a stage closes.

## Triage rules (DV2.1 writes the ledger; DV2.2–2.4 burn it down)

- Every row above gets exactly one disposition: **fix-this-stage** (with the regression test named)
  or **deferred** (with a named owner and a reason). No row vanishes.
- The triage output is a committed ledger table in the evidence directory, one row per defect,
  cross-referencing the three sources — including recovering the #46-lost karvan rows from the
  imported copy and settling the dangling "bug 44" reference.
- Fixing order inside the stage is the triage's call, but the prompt-composition cluster (1 above,
  #15, #21) outranks the rest: the era's own batteries land on that seam next stage.
- A fix without a test that would have caught it is not a fix; close the bug with the `conductor
  bug` verb in the same commit.
