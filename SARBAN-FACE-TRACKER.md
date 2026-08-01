# Sarban face - the watcher and the surfaces Phase Tracker

**Plan:** Sarban face - the watcher and the surfaces | **Branch:** `feat/sarban` | **Design doc:** docs/history/CONDUCTOR-SARBAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: **session 39 - SF7.1 CLAIMED DONE.** One commit `37a75ef`. The three `FIELD-NOTES-*.md` logs
  each end in a closure ledger, 31 rows, finding -> stage -> commit -> what closed it. Map measured
  from the commits (fifteen cite their own finding number) not from Appendix B, which was wrong in
  three places and is now corrected in place. devcontext #19 is the one half-closure and its row
  says what was NOT adopted. `SF7_1DocsMatchRealityTests` 16/16; the two new structural pins were
  driven RED on purpose before being trusted. Note 191's warning about `#5 -> SC5.2` was itself
  wrong - `SessionWatchdog.Remedy` is #5's fix and `git log -S Remedy` returns `e6b15c7`.
next: **SF7.2, the last checkpoint - and most of it is the owner's, not yours.** Merge `feat/sarban`
  to master is **ownerGate**: do not merge unsigned. `tools/install.ps1` is trap 0/1 - it is the
  FIRST install of this run and needs the owner to confirm no other conductor run is live first, so
  expect a `HUMAN:` line rather than a reinstall you perform. Tag through the SC8 pipeline; the era
  CHANGELOG sits under `Unreleased` and `tools/changelog-section.sh` is the reader that pipeline uses.
  red: nothing known. open: bugs **#15 #16 #17 #18 #19 #20 #21**.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 24 |
| Done | 6 |
| Claimed (unconfirmed) | 16 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### SF0 — The ledger closes - the core run's leftovers

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SF0.1 | Bugs 6 and 11 die as a class — an inert plan key is either wired to its documented meaning or rejected at load, never readable-and-ignored — and bug 2 plus FU-OWNER-12 stop the notification path lying: no start line for a service that early-returned, and one logged sentence at run start saying whether pushes can be delivered at all | DONE | 5217986 | engine-fast:OK · face-fast:OK |
| SF0.2 | Bug 10 — a claim made during a Verify or Audit session is counted, stamped and confirmed like any other, with the empty-string GateSummary evidence fallback fixed in the same change — plus bug 4 (a phase-gate RED names the session kind it actually queues), bug 3 (a confirmed last stage completes instead of spinning forever) and bug 8 (the harness git helper asserts its exit code, so NewCommits assertions stop being vacuous) | DONE | fdd78ae | engine-fast:OK · face-fast:OK |
| SF0.3 | Bugs 9, 5, 12 and 13 — one pid-liveness policy everywhere including MCP, bg status survives an uninspectable pid, bg start stops leaking the caller's stdout handle, bg logs reads a live log — and FU-OWNER-9's self-PID guard lands with the locked-by-conductor warning in the fix prompt | DONE | c84ccfc | engine-fast:OK · face-fast:OK |
| SF0.4 | Open bugs survive the run that found them — a new run in this repo sees the previous run's open rows, and run-ended says how many are open — and every remaining followups.md row is fixed, closed with its evidence, or re-homed to a living owner, with FU-F1-07 verified against SC8's scanning verb-parity test and FU-B10-2 measured from the core run's own sessions | DONE | d5b81cb | engine-fast:OK · face-fast:OK |

### SF1 — The face sheds dead weight

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SF1.1 | Verifier scores are served by a real endpoint and the Report tab renders them without SQL | DONE | 9d993ef | engine-fast:OK · face-fast:OK |
| SF1.2 | The Dev SQL console and its traces are gone — tab, /report/query, report --query — while MCP run_query stays for chat and the two non-SQL Dev panels are re-homed, not deleted | DONE ✓ | 8f96ef2 | engine-fast:OK · face-fast:OK |
| SF1.3 | The face has at most ten tabs after a written consolidation note: Console folds into Agent as a raw toggle, Timeline merges with Sessions into one history surface; keys, help and goldens regenerated | DONE | - | .conductor/evidence/SF1/SF1.3-summary.md |

