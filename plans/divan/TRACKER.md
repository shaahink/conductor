# Divan - the chancellery: inbox, courier, and the record that gets out - Phase Tracker

**Plan:** Divan - the chancellery: inbox, courier, and the record that gets out | **Branch:** `feat/divan` | **Design doc:** docs/dev/NEXT-ERA-FINDINGS-2026-08-23.md

## Handoff (overwrite this block, <=12 lines, no history)

last: era authored 2026-08-25, no session has run yet. The design authority is the findings doc
  named above, INCLUDING its Part 6 amendments - the decided options are restated in the plan
  file's header comment and each stage's notes; do not re-litigate them. The bug sweep's own
  strand doc is docs/dev/DIVAN-BUG-SWEEP-2026-08-25.md - eleven open bugs ride in from earlier
  runs in this repo's run.db and are in your open-bugs battery.
next: DV1.1 - channel health made loud. The seeded regression is the edge run's own failure
  (github block enabled, token absent, two log lines, nothing else). Read the stage notes and
  OBSERVABILITY-AND-MARKET-2026-08-22.md section 2.2 cause 1 before touching anything.
  Note: karvansara-edge's KS12.3 ship is PARKED WITH THE OWNER and is not this era's - this
  branch stacks on feat/karvansara-edge, unmerged by design.

## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 23 |
| Done | 0 |
| Carried open bugs at authoring | 11 |

## Checkpoints

Status in TODO - IN PROGRESS - DONE - DONE (confirmed by engine, shown with a check) - BLOCKED - SKIPPED.
Evidence = artifact path produced by a run this phase (a code path is not evidence). Agent claims are
marked DONE; the engine confirms.

### DV1 - The channel that says so

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV1.1 | Channel health is loud: every configured outbound channel carries live state landing in the REPORT.md header, /status and the owner queue within one boundary of a failure; configured-but-dead refused at preflight or parked loudly, never logged-and-ignored; the seeded proof is the edge run's own github-block-without-token failure, pinned by test in all three surfaces | TODO | | |
| DV1.2 | Owner queue pushed to admin chats on change: one item per push with the exact clearing command, KS11.3 grammar, golden-pinned; no-change-no-push proven so the channel is signal, not noise | TODO | | |

### DV2 - The sweep

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV2.1 | Triage ledger: every row of DIVAN-BUG-SWEEP's three ledgers dispositioned fix-this-stage or deferred-with-named-owner, committed as evidence; the 41/44/60 numbering resolved against the real bugs table; no row dropped | TODO | | |
| DV2.2 | Cluster A, prompt composition: per-battery budget shares so the knowledge ledger can no longer starve the open-bugs battery, with a rendered notice when a battery is dropped and a regression test on a grown ledger; bug 15 (prompt-size silent stop) and bug 21 (argv-ceiling warning) closed with tests | TODO | | |
| DV2.3 | Cluster B, channels: getUpdates 409 handling that names the other consumer and backs off; the false will-deliver-nothing startup line reads the resolved ChatCount; the telegram test endpoint survives a chats-only plan; report-push failures log their reason - all proven at the stub seam with scratch tokens | TODO | | |
| DV2.4 | Cluster C, state and verdict, and the close: budget counters persisted across engine restarts (the per-process-cap defect), the stage-boundary squash refuses to abort a STALE rebase and asserts ancestry after any abort, bug 27 first-write FK, 429-with-reset-time classified as backoff not AgentError, FU-F1-06 UpdateRunStatus lands; every fix-here row closed via the conductor bug verb, every remaining open row carries a named owner in the ledger | TODO | | |

