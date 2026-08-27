# Charkh - the wheel: what the owner still does by hand becomes machinery Phase Tracker

**Plan:** Charkh - the wheel: what the owner still does by hand becomes machinery | **Branch:** `feat/charkh` | **Design doc:** docs/dev/CHARKH-PLAN-2026-08-26.md

## Handoff (overwrite this block, <=12 lines, no history)

last: CH4.3 AND CH4.4 both DONE and claimed; CH4 is closed. CH4.3 scoped the retire sweep -
  GithubIdentity gains OwnerMarker (NOT RunMarker; the diary is found by scanning bodies for that),
  an issue is retirable only if that marker is ours OR this run's GithubMap points at that number,
  and everything else is REFUSED BY NAME on GithubSyncResult.RetireRefused. CH4.4 made the runbook
  the verb's output: `conductor release runbook` renders from the SAME probes preflight runs and the
  SAME planner perform uses, and two tests derive the act vocabulary BY REFLECTION, so an act wired
  to nothing fails the day it is added. Evidence: .conductor/evidence/CH4/ch4-3-retire-scoped.md,
  ch4-4-release-runbook.md, and ch4-4-charkh-runbook.md - the generated runbook itself.
owed to CH5: (a) CH4.3's second clause, writing the edge run's GitHub record, is ordered AFTER the
  reinstall - today's A/B says the installed 0.5.0 would retire 14 of Charkh's OWN cards; (b) bug 91
  - IsStillGoing counts needs_human as in flight, so the corpus act omits that very backfill.
next: CH5.1, then CH5.2 closes the era using ch4-4-charkh-runbook.md REGENERATED at the time.

## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 14 |
| Done | 8 |
| Claimed (unconfirmed) | 3 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### CH1 — CI green, and the reason it was not

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| CH1.1 | The rendered board page is one document whatever the checkout did to the source: the inline CSS constant is normalised to LF at load, and a test asserts the PROPERTY (Render output carries no carriage return) rather than the symptom, so the next raw string literal in that file cannot reintroduce it silently | DONE ✓ | 1232ea0 | .conductor/evidence/CH1/ch1-1-lf-property.md |
| CH1.2 | A plan file in this repo is loadable on a fresh clone: the three KS1_4DoctorPlanLintsTests that load this repo's own plan and Validate it stop depending on an absolute machine path, by whichever of the two routes the checkpoint records as chosen, pinned by a test that would fail on the old form | DONE ✓ | 1232ea0 | .conductor/evidence/CH1/ch1-2-plan-repo-is-portable.md |
| CH1.3 | The local battery and CI can no longer disagree in silence: a divergence between what a run's gates just proved and what CI says about the same commit surfaces where the run can see it - the report header, the owner queue - in the DV1.1 channel-health shape, proven on a seeded divergence. Exit is CI green on Windows and Linux for master | DONE ✓ | 3750f9a | .conductor/evidence/CH1/ch1-3-flushevents-race-proof.md |

### CH2 — The tour that matches the engine - and knows when it does not

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| CH2.1 | docs/assets/demo.gif re-recorded against the v0.5.0 Face through the VHS container at the covered 1176x736 geometry, with the tape extended to the surfaces the last two eras added (the courier, the inbox, the board page, the hub); Docker verified FIRST with exact output, and if it cannot be made to work that is filed with the output rather than worked around | DONE ✓ | 13a5bfe | .conductor/evidence/CH2/CH2.1-demo-gif-rerecord.md |
| CH2.2 | Staleness becomes a gate rather than a thing somebody notices: a manifest of what the GIF was recorded from and a check that fails when the product has moved past it - payesh's social-card pattern ported, which is why payesh's cards were caught and conductor's GIF was not | DONE ✓ | 13a5bfe | .conductor/evidence/CH2/CH2.2-staleness-is-a-gate.md |

### CH3 — The docs say what shipped

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| CH3.1 | The published surface reconciled against the INSTALLED v0.5.0 engine rather than against intent: README first, then cli.md, operating.md, plan-config.md, quickstart.md, troubleshooting.md, tracker.md and the docs/README.md index; the courier is a real always-on process now and the docs still offer it as a possibility | DONE ✓ | ea75bda | .conductor/evidence/CH3/CH3.1-published-surface-reconciled.md |
| CH3.2 | Every reference resolves: the rule for the plans' notes prose citing the two moved briefs decided once, applied consistently and recorded in docs/dev/README.md; every relative link in docs/, every path in a test message and every contracts reference swept. Frozen run artifacts under .conductor are a record - reported, never rewritten | DONE ✓ | ea75bda | .conductor/evidence/CH3/CH3.2-references-resolve.md |
| CH3.3 | SF7_1DocsMatchRealityTests extended to every verb and config key this era adds, each new assertion proven RED on a seeded stale doc - the negative control is the point of the battery | DONE ✓ | ea75bda | .conductor/evidence/CH3/CH3.3-docs-battery-extended.md |

### CH4 — The machinery - the era-close stops being prose

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| CH4.1 | Release preflight as a verb: every precondition DV7.3 measured by hand becomes something the engine measures - ff-only merge, a CHANGELOG section the extractor exits 0 on, no live conductor process, migration versions matching, the courier's token scope and task state, the run whose backfill is owed - as a verdict per line with a non-zero exit when any line is red | DONE | cf8997f | .conductor/evidence/CH4/ch4-1-release-preflight.md |
| CH4.2 | The mechanical acts performed and the judgement acts refused BY NAME: the CHANGELOG rename, the tag, the ff-only merge and the doc move with its tracker/planDoc/readOrder repoint are performed; the version number, single-vs-split release and corpus inclusion are stopped at and named. An act that needs the owner is never silently skipped - that failure is exactly what KS12.3 was | DONE | cf8997f | .conductor/evidence/CH4/ch4-2-release-perform.md |
| CH4.3 | A backfill can no longer vandalise another run's board: the retire sweep is scoped to the run being synced, or a backfill that would retire another run's checkpoints is refused with what it would have closed. Measured 2026-08-26: the edge run's dry run reported 23 retired against exactly Divan's 23 checkpoints. Then the edge run's own GitHub record is written | DONE | - | .conductor/evidence/CH4/ch4-3-retire-scoped.md |
| CH4.4 | The owner runbook becomes the preflight's output rather than a document written from scratch each era, generated from its own measurements and carrying the exact commands - the DV7.3 and KS12.3 artifacts are the shape being replaced | IN PROGRESS | - | - |

### CH5 — Ship Charkh with the machinery it built

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| CH5.1 | The internal record: ARCHITECTURE.md and docs/dev reconciled for everything Charkh changed, a closure ledger naming every bug and followup closed this era or its living owner, and this run's budget re-measured through a fresh build against a sqlite3 BACKUP COPY of the store and written into TOKEN-BUDGET-TUNING as the number the next era compiles against | TODO | - | - |
| CH5.2 | The era closed USING CH4's machinery rather than by hand: the preflight run, the runbook it generated, the mechanical acts performed and the refused ones parked with the owner. Anything the machinery got wrong is recorded as a finding - that is worth more than the checkpoint | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
