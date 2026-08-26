# Divan - the chancellery: inbox, courier, and the record that gets out Phase Tracker

**Plan:** Divan - the chancellery: inbox, courier, and the record that gets out | **Branch:** `feat/divan` | **Design doc:** docs/dev/NEXT-ERA-FINDINGS-2026-08-23.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: DV7.2 DONE (claimed, evidence .conductor/evidence/DV7/dv7-2-payesh-harvest.md). The payesh
  harvest re-run: PR https://github.com/shaahink/payesh/pull/3, base ks12/harvest-era-close, main
  never pushed. The plain re-harvest moved nothing - which exposed that the corpus stopped in early
  August, so the two closed eras joined it (18->20 runs, 340->387 sessions, $3,016->$3,488,
  648/677 -> 781/813 gates, all recomputed). `needs_human` was a status the pages paint in no role:
  fixed, plus a harvest-side refusal for ANY unpaintable status, both proven red. 128/128 tests.
find: the live run is deliberately NOT published (21/23 would freeze wrong) - it earns its
  anonymise.json entry from the harvest AFTER its last checkpoint closes. Bug #83 filed: a second
  anonymity false positive, from a source #47/#41 do not cover.
next: DV7.3 - pre-flight each precondition and PARK with the runbook, the KS12.3 pattern at
  .conductor/evidence/KS12/ks12-3-owner-runbook.md. Two things it must say: "merge the payesh PR"
  is now TWO merges, #2 then #3; and feat/divan stacks on feat/karvansara-edge.
red: #83, #82, #81, #80, #79, #76, #75 (a note keeps only its first line - write one long line).

## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 23 |
| Done | 20 |
| Claimed (unconfirmed) | 1 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### DV1 — The channel that says so - health made loud, the queue that reaches you

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV1.1 | Channel health is loud: every configured outbound channel carries live state landing in the REPORT.md header, /status and the owner queue within one boundary of a failure; configured-but-dead refused at preflight or parked loudly, never logged-and-ignored; the seeded proof is the edge run's own github-block-without-token failure, pinned by test in all three surfaces | DONE ✓ | b9ea19f | .conductor/evidence/DV1/dv1-1-live-rig.txt |
| DV1.2 | Owner queue pushed to admin chats on change: one item per push with the exact clearing command, KS11.3 grammar, golden-pinned; no-change-no-push proven so the channel is signal, not noise | DONE ✓ | b9ea19f | .conductor/evidence/DV1/dv1-2-owner-queue-push.txt |

### DV2 — The sweep - every known defect triaged, the clusters burned down

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV2.1 | Triage ledger: every row of DIVAN-BUG-SWEEP's three ledgers dispositioned fix-this-stage or deferred-with-named-owner, committed as evidence; the number-46-lost karvan rows recovered from the imported copy and the dangling bug-44 reference settled; no row dropped | DONE ✓ | c86dcec | .conductor/evidence/DV2/dv2-1-triage-ledger.md |
| DV2.2 | Cluster A, prompt composition: per-battery budget shares so the knowledge ledger can no longer starve the open-bugs battery, with a rendered notice when a battery is dropped and a regression test on a grown ledger; bug 15 (prompt-size silent stop) and bug 21 (argv-ceiling warning) closed with tests | DONE ✓ | c86dcec | .conductor/evidence/DV2/dv2-2-cluster-a.txt |
| DV2.3 | Cluster B, channels: getUpdates 409 handling that names the other consumer and backs off; the false will-deliver-nothing startup line reads the resolved ChatCount; the telegram test endpoint survives a chats-only plan; report-push failures log their reason - all proven at the stub seam with scratch tokens | DONE ✓ | 2b37a01 | .conductor/evidence/DV2/dv2-3-cluster-b.md |
| DV2.4 | Cluster C, state and verdict, and the close: budget counters persisted across engine restarts (the per-process-cap defect), the stage-boundary squash refuses to abort a STALE rebase and asserts ancestry after any abort, bug 27 first-write FK, 429-with-reset-time classified as backoff not AgentError, FU-F1-06 UpdateRunStatus lands; every fix-here row closed via the conductor bug verb, every remaining open row carries a named owner in the ledger | DONE ✓ | 8c23aaf | .conductor/evidence/DV2/dv2-4-cluster-c.md |