### DV3 - The inbox

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV3.1 | Inbound message kinds: voice, audio, document, photo, caption, reply_to_message, message_thread_id on the DTO; getFile download; the 20 MB bot-API cap refused by name to the sender, never dropped; a stub-wire test drives each kind end to end | TODO | | |
| DV3.2 | The per-project inbox: durable store under .conductor/inbox (never committed - no gitignore allowlist entry, this repo is public), media beside transcript, atomic writes, append-only index deduped by update_id, read cursor with seen-by-session marks; InboxBattery on the IPromptBattery seam, fenced and framed, with an architecture test proving the fencing is always present; a note filed with no run live is read by the next session of the next run, proven in the rig | TODO | | |
| DV3.3 | Transcription: configured local command (faster-whisper on this machine's GPU), per-segment confidence marked in the stored note, unset command files the note untranscribed with audio kept and the reply saying so; conductor inbox prune is the only deletion path; a real .ogg transcribes in the rig | TODO | | |
| DV3.4 | Routing: a voice note sent as a reply to a checkpoint push files against that push's project with no command typed; sticky /project selection; message_thread_id topics in supergroups; unknown slug refused by name; unroutable notes parked in a machine-level dead-letter directory, never dropped | TODO | | |

### DV4 - The courier

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV4.1 | The daemon: conductor courier owns the token, polls always, routes to per-project inboxes via an explicit allowlist; durable poll offset in the state home plus update_id dedup - kill the courier between receive and acknowledge, restart, and the note files exactly once; the 24-hour Telegram retention limit stated in docs | TODO | | |
| DV4.2 | Lifecycle: courier install / uninstall / restart / status as a per-user Scheduled Task with restart-on-failure; tools/install.ps1 stops and restarts a running courier; version handshake at the loopback hello refuses a stale courier by name, naming the restart command; live proof registers a scratch-named task and unregisters it - the real install is the owner's at DV7.3 | TODO | | |
| DV4.3 | The seam: loopback-only listener, per-install shared secret file-permission-protected, own named port; CourierChannel on IMessageChannel so live runs push through the daemon; when a courier is configured, in-run polling refuses to start and names it; courier-less machines byte-identical by golden replay; killing the daemon makes a live run's REPORT.md, /status and owner queue all say so within one boundary | TODO | | |
| DV4.4 | Promotion: note to followups.md row to Tier-B lane by an explicit button on the acknowledgement; auto-inject from an inbox note refused by design with a negative test proving no code path does it; filing stays admin-only | TODO | | |

### DV5 - The cloud, the safe shapes

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV5.1 | The /cloud admin verb: flags verified against the installed CLI first; preflight requires clean tree and pushed, current branch, refusing by name in the chat with the exact git state; session id and URL returned to the chat and recorded in the event log as an owner action; follow-ups ride claude -p --cloud | TODO | | |
| DV5.2 | The cloud lane behind a flag, default off: only work needing no conductor tools and no verdict; branch consumed, every gate re-run locally, the referee never moves; cost recorded and reported as unknown, pinned by a test that no code path prints zero for a cloud lane; droppable without losing DV5.1 | TODO | | |

### DV6 - The record that gets out

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV6.1 | Bugs and followups as a long-lived issue class: conductor:bug / conductor:followup labels, opened when filed, closed by the closing commit, surviving the run; the daily digest gains the ledger line, golden-pinned | TODO | | |
| DV6.2 | The columns: Projects v2 mutation path landed - live if the token now carries project scope, else behind the existing refusal with stubbed proof and a filed finding naming gh auth refresh -s project as the owner's one-command unblock; the KS9.3 refusal moves either way | TODO | | |
| DV6.3 | CUT-FIRST - board snapshot as one self-contained HTML file rendered from Http/Contracts at each boundary, pushed as a Telegram document; the page states its own staleness; no inbound anything | TODO | | |
| DV6.4 | CUT-FIRST - SARIF export for file/line bugs uploaded to code scanning; docs state the public-free / private-needs-Advanced-Security split | TODO | | |

### DV7 - Ship Divan - close the era

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| DV7.1 | The internal record: ARCHITECTURE.md and docs/dev reconciled for everything Divan changed, the courier's lifecycle section and ADR included; closure ledger reconciled against DV2's triage; conductor budget re-measured through the fresh build against a backup copy and written into TOKEN-BUDGET-TUNING for the next era | TODO | | |
| DV7.2 | The published surface: README, cli.md, operating.md, plan-config.md, quickstart/troubleshooting where touched, docs index; CHANGELOG Unreleased written as the release body (it may still carry edge's entries - the split call is the owner's); docs-match-reality tests extended and proven red on a seeded stale doc; payesh harvest re-run on a branch with a PR, never pushing main | TODO | | |
| DV7.3 | OWNER-ONLY ship: merge (stacked on feat/karvansara-edge - KS12.3 lands first or together), tag, reinstall, real courier install, github sync backfill of this run, payesh PR merge, tracker and findings doc move to docs/history; a session pre-flights and parks with the runbook, it does not perform them | TODO | | |

## Legend

- A checkpoint is DONE only when claimed through the conductor task verb with an evidence path;
  the engine confirms it after its own battery. Prose claims move nothing.
- Escalation: a line in the handoff block beginning with the word HUMAN followed by a colon parks
  the run for the owner. The literal token appears only when escalating right now - never in
  prose, notes or this legend's examples.
- Evidence artifacts live under .conductor/evidence/DVn/ and are force-added (the directory is
  gitignored by star).
