# Conductor — Divan - the chancellery: inbox, courier, and the record that gets out run report

_Updated 2026-08-26 09:03 UTC · branch `feat/divan` · HEAD `e6d0d1c`_

**Status:** Idle
**Stage:** DV4 — The courier - one bot, always awake, outliving the run · attempts used 0 · working ▸ DV4.3
**Checkpoints:** 12/23 done · **Sessions run:** 11 · **Cost:** $155.7828 (agent $155.7102 + gates $0.0725) · **Tokens:** 2,287,135 in / 834,497 out
**Confirmed phases:** DV1, DV2, DV3

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| DV1 | The channel that says so - health made loud, the queue that reaches you | ██████████ 2/2 | confirmed ✓ |
| DV2 | The sweep - every known defect triaged, the clusters burned down | ██████████ 4/4 | confirmed ✓ |
| DV3 | The inbox - feedback that arrives when you have it, and survives the run | ██████████ 4/4 | confirmed ✓ |
| DV4 | The courier - one bot, always awake, outliving the run | █████░░░░░ 2/4 | **← active** |
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

<details><summary>DV4 — The courier - one bot, always awake, outliving the run (2/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| DV4.1 | The daemon: conductor courier owns the token, polls always, routes to per-project inboxes via an explicit allowlist; durable poll offset in the state home plus update_id dedup - kill the courier between receive and acknowledge, restart, and the note files exactly once; the 24-hour Telegram retention limit stated in docs | ✅ DONE | [`b0cc449`](https://github.com/shaahink/conductor/commit/b0cc449) |
| DV4.2 | Lifecycle: courier install / uninstall / restart / status as a per-user Scheduled Task with restart-on-failure; tools/install.ps1 stops and restarts a running courier; version handshake at the loopback hello refuses a stale courier by name, naming the restart command; live proof registers a scratch-named task and unregisters it - the real install is the owner's at DV7.3 | ✅ DONE | - |
| DV4.3 | The seam: loopback-only listener, per-install shared secret file-permission-protected, own named port; CourierChannel on IMessageChannel so live runs push through the daemon; when a courier is configured, in-run polling refuses to start and names it; courier-less machines byte-identical by golden replay; killing the daemon makes a live run's REPORT.md, /status and owner queue all say so within one boundary | ⬜ TODO | - |
| DV4.4 | Promotion: note to followups.md row to Tier-B lane by an explicit button on the acknowledgement; auto-inject from an inbox note refused by design with a negative test proving no code path does it; filing stays admin-only | ⬜ TODO | - |

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

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 9 | 215.1M | 98.7% | $148.41 | 11 | 19.6M | $13.49 |
| stage DV1 | 1 | 23.7M | 98.8% | $16.04 | 2 | 11.8M | $8.02 |
| stage DV2 | 3 | 77.3M | 98.7% | $52.81 | 4 | 19.3M | $13.20 |
| stage DV3 | 3 | 78.8M | 98.6% | $55.23 | 4 | 19.7M | $13.81 |
| stage DV4 | 2 | 35.3M | 98.7% | $24.34 | 1 | 35.3M | $24.34 |
| 2026-08 | 9 | 215.1M | 98.7% | $148.41 | 11 | 19.6M | $13.49 |

_Where the money goes: agent $148.35 (100%) · gate $0.06 (0%) · blended $0.69/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-25 22:48:30  ▪ gate face-full pass [phase]  (2.6s)
08-25 22:48:30  ✓ checkpoint DV2.1 confirmed
08-25 22:48:30  ✓ checkpoint DV2.2 confirmed
08-25 22:48:30  ✓ checkpoint DV2.3 confirmed
08-25 22:48:30  ✓ checkpoint DV2.4 confirmed
08-25 22:48:30  ▸ stage DV2 confirmed  (2h24m35s)
08-25 22:48:31  ▸ stage DV3 entered — The inbox - feedback that arrives when you have it, and survives the run
08-25 22:48:31  • session #6 DV3 Deliver started (attempt 1/8)
08-25 23:32:48  ▪ gate engine-fast pass [session]  (1m03s)
08-25 23:32:48  ▪ gate face-fast pass [session]  (2.9s)
08-25 23:32:49  • session #6 DV3 → Advanced · done DV3.1,DV3.2 · 5 commit(s)  (44m18s)
08-25 23:32:50  • session #7 DV3 Deliver started (attempt 1/8)
08-26 00:23:03  ▪ gate engine-fast pass [session]  (1m11s)
08-26 00:23:03  ▪ gate face-fast pass [session]  (32.8s)
08-26 00:23:04  • session #7 DV3 → Advanced · done DV3.3,DV3.4 · 3 commit(s)  (50m13s)
08-26 00:33:02  ▪ gate engine-fast pass [phase]  (0.0s)
08-26 00:33:02  ▪ gate face-fast pass [phase]  (0.0s)
08-26 00:33:02  ▪ gate engine-full FAIL [phase]  (5m00s)
08-26 00:33:02  ▪ gate face-full pass [phase]  (2.9s)
08-26 00:33:03  • session #8 DV3 Fix started (attempt 2/8)
08-26 00:53:21  ▪ gate engine-fast pass [session]  (1m04s)
08-26 00:53:21  ▪ gate face-fast pass [session]  (3.5s)
08-26 00:53:22  • session #8 DV3 → Progress · 2 commit(s)  (20m18s)
08-26 00:58:18  ▪ gate engine-fast pass [phase]  (0.0s)
08-26 00:58:18  ▪ gate face-fast pass [phase]  (0.0s)
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
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 11 · retries 2 (18 %) · overall Warn
⚠ [context-saturation] session #2: 23,394,515 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #3: 34,336,668 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #5: 32,631,442 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 32,762,695 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #7: 34,491,022 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #9: 34,833,588 context tokens (≥ 20,000,000)
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/divan
working tree: clean
vs upstream: up to date
```

### Commits by session

- **s3 (DV2 Deliver)** — 5 commit(s):
  - [`103e387`](https://github.com/shaahink/conductor/commit/103e387) fix(DV2.3, part): cluster B's four source fixes, tests still owed
  - [`dad5030`](https://github.com/shaahink/conductor/commit/dad5030) docs(divan): the handoff for DV2.3 - cluster A closed, and the two traps in cluster B
  - [`b8efd63`](https://github.com/shaahink/conductor/commit/b8efd63) fix(DV2.2): the battery budget is shared, and the argv wall is checked where it is hit
  - [`d8dac20`](https://github.com/shaahink/conductor/commit/d8dac20) docs(divan): the handoff for DV2.2 - where the ids live now, and what the map got wrong
  - [`c86dcec`](https://github.com/shaahink/conductor/commit/c86dcec) triage(DV2.1): fifty defects, one disposition each, and three corrections to the map
- **s4 (DV2 Deliver)** — 3 commit(s):
  - [`50ca4a8`](https://github.com/shaahink/conductor/commit/50ca4a8) docs(divan): the handoff for DV2.4 - cluster C, and the probe that caught #66
  - [`96433ec`](https://github.com/shaahink/conductor/commit/96433ec) test(DV2.3): the report push line end to end, and the evidence
  - [`2b37a01`](https://github.com/shaahink/conductor/commit/2b37a01) test(DV2.3): cluster B's regression tests, and what the first one found
- **s5 (DV2 Deliver)** — 7 commit(s):
  - [`292426a`](https://github.com/shaahink/conductor/commit/292426a) docs(divan): say plainly that the full suite was still running at session end
  - [`b3378e6`](https://github.com/shaahink/conductor/commit/b3378e6) docs(divan): the handoff for DV3.1 - the sweep is closed, and the tail that was never read
  - [`658cfc8`](https://github.com/shaahink/conductor/commit/658cfc8) refactor(DV2.4): split RunLoop's teardown and SessionRunner's refusal helpers; DV2.4 evidence
  - [`73f05d0`](https://github.com/shaahink/conductor/commit/73f05d0) fix(DV2.4): bug #69 - a rate limit parks the run instead of spending its attempts
  - [`6aa028b`](https://github.com/shaahink/conductor/commit/6aa028b) test(DV2.4): FU-F1-06 - a parked run reads back as parked, proven end to end
  - [`4866902`](https://github.com/shaahink/conductor/commit/4866902) fix(DV2.4): bug #68 - the budget reaches the store on every exit path; bug #71 pinned
  - [`8c23aaf`](https://github.com/shaahink/conductor/commit/8c23aaf) fix(DV2.4): bug #67 - the squash refuses a stale rebase instead of rewinding the branch
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

engine-fast:OK · face-fast:OK

## Last session result

> **DV4.2 landed - courier lifecycle as a logon task, presence record, version handshake**
> - Live proof PASS against the real Task Scheduler: 32 checks, scratch task registered then removed, owner's task untouched
> - Found and fixed a guard defect that would have crashed tools/install.ps1 on every machine without a courier
> - Scoped suite DV4_2 | DV4_1 | SF7_1 | K7_2 | KS11_1: 121 passed, 0 failed
>
> artefacts: 962d6bc, 5c78d3f, e6d0d1c, src/Conductor.Core/Courier/{CourierTask, CourierPresence, CourierProtocol}.cs, src/Conductor/Commands/CourierCommand.Lifecycle.cs, tools/lib/courier-guard.ps1
>
> evidence: .conductor/evidence/DV4/dv4-2-lifecycle.md, .conductor/evidence/DV4/dv4-2-live-proof.log
>
> gaps: DV4.3 (loopback listener, shared secret, CourierChannel, golden replay) untouched; the real courier installation stays the owner's at DV7.3

## Tracker handoff

```
last: DV4.2 landed and is claimed. `courier install|uninstall|restart|stop` register a per-user
  Scheduled Task from XML (RestartOnFailure PT1M, IgnoreNew, ExecutionTimeLimit PT0S, LeastPrivilege
  + InteractiveToken) - `schtasks /SC ONLOGON` cannot express restart-on-failure at all. `courier run`
  now writes courier.run.json (pid, protocol, engine, exe, task) and clears it on exit;
  CourierProtocol.RefuseStale refuses an OLDER courier by task name with the restart command.
  tools/install.ps1 brackets its publish with tools/lib/courier-guard.ps1.
find: the live proof found what tests could not - the scheduler DROPS <RunLevel> when it is the
  default and rewrites <UserId> to a SID (assert the ABSENCE of HighestAvailable, not the element);
  and courier-guard.ps1 called schtasks directly, which under install.ps1's ErrorActionPreference=Stop
  made a native stderr write TERMINATING - it would have crashed the installer on every machine
  without a courier. Also: KS11.1's seam test strips comments but READS STRING LITERALS.
next: DV4.3 the seam. CourierPresence IS the hello, written down - serve the same record over the
  loopback socket and reuse RefuseStale unchanged. Rig: tools/dv4/dv4-2-live-proof.ps1 (its token
  gate and its Sch wrapper are worth copying). This machine HAS a bot token in the user environment.
red: none from the scoped runs (DV4_2|DV4_1|SF7_1|K7_2|KS11_1 = 121/121, live proof PASS).
```
