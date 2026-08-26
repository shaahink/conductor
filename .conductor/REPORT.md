# Conductor — Divan - the chancellery: inbox, courier, and the record that gets out run report

_Updated 2026-08-26 10:46 UTC · branch `feat/divan` · HEAD `115020b`_

**Status:** Idle
**Stage:** DV4 — The courier - one bot, always awake, outliving the run · attempts used 0
**Checkpoints:** 14/23 done · **Sessions run:** 14 · **Cost:** $196.7014 (agent $196.6050 + gates $0.0963) · **Tokens:** 2,856,287 in / 1,092,751 out
**Confirmed phases:** DV1, DV2, DV3, DV4

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| DV1 | The channel that says so - health made loud, the queue that reaches you | ██████████ 2/2 | confirmed ✓ |
| DV2 | The sweep - every known defect triaged, the clusters burned down | ██████████ 4/4 | confirmed ✓ |
| DV3 | The inbox - feedback that arrives when you have it, and survives the run | ██████████ 4/4 | confirmed ✓ |
| DV4 | The courier - one bot, always awake, outliving the run | ██████████ 4/4 | confirmed ✓ |
| DV5 | The cloud, the safe shapes - an owner verb, a flagged experiment | ░░░░░░░░░░ 0/2 | todo |
| DV6 | The record that gets out - bugs that outlive the board, columns, the page | ░░░░░░░░░░ 0/4 | todo |
| DV7 | Ship Divan - close the era | ░░░░░░░░░░ 0/3 | todo |