### SF2 — The face tells the truth kindly - state, time, money

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SF2.1 | Home shows one honest connection line with age, start-a-run instructions only when no run exists, a last-run summary card when offline, one Connected definition, and consistent path casing | DONE | 93611dd | .conductor/evidence/SF2/SF2.1-summary.md |
| SF2.2 | One shared time formatter renders local time with relative age and a date when not today; the Timeline UTC mislabel is fixed and the previously-unrendered timestamps render | DONE | f05791b | .conductor/evidence/SF2/SF2.2-summary.md |
| SF2.3 | Over-budget renders as OVER never zero-percent headroom; window and lifetime spend are distinguished; the top bar shows in-flight session cost live; the attempts marker has a legend | DONE ✓ | ef1620f | .conductor/evidence/SF2/SF2.3-summary.md |

### SF3 — Reading a session becomes cheap

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SF3.1 | Tool calls render as one-liners and each session has a digest panel — tool mix, files touched, claims, bg-purpose storyline; fold is rune-safe | DONE | 352bc1a | .conductor/evidence/SF3/SF3.1-summary.md |
| SF3.2 | The kanban groups by stage with the active stage highlighted, card meta visible unselected, column totals, skips separated from Done, in-column scroll, and a you-are-here ribbon | DONE | 3e7c4b3 | .conductor/evidence/SF3/SF3.2-summary.md |
| SF3.3 | Branch, dirty state, ahead-behind and HEAD sha are on the wire and in the face; session history shows commit subjects; the sidebar cues execution-vs-declared stage order | DONE ✓ | f91fa5e | .conductor/evidence/SF3/SF3.3-part2d-renderers.md |

### SF4 — The human queue is a first-class surface

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SF4.1 | OWNER-QUEUE.md and GET /owner/queue collect every open human item — HUMAN lines, ownerGates, parks with age, blocked-until waits — each saying what it unblocks and the command that clears it, regenerated at session boundaries | DONE | - | .conductor/evidence/SF4/SF4.1-owner-queue.md |
| SF4.2 | The face surfaces the owner queue with age and unblocks, and a newly-arrived item pushes to Telegram | DONE ✓ | d61da19 | .conductor/evidence/SF4/SF4-fix-session25-sc1-identity-stamp.md |

### SF5 — Supervision without a polling meter

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SF5.1 | conductor watch blocks silently and returns or fires a hook only on the wake set — park, circuit breaker, budget park, phase RED twice on a stage, engine gone, run ended — with a json brief of about thirty lines and a timeout heartbeat | DONE | - | .conductor/evidence/SF5/SF5.1-live-drive.log |
| SF5.2 | A supervisor plan block runs a configured command on wake with the brief on stdin; operating.md carries the wake and dont-wake table and the standing-order pattern | DONE | 4efedac | .conductor/evidence/SF5/SF5.2-supervisor-block.md |
| SF5.3 | The remote supervision pattern is documented and proven once end to end — a wake reaching a remote listener — with an honest note of what stays manual | DONE | 2cd9083 | .conductor/evidence/SF5/SF5.3-live-drive.log |
| SF5.4 | conductor ps lists every run on the machine from the control-plane discovery files; process titles carry repo and run id; the face offers a run picker when more than one control plane answers | DONE ✓ | 3f0ff2e | .conductor/evidence/SF5/SF5.4-part3-run-picker.md |

### SF6 — The prompt bank compounds

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SF6.1 | The built-in session and fix templates carry the field lessons: in-progress first, claim before handoff, deferred-MCP fallback on one line, long commands under conductor bg, the anchor-commit rule for multi-repo plans | DONE | 8dd1aa3 | .conductor/evidence/SF6/SF6-fix-s36-budget-and-anchors.md |
| SF6.2 | The prompt bank under plans/ is pruned, enriched from the rounds — proof-note pattern, owner-block alternate completions, the unblocks voice — and indexed so it is choosable | DONE ✓ | 4b894c1 | .conductor/evidence/SF6/SF6-2-prompt-bank.md |
| SF6.3 | conductor init scaffolds the refreshed template set with telegram and supervisor hints, and its output passes doctor clean | DONE | - | .conductor/evidence/SF6/SF6-fix-s36-budget-and-anchors.md |

### SF7 — Ship the era

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SF7.1 | The docs match the code — plan-config advisor default, tracker runtime files, operating supervision section, NEXT-FEATURES refresh — the field notes carry a closure ledger, and the era CHANGELOG is written | IN PROGRESS | - | - |
| SF7.2 | feat/sarban is merged to master by the owner, the release is tagged through the SC8 pipeline, and the installed conductor version matches the releases page | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
