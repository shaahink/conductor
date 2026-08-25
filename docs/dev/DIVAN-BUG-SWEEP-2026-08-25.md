# The Divan bug sweep — every known defect, in one place, before the era builds on top of them

*2026-08-25. The strand doc for stage DV2 of `plans/divan/core.plan.json`. The era that follows
(the inbox, the courier, the cloud verbs) lands on the prompt-battery seam, the Telegram service and
the run-state store — the three places most of these defects live. Sweeping first is not hygiene for
its own sake: it is clearing the ground the era pours concrete on.*

*Three ledgers feed this sweep. Two are in the repo already; the third is transcribed here because it
existed only in the operator's field notes and would otherwise be rediscovered at full price.*

---

## Ledger 1 — `run.db` `bugs` table: 11 open rows (measured 2026-08-25, ids 1–35, 24 fixed / 11 open)

Surfaced to every session by the open-bugs battery and `conductor bug list`. Close each with the
`conductor bug` verb when its fix lands with a regression test. The open ids today: **15, 16, 18,
19, 20, 21, 23, 24, 27, 31, 35.** Highlights the era cares about:

- **#15** — a composed prompt over ~8191 chars silently stops a cmd.exe-based agent (prompt-size class).
- **#16** — the gate battery can try to rebuild a conductor.exe that is currently running (the
  remainder of FU-OWNER-9; a live proof exists that the debug-build image is exactly what the next
  `dotnet build Conductor.slnx` overwrites).
- **#20** — `run` resolves `CONDUCTOR_PLAN` over the CWD, so a scratch rig launched from inside a
  session can aim at the wrong plan.
- **#21** — nothing warns when packs push the composed prompt past the argv ceiling.
- **#24** — `AgentConfig.Merge` silently drops `Env` (`src/Conductor/Models/AgentConfig.cs:36-48`).
- **#27** — a brand-new run.db logs `FOREIGN KEY constraint failed` on the first `run_state` write.
- **#23** — CI Windows battery flake in `SF0_3PidsAndBackgroundWorkTests.McpBgStatus_…`.
- **#18, #31** — face lane (bottom-bar clipping; textarea key dispatch).
- **#35** — `tools/w3/window-close.ps1` and `tools/sf1/sf1-2-live-proof.ps1` read run.db from the
  wrong place.

**A numbering discrepancy to settle at triage:** the edge tracker's handoff names "bug 60"
(analyzer-debt, pragma-src 33 vs bar 31) and "bug 41" (payesh `npm run anonymity` red on the word
"website"), and older notes name "bug 44/45". The bugs table's ids stop at 35 — those numbers exist
in no table row today. Triage finds the real rows or files them fresh; neither defect may be lost to
a dangling number. (Bug 45's *lesson* — migration-version skew between a fresh build and the
installed engine bricking the live store — is real regardless; it is trap 18 in the plan.)

## Ledger 2 — `.conductor/followups.md`: rows still marked OPEN

262 rows, 11 OPEN as of today. The ones a session can own:

- **FU-F1-06** — `UpdateRunStatus` exists nowhere in `src/`, so a run that ends `NeedsHuman`,
  `Paused` or `AwaitingOwner` still reads `status='running'` in the store. Re-verified with the scan
  widened to all of `src/`. The "immortal running" record, and the oldest standing row a session can
  actually clear.
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
   minimum: detect 409, log the other consumer's existence loudly, back off.
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
  cross-referencing the three sources — including resolving the 41/44/60 numbering against the real
  table.
- Fixing order inside the stage is the triage's call, but the prompt-composition cluster (1 above,
  #15, #21) outranks the rest: the era's own batteries land on that seam next stage.
- A fix without a test that would have caught it is not a fix; close the bug with the `conductor
  bug` verb in the same commit.