<details> ✅<summary>DV1 — The channel that says so - health made loud, the queue that reaches you (2/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| DV1.1 | Channel health is loud: every configured outbound channel carries live state landing in the REPORT.md header, /status and the owner queue within one boundary of a failure; configured-but-dead refused at preflight or parked loudly, never logged-and-ignored; the seeded proof is the edge run's own github-block-without-token failure, pinned by test in all three surfaces | ✅ DONE | [`b9ea19f`](https://github.com/shaahink/conductor/commit/b9ea19f) |
| DV1.2 | Owner queue pushed to admin chats on change: one item per push with the exact clearing command, KS11.3 grammar, golden-pinned; no-change-no-push proven so the channel is signal, not noise | ✅ DONE | [`b9ea19f`](https://github.com/shaahink/conductor/commit/b9ea19f) |

</details>

<details> ✅<summary>DV2 — The sweep - every known defect triaged, the clusters burned down (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| DV2.1 | Triage ledger: every row of DIVAN-BUG-SWEEP's three ledgers dispositioned fix-this-stage or deferred-with-named-owner, committed as evidence; the number-46-lost karvan rows recovered from the imported copy and the dangling bug-44 reference settled; no row dropped | ✅ DONE | [`c86dcec`](https://github.com/shaahink/conductor/commit/c86dcec) |
| DV2.2 | Cluster A, prompt composition: per-battery budget shares so the knowledge ledger can no longer starve the open-bugs battery, with a rendered notice when a battery is dropped and a regression test on a grown ledger; bug 15 (prompt-size silent stop) and bug 21 (argv-ceiling warning) closed with tests | ✅ DONE | [`c86dcec`](https://github.com/shaahink/conductor/commit/c86dcec) |
| DV2.3 | Cluster B, channels: getUpdates 409 handling that names the other consumer and backs off; the false will-deliver-nothing startup line reads the resolved ChatCount; the telegram test endpoint survives a chats-only plan; report-push failures log their reason - all proven at the stub seam with scratch tokens | ✅ DONE | [`2b37a01`](https://github.com/shaahink/conductor/commit/2b37a01) |
| DV2.4 | Cluster C, state and verdict, and the close: budget counters persisted across engine restarts (the per-process-cap defect), the stage-boundary squash refuses to abort a STALE rebase and asserts ancestry after any abort, bug 27 first-write FK, 429-with-reset-time classified as backoff not AgentError, FU-F1-06 UpdateRunStatus lands; every fix-here row closed via the conductor bug verb, every remaining open row carries a named owner in the ledger | ✅ DONE | [`8c23aaf`](https://github.com/shaahink/conductor/commit/8c23aaf) |

</details>

<details> ✅<summary>DV3 — The inbox - feedback that arrives when you have it, and survives the run (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| DV3.1 | Inbound message kinds: voice, audio, document, photo, caption, reply_to_message, message_thread_id on the DTO; getFile download; the 20 MB bot-API cap refused by name to the sender, never dropped; a stub-wire test drives each kind end to end | ✅ DONE | [`2bebfbe`](https://github.com/shaahink/conductor/commit/2bebfbe) |
| DV3.2 | The per-project inbox: durable store under .conductor/inbox (never committed - no gitignore allowlist entry, this repo is public), media beside transcript, atomic writes, append-only index deduped by update_id, read cursor with seen-by-session marks; InboxBattery on the IPromptBattery seam, fenced and framed, with an architecture test proving the fencing is always present; a note filed with no run live is read by the next session of the next run, proven in the rig | ✅ DONE | [`2bebfbe`](https://github.com/shaahink/conductor/commit/2bebfbe) |
| DV3.3 | Transcription: configured local command (faster-whisper on this machine's GPU), per-segment confidence marked in the stored note, unset command files the note untranscribed with audio kept and the reply saying so; conductor inbox prune is the only deletion path; a real .ogg transcribes in the rig | ✅ DONE | [`4b1f04a`](https://github.com/shaahink/conductor/commit/4b1f04a) |
| DV3.4 | Routing: a voice note sent as a reply to a checkpoint push files against that push's project with no command typed; sticky /project selection; message_thread_id topics in supergroups; unknown slug refused by name; unroutable notes parked in a machine-level dead-letter directory, never dropped | ✅ DONE | [`4b1f04a`](https://github.com/shaahink/conductor/commit/4b1f04a) |

</details>

<details> ✅<summary>DV4 — The courier - one bot, always awake, outliving the run (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| DV4.1 | The daemon: conductor courier owns the token, polls always, routes to per-project inboxes via an explicit allowlist; durable poll offset in the state home plus update_id dedup - kill the courier between receive and acknowledge, restart, and the note files exactly once; the 24-hour Telegram retention limit stated in docs | ✅ DONE | [`b0cc449`](https://github.com/shaahink/conductor/commit/b0cc449) |
| DV4.2 | Lifecycle: courier install / uninstall / restart / status as a per-user Scheduled Task with restart-on-failure; tools/install.ps1 stops and restarts a running courier; version handshake at the loopback hello refuses a stale courier by name, naming the restart command; live proof registers a scratch-named task and unregisters it - the real install is the owner's at DV7.3 | ✅ DONE | [`962d6bc`](https://github.com/shaahink/conductor/commit/962d6bc) |
| DV4.3 | The seam: loopback-only listener, per-install shared secret file-permission-protected, own named port; CourierChannel on IMessageChannel so live runs push through the daemon; when a courier is configured, in-run polling refuses to start and names it; courier-less machines byte-identical by golden replay; killing the daemon makes a live run's REPORT.md, /status and owner queue all say so within one boundary | ✅ DONE | [`5544cff`](https://github.com/shaahink/conductor/commit/5544cff) |
| DV4.4 | Promotion: note to followups.md row to Tier-B lane by an explicit button on the acknowledgement; auto-inject from an inbox note refused by design with a negative test proving no code path does it; filing stays admin-only | ✅ DONE | [`273674c`](https://github.com/shaahink/conductor/commit/273674c) |

</details>

<details><summary>DV5 — The cloud, the safe shapes - an owner verb, a flagged experiment (0/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| DV5.1 | The /cloud admin verb: flags verified against the installed CLI first; preflight requires clean tree and pushed, current branch, refusing by name in the chat with the exact git state; session id and URL returned to the chat and recorded in the event log as an owner action; follow-ups ride claude -p --cloud | ⬜ TODO | - |
| DV5.2 | The cloud lane behind a flag, default off: only work needing no conductor tools and no verdict; branch consumed, every gate re-run locally, the referee never moves; cost recorded and reported as unknown, pinned by a test that no code path prints zero for a cloud lane; droppable without losing DV5.1 | ⬜ TODO | - |

</details>

<details><summary>DV6 — The record that gets out - bugs that outlive the board, columns, the page (0/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| DV6.1 | Bugs and followups as a long-lived issue class: conductor:bug / conductor:followup labels, opened when filed, closed by the closing commit, surviving the run; the daily digest gains the ledger line, golden-pinned | ⬜ TODO | - |
| DV6.2 | The columns: Projects v2 mutation path landed - live if the token now carries project scope, else behind the existing refusal with stubbed proof and a filed finding naming gh auth refresh -s project as the owner's one-command unblock; the KS9.3 refusal moves either way | ⬜ TODO | - |
| DV6.3 | CUT-FIRST - board snapshot as one self-contained HTML file rendered from Http/Contracts at each boundary, pushed as a Telegram document; the page states its own staleness; no inbound anything | ⬜ TODO | - |
| DV6.4 | CUT-FIRST - SARIF export for file/line bugs uploaded to code scanning; docs state the public-free / private-needs-Advanced-Security split | ⬜ TODO | - |

</details>

<details><summary>DV7 — Ship Divan - close the era (0/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| DV7.1 | The internal record: ARCHITECTURE.md and docs/dev reconciled for everything Divan changed, the courier's lifecycle section and ADR included; closure ledger reconciled against DV2's triage; conductor budget re-measured through the fresh build against a backup copy and written into TOKEN-BUDGET-TUNING for the next era | ⬜ TODO | - |
| DV7.2 | The published surface: README, cli.md, operating.md, plan-config.md, quickstart/troubleshooting where touched, docs index; CHANGELOG Unreleased written as the release body (it may still carry edge's entries - the split call is the owner's); docs-match-reality tests extended and proven red on a seeded stale doc; payesh harvest re-run on a branch with a PR, never pushing main | ⬜ TODO | - |
| DV7.3 | OWNER-ONLY ship: merge (stacked on feat/karvansara-edge - KS12.3 lands first or together), tag, reinstall, real courier install, github sync backfill of this run, payesh PR merge, tracker and findings doc move to docs/history; a session pre-flights and parks with the runbook, it does not perform them | ⬜ TODO | - |

</details>

## Sessions

| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Overhead | Tokens |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | DV1 | Deliver | 1 | 08-25 18:21 | 0:03 | Interrupted |  | 0 |  |  |  |  |
| 2 | DV1 | Resume | 1r1 | 08-25 18:25 | 0:49 | Advanced | DV1.1 DV1.2 | 6 | engine-fast:OK · face-fast:OK | $16.0285 | $0.0140 | 200,295/93,184 |
| 3 | DV2 | Deliver | 1 | 08-25 19:23 | 0:54 | Advanced | DV2.1 DV2.2 | 5 | engine-fast:OK · face-fast:OK | $23.5420 | $0.0093 | 274,301/144,947 |
| 4 | DV2 | Deliver | 1 | 08-25 20:20 | 0:25 | Advanced | DV2.3 | 3 | engine-fast:OK · face-fast:OK | $7.3377 | $0.0065 | 127,564/54,350 |
| 5 | DV2 | Deliver | 1 | 08-25 20:46 | 0:50 | Advanced | DV2.4 | 7 | engine-fast:OK · face-fast:OK | $21.9036 | $0.0093 | 245,905/124,874 |
| 6 | DV3 | Deliver | 1 | 08-25 21:48 | 0:43 | Advanced | DV3.1 DV3.2 | 5 | engine-fast:OK · face-fast:OK | $23.2906 | $0.0067 | 301,366/155,534 |
| 7 | DV3 | Deliver | 1 | 08-25 22:32 | 0:48 | Advanced | DV3.3 DV3.4 | 3 | engine-fast:OK · face-fast:OK | $24.3567 | $0.0104 | 311,508/159,559 |
| 8 | DV3 | Fix | 2 | 08-25 23:33 | 0:19 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $7.5553 | $0.0068 | 123,278/43,543 |
| 9 | DV4 | Deliver | 1 | 08-25 23:58 | 0:36 | RolledOver | DV4.1 | 3 |  | $24.3350 |  | 450,239/1,895 |
| 10 | DV4 | Deliver | 1 | 08-26 00:34 | 8:09 | TimedOut |  | 0 |  |  |  | 76,445/179 |
| 11 | DV4 | Resume | 2r1 | 08-26 08:44 | 0:17 | Advanced | DV4.2 | 3 | engine-fast:OK · face-fast:OK | $7.3609 | $0.0096 | 176,234/56,432 |
| 12 | DV4 | Deliver | 1 | 08-26 09:03 | 0:37 | Advanced | DV4.3 | 4 | engine-fast:OK · face-fast:OK | $19.8516 | $0.0083 | 271,404/139,580 |
| 13 | DV4 | Deliver | 1 | 08-26 09:42 | 0:34 | Advanced | DV4.4 | 1 | engine-fast:OK · face-fast:OK | $16.5702 | $0.0079 | 205,156/84,103 |
| 14 | DV4 | Fix | 2 | 08-26 10:28 | 0:11 | Progress |  | 1 | engine-fast:OK · face-fast:OK | $4.4730 | $0.0076 | 92,592/34,571 |

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 13 | 275.7M | 98.6% | $196.70 | 14 | 19.7M | $14.05 |
| stage DV1 | 1 | 23.7M | 98.8% | $16.04 | 2 | 11.8M | $8.02 |
| stage DV2 | 3 | 77.3M | 98.7% | $52.81 | 4 | 19.3M | $13.20 |
| stage DV3 | 3 | 78.8M | 98.6% | $55.23 | 4 | 19.7M | $13.81 |
| stage DV4 | 6 | 95.9M | 98.4% | $72.62 | 4 | 24M | $18.16 |
| 2026-08 | 13 | 275.7M | 98.6% | $196.70 | 14 | 19.7M | $14.05 |

_Where the money goes: agent $196.61 (100%) · gate $0.10 (0%) · blended $0.71/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-26 00:58:18  ▪ gate engine-full pass [phase]  (4m53s)
08-26 00:58:18  ▪ gate face-full pass [phase]  (1.4s)
08-26 00:58:18  ✓ checkpoint DV3.1 confirmed
08-26 00:58:18  ✓ checkpoint DV3.2 confirmed
08-26 00:58:18  ✓ checkpoint DV3.3 confirmed
08-26 00:58:18  ✓ checkpoint DV3.4 confirmed
08-26 00:58:18  ▸ stage DV3 confirmed  (2h09m47s)
08-26 00:58:19  ▸ stage DV4 entered — The courier - one bot, always awake, outliving the run
08-26 00:58:19  • session #9 DV4 Deliver started (attempt 1/8)
08-26 01:34:38  • session #9 DV4 → RolledOver · done DV4.1 · 3 commit(s)  (36m18s)
08-26 01:34:38  • session #10 DV4 Deliver started (attempt 1/8)
08-26 09:44:28  • session #10 DV4 → TimedOut  (8h09m49s)
08-26 09:44:39  • session #11 DV4 Resume started (attempt 2/8)
08-26 10:03:24  ▪ gate engine-fast pass [session]  (1m03s)
08-26 10:03:24  ▪ gate face-fast pass [session]  (31.9s)
08-26 10:03:24  • session #11 DV4 → Advanced · done DV4.2 · 3 commit(s)  (18m44s)
08-26 10:03:25  • session #12 DV4 Deliver started (attempt 1/8)
08-26 10:42:01  ▪ gate engine-fast pass [session]  (1m17s)
08-26 10:42:01  ▪ gate face-fast pass [session]  (5.3s)
08-26 10:42:02  • session #12 DV4 → Advanced · done DV4.3 · 4 commit(s)  (38m37s)
08-26 10:42:03  • session #13 DV4 Deliver started (attempt 1/8)
08-26 11:18:17  ▪ gate engine-fast pass [session]  (1m05s)
08-26 11:18:17  ▪ gate face-fast pass [session]  (14.1s)
08-26 11:18:18  • session #13 DV4 → Advanced · done DV4.4 · 1 commit(s)  (36m14s)
08-26 11:28:45  ▪ gate engine-fast pass [phase]  (0.0s)
08-26 11:28:45  ▪ gate face-fast pass [phase]  (0.0s)
08-26 11:28:45  ▪ gate engine-full FAIL [phase]  (5m34s)
08-26 11:28:46  ▪ gate face-full pass [phase]  (3.0s)
08-26 11:28:46  • session #14 DV4 Fix started (attempt 2/8)
08-26 11:41:34  ▪ gate engine-fast pass [session]  (1m12s)
08-26 11:41:34  ▪ gate face-fast pass [session]  (3.3s)
08-26 11:41:34  • session #14 DV4 → Progress · 1 commit(s)  (12m48s)
08-26 11:46:54  ▪ gate engine-fast pass [phase]  (0.0s)
08-26 11:46:54  ▪ gate face-fast pass [phase]  (0.0s)
08-26 11:46:54  ▪ gate engine-full pass [phase]  (5m15s)
08-26 11:46:54  ▪ gate face-full pass [phase]  (1.7s)
08-26 11:46:54  ✓ checkpoint DV4.1 confirmed
08-26 11:46:54  ✓ checkpoint DV4.2 confirmed
08-26 11:46:54  ✓ checkpoint DV4.3 confirmed
08-26 11:46:54  ✓ checkpoint DV4.4 confirmed
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 14 · retries 3 (21 %) · overall Warn
⚠ [context-saturation] session #12: 27,281,149 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #2: 23,394,515 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #3: 34,336,668 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #5: 32,631,442 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 32,762,695 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #7: 34,491,022 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #9: 34,833,588 context tokens (≥ 20,000,000)
⚠ [gate-oscillation] gate 'engine-full' flipped pass/fail 4x
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/divan
working tree: M .conductor/REPORT.md, M plans/divan/TRACKER.md
vs upstream: up to date
```

### Commits by session

- **s6 (DV3 Deliver)** — 5 commit(s):
  - [`22cb53c`](https://github.com/shaahink/conductor/commit/22cb53c) docs(divan): handoff - DV3.3 opened and returned to TODO at the wrap-up nudge
  - [`60716a5`](https://github.com/shaahink/conductor/commit/60716a5) docs(divan): handoff after DV3.1 and DV3.2 - the line-boundary trim that un-quotes a note
  - [`e8bcdc2`](https://github.com/shaahink/conductor/commit/e8bcdc2) feat(DV3.2): the per-project inbox - a note that survives the run that received it
  - [`96ae444`](https://github.com/shaahink/conductor/commit/96ae444) docs(divan): the handoff for DV3.2 - the seam rule that reads string literals
  - [`2bebfbe`](https://github.com/shaahink/conductor/commit/2bebfbe) feat(DV3.1): the inbound message kinds - a voice note stops being invisible
- **s7 (DV3 Deliver)** — 3 commit(s):
  - [`5dd2df3`](https://github.com/shaahink/conductor/commit/5dd2df3) feat(DV3.4): routing - a reply to a push files against that push's project
  - [`bf0cbf0`](https://github.com/shaahink/conductor/commit/bf0cbf0) feat(DV3.3): the live proof, the marks in the prompt, and the handoff
  - [`4b1f04a`](https://github.com/shaahink/conductor/commit/4b1f04a) feat(DV3.3): transcription - the words, with the doubt kept
- **s8 (DV3 Fix)** — 2 commit(s):
  - [`516c26c`](https://github.com/shaahink/conductor/commit/516c26c) fix(DV3): declare the new adapter partial, and the evidence for a green battery
  - [`78663f7`](https://github.com/shaahink/conductor/commit/78663f7) fix(DV3): the index that wrote over itself, and four registries the inbox never reached
- **s9 (DV4 Deliver)** — 3 commit(s):
  - [`d43a74a`](https://github.com/shaahink/conductor/commit/d43a74a) fix(DV4.1): the orphan the live proof found, and an offset that read itself wrong
  - [`0986ddf`](https://github.com/shaahink/conductor/commit/0986ddf) feat(DV4.1): the courier - one bot, always awake, outliving the run
  - [`b0cc449`](https://github.com/shaahink/conductor/commit/b0cc449) refactor(DV4.1): one getFile, one getUpdates - the courier gets a wire to share
- **s11 (DV4 Resume)** — 3 commit(s):
  - [`e6d0d1c`](https://github.com/shaahink/conductor/commit/e6d0d1c) docs(divan): the handoff for DV4.2 - the presence record is DV4.3's hello, written down
  - [`5c78d3f`](https://github.com/shaahink/conductor/commit/5c78d3f) proof(DV4.2): the scheduler accepts it, and the guard defect the live run found
  - [`962d6bc`](https://github.com/shaahink/conductor/commit/962d6bc) feat(DV4.2): the courier gets a lifecycle - a logon task, a presence record, and a version it states
- **s12 (DV4 Deliver)** — 4 commit(s):
  - [`a179857`](https://github.com/shaahink/conductor/commit/a179857) docs(divan): the handoff for DV4.3 - the seam is open, and a second courier is refused
  - [`54a9444`](https://github.com/shaahink/conductor/commit/54a9444) proof(DV4.3): the seam holds against a real daemon, and the defect the live run found
  - [`9d0e0e2`](https://github.com/shaahink/conductor/commit/9d0e0e2) feat(DV4.3): the token handover - a run stops polling for the courier and pushes through it
  - [`5544cff`](https://github.com/shaahink/conductor/commit/5544cff) feat(DV4.3): the loopback seam - a named port, a per-install secret, and the hello served
- **s13 (DV4 Deliver)** — 1 commit(s):
  - [`273674c`](https://github.com/shaahink/conductor/commit/273674c) feat(DV4.4): promotion - one tap turns a note into a followups row and a Tier-B lane
- **s14 (DV4 Fix)** — 1 commit(s):
  - [`115020b`](https://github.com/shaahink/conductor/commit/115020b) fix(DV4): the two architecture ratchets go green by moving code, not by moving a bar

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

engine-fast:cached · face-fast:cached · engine-full:OK · face-full:OK

## Last session result

> **both engine-full architecture ratchets fixed by moving code, suite 3339/3339 green**
> - Type ceiling: CourierJson and CourierConflictException split into their own files, no baseline entry added
> - Bug #77 closed: AtomicFile extracted from InboxStore, CourierDaemon.Discard deleted so prune is the only inbox deleter again
> - Nothing weakened: tests/ untouched, architecture-baseline.json byte-identical, same 3339 total as the red run
>
> artefacts: 115020b, src/Conductor.Core/AtomicFile.cs, src/Conductor.Core/Courier/CourierJson.cs, src/Conductor.Core/Courier/CourierConflictException.cs, src/Conductor.Core/Courier/CourierDaemon.cs, src/Conductor.Core/Inbox/InboxStore.cs, plans/divan/TRACKER.md
>
> evidence: .conductor/evidence/DV4/dv4-repair-s014-architecture-ratchets.md
>
> gaps: bug #76 still open — the courier delivers an evidence artifact as text naming the path, it uploads no file

## Tracker handoff

```
last: REPAIR session. The s013 engine-full red was two architecture ratchets, both real, both
  from DV4's own work, both fixed by MOVING code - no baseline entry, no skip, no relaxed bar.
  (1) Type ceiling: CourierWire.cs and ICourierSource.cs declared 4 types against 3; split out
  CourierJson.cs and CourierConflictException.cs. (2) bug #77, CLOSED: the prune-is-the-only-
  deleter sweep is FILE-level - a .cs is judged for MENTIONING InboxStore/InboxNote, then any
  File.Delete outside a method named Prune or TryDelete is an offender.
find: CourierPresence was swept only because it borrowed InboxStore.WriteAtomic as the engine's
  atomic writer - so did CourierOffset, CourierSettings and DeadLetterBox. Extracted
  Conductor.Core/AtomicFile.cs and REMOVED InboxStore.WriteAtomic; the note store is no longer a
  file-utility library. CourierDaemon.Discard was a real second deleter (the Append-race orphan):
  deleted it, the orphan now stays and is named in the log, which is what RemoteSurface.Inbound
  has always done in the same race. Do not re-add an inbox clean-up: kilobytes vs the invariant.
next: DV5.1 - the /cloud admin verb. Verify its flags against the installed claude FIRST (trap 16).
red: bug #76 (courier delivers an evidence artifact as text naming the path, uploads no file)
  still open and untouched.
```