### DV3 — The inbox - feedback that arrives when you have it, and survives the run

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV3.1 | Inbound message kinds: voice, audio, document, photo, caption, reply_to_message, message_thread_id on the DTO; getFile download; the 20 MB bot-API cap refused by name to the sender, never dropped; a stub-wire test drives each kind end to end | DONE ✓ | 2bebfbe | .conductor/evidence/DV3/dv3-1-inbound-kinds.md |
| DV3.2 | The per-project inbox: durable store under .conductor/inbox (never committed - no gitignore allowlist entry, this repo is public), media beside transcript, atomic writes, append-only index deduped by update_id, read cursor with seen-by-session marks; InboxBattery on the IPromptBattery seam, fenced and framed, with an architecture test proving the fencing is always present; a note filed with no run live is read by the next session of the next run, proven in the rig | DONE ✓ | 2bebfbe | .conductor/evidence/DV3/dv3-2-the-inbox.md |
| DV3.3 | Transcription: configured local command (faster-whisper on this machine's GPU), per-segment confidence marked in the stored note, unset command files the note untranscribed with audio kept and the reply saying so; conductor inbox prune is the only deletion path; a real .ogg transcribes in the rig | DONE ✓ | 4b1f04a | .conductor/evidence/DV3/dv3-3-transcription.md |
| DV3.4 | Routing: a voice note sent as a reply to a checkpoint push files against that push's project with no command typed; sticky /project selection; message_thread_id topics in supergroups; unknown slug refused by name; unroutable notes parked in a machine-level dead-letter directory, never dropped | DONE ✓ | 4b1f04a | .conductor/evidence/DV3/dv3-fix-red-battery.md |

### DV4 — The courier - one bot, always awake, outliving the run

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV4.1 | The daemon: conductor courier owns the token, polls always, routes to per-project inboxes via an explicit allowlist; durable poll offset in the state home plus update_id dedup - kill the courier between receive and acknowledge, restart, and the note files exactly once; the 24-hour Telegram retention limit stated in docs | DONE ✓ | b0cc449 | .conductor/evidence/DV4/dv4-1-courier-daemon.md |
| DV4.2 | Lifecycle: courier install / uninstall / restart / status as a per-user Scheduled Task with restart-on-failure; tools/install.ps1 stops and restarts a running courier; version handshake at the loopback hello refuses a stale courier by name, naming the restart command; live proof registers a scratch-named task and unregisters it - the real install is the owner's at DV7.3 | DONE ✓ | 962d6bc | .conductor/evidence/DV4/dv4-2-lifecycle.md |
| DV4.3 | The seam: loopback-only listener, per-install shared secret file-permission-protected, own named port; CourierChannel on IMessageChannel so live runs push through the daemon; when a courier is configured, in-run polling refuses to start and names it; courier-less machines byte-identical by golden replay; killing the daemon makes a live run's REPORT.md, /status and owner queue all say so within one boundary | DONE ✓ | 5544cff | .conductor/evidence/DV4/dv4-3-loopback-seam.txt |
| DV4.4 | Promotion: note to followups.md row to Tier-B lane by an explicit button on the acknowledgement; auto-inject from an inbox note refused by design with a negative test proving no code path does it; filing stays admin-only | DONE ✓ | 273674c | .conductor/evidence/DV4/dv4-4-promotion.md |

### DV5 — The cloud, the safe shapes - an owner verb, a flagged experiment

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV5.1 | The /cloud admin verb: flags verified against the installed CLI first; preflight requires clean tree and pushed, current branch, refusing by name in the chat with the exact git state; session id and URL returned to the chat and recorded in the event log as an owner action; follow-ups ride claude -p --cloud | DONE ✓ | 32a4868 | .conductor/evidence/DV5/dv5.1-live-proof.md |
| DV5.2 | The cloud lane behind a flag, default off: only work needing no conductor tools and no verdict; branch consumed, every gate re-run locally, the referee never moves; cost recorded and reported as unknown, pinned by a test that no code path prints zero for a cloud lane; droppable without losing DV5.1 | DONE ✓ | 32a4868 | .conductor/evidence/DV5/dv5.2-cloud-lane.md |

### DV6 — The record that gets out - bugs that outlive the board, columns, the page

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV6.1 | Bugs and followups as a long-lived issue class: conductor:bug / conductor:followup labels, opened when filed, closed by the closing commit, surviving the run; the daily digest gains the ledger line, golden-pinned | DONE ✓ | 8d14fe5 | .conductor/evidence/DV6/dv6-1-ledger-issue-class.md |
| DV6.2 | The columns: Projects v2 mutation path landed - live if the token now carries project scope, else behind the existing refusal with stubbed proof and a filed finding naming gh auth refresh -s project as the owner's one-command unblock; the KS9.3 refusal moves either way | DONE ✓ | d1e5b0e | .conductor/evidence/DV6/dv6-2-the-columns.md |
| DV6.3 | CUT-FIRST - board snapshot as one self-contained HTML file rendered from Http/Contracts at each boundary, pushed as a Telegram document; the page states its own staleness; no inbound anything | DONE ✓ | e17f09b | .conductor/evidence/DV6/dv6-3-the-page.md |
| DV6.4 | CUT-FIRST - SARIF export for file/line bugs uploaded to code scanning; docs state the public-free / private-needs-Advanced-Security split | DONE ✓ | 7a336e3 | .conductor/evidence/DV6/dv6-4-ratchet-fix.md |

### DV7 — Ship Divan - close the era

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV7.1 | The internal record: ARCHITECTURE.md and docs/dev reconciled for everything Divan changed, the courier's lifecycle section and ADR included; closure ledger reconciled against DV2's triage; conductor budget re-measured through the fresh build against a backup copy and written into TOKEN-BUDGET-TUNING for the next era | DONE | fc33699 | .conductor/evidence/DV7/dv7-1-closure-ledger.md |
| DV7.2 | The published surface: README, cli.md, operating.md, plan-config.md, quickstart/troubleshooting where touched, docs index; CHANGELOG Unreleased written as the release body (it may still carry edge's entries - the split call is the owner's); docs-match-reality tests extended and proven red on a seeded stale doc; payesh harvest re-run on a branch with a PR, never pushing main | IN PROGRESS | - | - |
| DV7.3 | OWNER-ONLY ship: merge (stacked on feat/karvansara-edge - KS12.3 lands first or together), tag, reinstall, real courier install, github sync backfill of this run, payesh PR merge, tracker and findings doc move to docs/history; a session pre-flights and parks with the runbook, it does not perform them | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
