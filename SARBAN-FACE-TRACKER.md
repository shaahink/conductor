# Sarban face — tracker

The authority is `docs/history/CONDUCTOR-SARBAN.md` (Part II, sections SF1–SF7, including the
screenshot-critique table). This file is the checkpoint surface conductor drives; it is a
**generated view** of the work graph in `.conductor/run.db`. Claim with
`conductor task --done <id> --evidence <path>` — hand-editing a row claims nothing.

**Prerequisite — MET 2026-07-31:** the core plan completed 26/26, the owner republished the engine
(now the v0.2.0 release build `0.2.0+f638ba6f7f14`), and a Telegram test push arrived. SF3 spends
the structure SC7 captured; SF4.2 pushes through SC1's fix.

**SF0 is the leftovers lane and it runs FIRST**, added 2026-07-31: the core run's eleven open bugs
(ledger ids 2,3,4,5,6,8,9,10,11,12,13) plus the still-open rows in `.conductor/followups.md`. It
leads because a debt lane scheduled last is a debt lane that gets dropped — which is how this
backlog was built. The bug ledger is **run-scoped** — `conductor bug list` under this plan cannot
see them — so they are transcribed into `followups.md` and that transcription is the source of
truth for the lane.

**Out of scope, deliberately:** the SF7.2 merge itself is owner-signed (ownerGate) — prepare it,
do not perform it.

## Handoff  (overwrite this block, ≤12 lines, no history)
last: nothing yet — authored with the era spec, amended 2026-07-31 with the FU-OWNER rows and SF0.
stage: **SF0 TODO** (attempt 0).
gate: not yet run this era.
next: **SF0.1** — inert plan keys (bugs 6, 11) either work or are rejected at load; the Telegram
  start line stops claiming a service that early-returned. Read the carried-forward bug table in
  `.conductor/followups.md` first — those eleven bugs are NOT in your run.db.
trap: a SECOND conductor run is live on this machine in another repo — no install.ps1, and never
  kill a conductor or dotnet pid without checking its command line first (promptExtra trap 0).
  Tab changes touch tabKey in model.go AND the hand-maintained help legend in cmdbar.go; goldens
  regenerate in a separate rebaseline commit. Exercise changes through YOUR build, never the
  conductor on PATH; live-run proofs go in a scratch repo with its own port, never against this
  repo's .conductor.

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path.
Checkpoint ids share a prefix with their stage id (SF1.1 belongs to stage SF1).

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SF0.1 | Bugs 6 and 11 die as a class — an inert plan key is either wired to its documented meaning or rejected at load, never readable-and-ignored — and bug 2 plus FU-OWNER-12 stop the notification path lying: no start line for a service that early-returned, and one logged sentence at run start saying whether pushes can be delivered at all | TODO | | |
| SF0.2 | Bug 10 — a claim made during a Verify or Audit session is counted, stamped and confirmed like any other, with the empty-string GateSummary evidence fallback fixed in the same change — plus bug 4 (a phase-gate RED names the session kind it actually queues), bug 3 (a confirmed last stage completes instead of spinning forever) and bug 8 (the harness git helper asserts its exit code, so NewCommits assertions stop being vacuous) | TODO | | |
| SF0.3 | Bugs 9, 5, 12 and 13 — one pid-liveness policy everywhere including MCP, bg status survives an uninspectable pid, bg start stops leaking the caller's stdout handle, bg logs reads a live log — and FU-OWNER-9's self-PID guard lands with the locked-by-conductor warning in the fix prompt | TODO | | |
| SF0.4 | Open bugs survive the run that found them — a new run in this repo sees the previous run's open rows, and run-ended says how many are open — and every remaining followups.md row is fixed, closed with its evidence, or re-homed to a living owner, with FU-F1-07 verified against SC8's scanning verb-parity test and FU-B10-2 measured from the core run's own sessions | TODO | | |
| SF1.1 | Verifier scores are served by a real endpoint and the Report tab renders them without SQL | TODO | | |
| SF1.2 | The Dev SQL console and its traces are gone — tab, /report/query, report --query — while MCP run_query stays for chat and the two non-SQL Dev panels are re-homed, not deleted | TODO | | |
| SF1.3 | The face has at most ten tabs after a written consolidation note: Console folds into Agent as a raw toggle, Timeline merges with Sessions into one history surface; keys, help and goldens regenerated | TODO | | |
| SF2.1 | Home shows one honest connection line with age, start-a-run instructions only when no run exists, a last-run summary card when offline, one Connected definition, and consistent path casing | TODO | | |
| SF2.2 | One shared time formatter renders local time with relative age and a date when not today; the Timeline UTC mislabel is fixed and the previously-unrendered timestamps render | TODO | | |
| SF2.3 | Over-budget renders as OVER never zero-percent headroom; window and lifetime spend are distinguished; the top bar shows in-flight session cost live; the attempts marker has a legend | TODO | | |
| SF3.1 | Tool calls render as one-liners and each session has a digest panel — tool mix, files touched, claims, bg-purpose storyline; fold is rune-safe | TODO | | |
| SF3.2 | The kanban groups by stage with the active stage highlighted, card meta visible unselected, column totals, skips separated from Done, in-column scroll, and a you-are-here ribbon | TODO | | |
| SF3.3 | Branch, dirty state, ahead-behind and HEAD sha are on the wire and in the face; session history shows commit subjects; the sidebar cues execution-vs-declared stage order | TODO | | |
| SF4.1 | OWNER-QUEUE.md and GET /owner/queue collect every open human item — HUMAN lines, ownerGates, parks with age, blocked-until waits — each saying what it unblocks and the command that clears it, regenerated at session boundaries | TODO | | |
| SF4.2 | The face surfaces the owner queue with age and unblocks, and a newly-arrived item pushes to Telegram | TODO | | |
| SF5.1 | conductor watch blocks silently and returns or fires a hook only on the wake set — park, circuit breaker, budget park, phase RED twice on a stage, engine gone, run ended — with a json brief of about thirty lines and a timeout heartbeat | TODO | | |
| SF5.2 | A supervisor plan block runs a configured command on wake with the brief on stdin; operating.md carries the wake and dont-wake table and the standing-order pattern | TODO | | |
| SF5.3 | The remote supervision pattern is documented and proven once end to end — a wake reaching a remote listener — with an honest note of what stays manual | TODO | | |
| SF5.4 | conductor ps lists every run on the machine from the control-plane discovery files; process titles carry repo and run id; the face offers a run picker when more than one control plane answers | TODO | | |
| SF6.1 | The built-in session and fix templates carry the field lessons: in-progress first, claim before handoff, deferred-MCP fallback on one line, long commands under conductor bg, the anchor-commit rule for multi-repo plans | TODO | | |
| SF6.2 | The prompt bank under plans/ is pruned, enriched from the rounds — proof-note pattern, owner-block alternate completions, the unblocks voice — and indexed so it is choosable | TODO | | |
| SF6.3 | conductor init scaffolds the refreshed template set with telegram and supervisor hints, and its output passes doctor clean | TODO | | |
| SF7.1 | The docs match the code — plan-config advisor default, tracker runtime files, operating supervision section, NEXT-FEATURES refresh — the field notes carry a closure ledger, and the era CHANGELOG is written | TODO | | |
| SF7.2 | feat/sarban is merged to master by the owner, the release is tagged through the SC8 pipeline, and the installed conductor version matches the releases page | TODO | | |
