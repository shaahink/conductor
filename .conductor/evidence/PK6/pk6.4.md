# PK6.4 — the close, pre-flighted and parked

**Date:** 2026-09-18 · session #14 · stage PK6 (ownerGate) · branch `feat/peyk-courier`
**Release:** **v0.6.0**, ONE section covering BOTH eras (Charkh + Peyk) — the owner's call, 2026-09-18.

This checkpoint is `ownerGate` by design: a session **pre-flights and parks**. Every act below that is
the owner's is printed, not performed. `release perform --yes` is *refused outright* while a run is
live in the plan's state dir (`ReleaseCommand.Perform.cs:53`), and this run is live — so the rehearsal
is the only thing a session can produce, and it is what the acceptance asks for.

Everything here was driven through the **fresh build** (`dotnet run --project src/Conductor --`),
never the `conductor` on PATH (trap 2). Nothing was merged, tagged, moved, installed or pushed.

## Artifacts

| file | what it is |
|---|---|
| `pk6.4-preflight.txt` | `release preflight --tag 0.6.0` — 7 preconditions, exit 1 |
| `pk6.4-perform-dryrun.txt` | `release perform --tag 0.6.0` (no `--yes`) — 5 mechanical + 5 owner acts, exit 1 |
| `pk6.4-runbook.md` | `release runbook --tag 0.6.0 --out …` — the owner document, generated (7 preconditions, 10 acts) |
| `pk6.4-budget.txt` / `pk6.4-money.txt` | the era's own numbers, measured off a **backup copy** of the store |
| commit `10e4eee` | the CHANGELOG section for both eras |

## 1. Trap 19 cleared BEFORE anything touched a store

- tree `MigrationRunner.CurrentVersion` = **15** (`src/Conductor.Core/Store/MigrationRunner.cs:11`)
- installed engine **0.5.1-alpha.0.43+b12540557ade**; `git show b12540557ade:.../MigrationRunner.cs` = **15**
- preflight's own migration line agrees: `migration - no schema skew: tree v15, installed engine
  0.5.1-alpha.0.43 carries the same migrations` — store
  `...\AppData\Local\conductor\runs\conductor-karvansara-core---the-open-door-308cfb9b\run.db` **at schema 15**

No skew in either direction, so no write could have migrated anything forward. The measurement verbs
were still run against a copy, as the rules require:

```
sqlite3 <live run.db> ".backup '%TEMP%/pk64-run-backup.db'"     # 11,227,136 bytes, schema 15
conductor budget %TEMP%\pk64-run-backup.db --repo all
conductor money  %TEMP%\pk64-run-backup.db --repo all
```

Worth recording for the next session: **`C:\Code\conductor\.conductor\run.db` is NOT the live store.**
It sits at schema **9** and is a stale local artifact; the engine's store is the `runs/...-308cfb9b/`
one the preflight names. A measurement taken from the repo-local file would have been wrong by two
eras.

## 2. The era's numbers (`conductor money`, off the copy, 2026-09-18)

| era | sessions | tokens | cache | cost | checkpoints | $/ckpt |
|---|---|---|---|---|---|---|
| **Peyk** `f85c0bd7` | 12 | 191.2M | 98.1% | **$164.00** | 16 | $10.25 |
| **Charkh** `858b4838` | 9 | 178.9M | 98.5% | $129.20 | 13 | $9.94 |
| **v0.6.0 total** | 21 | 370.1M | — | **$293.20** | 29 | — |

Peyk by stage: PK1 $16.23 · PK2 $22.94 · PK3 $31.44 · PK4 $41.33 · PK5 $29.76 · PK6 $22.30.
Both totals are **floors**: a session that times out records no cost rows at all
(`docs/dev/TOKEN-BUDGET-TUNING.md` section 14). `conductor budget`'s tuner, from the same copy,
prescribes **48M / 0.90** for the next era off Peyk's window (nudge 43.2M clears the 42.2M largest
closer, headroom 4.8M = 3.2x the 1.51M measured wrap-up) — an input to plan B's budget row, not a
decision taken here.

## 3. The CHANGELOG section — commit `10e4eee`

91 lines inserted, nothing deleted. Peyk's material written from `master..feat/peyk-courier`
(69 commits at the time of the rehearsal), spliced **into the existing `[Unreleased]` section** below
Charkh's blocks, with its own narrative paragraph and `### Added` / `### Fixed` / `### Changed`.

**The heading was deliberately left as `## [Unreleased]`.** `DoChangelogAsync`
(`ReleaseCommand.Perform.cs:198`) does `ReplaceFirst(text, "## [Unreleased]", "## [0.6.0] - <today>")`
and fails by name if that literal has vanished. Renaming it by hand here would have taken the
mechanical act away from the owner and left the verb reporting a failure for work already done. The
rehearsal confirms the act is now ready and sized:

```
-> changelog  mechanical rename '## [Unreleased]' to '## [0.6.0] - 2026-09-18' over 190 lines
              after this, `sh tools/changelog-section.sh 0.6.0` exits 0 and prints that body
```

## 4. The preflight — 4 of 7 red, and every red is a state the owner's own acts clear

| line | verdict | why, and what clears it |
|---|---|---|
| merge | red | 69 ahead of master, **would** fast-forward; the working tree is dirty. Dirty is unavoidable while the run is live (`.conductor/REPORT.md` is rewritten by the engine every stage). Clears when the run ends. |
| changelog | red | no `## [0.6.0]` section — **by design**, see section 3. The changelog act creates it. |
| docs | red | 11 rows in 2 files (`docs/cli.md` x5, `docs/operating.md` x6) still say "not in the released binary yet". The docs act rewrites them to "New in `v0.6.0`" (bug #95's machinery). |
| processes | red | 3 reasons a binary swap is unsafe: engine pid **15672** (`CONDUCTOR_PID`, the run asking) and pid 25164, plus the live-run lock. Clears when the run ends. |
| migration | green | section 1. |
| courier | green | installed, running (pid 20860), reachable, token persisted at User scope, 3 chats, 6 projects. It still runs the **old** binary; bug #100's fix reaches it at the owner's reinstall. |
| backfill | owner's | see section 6. |

Not one red is a defect in the branch, and not one is fixable by a session.

## 5. The five mechanical acts, as the rehearsal plans them

`changelog` -> `docs` -> `merge` -> `tag` -> `docmove` (`ReleasePerform.cs:40`), stopping at the first
refusal. The rehearsal reached `merge` and stopped, so `tag` and `docmove` read *not attempted*.
**When the owner runs it on a clean tree, all five run — including `docmove`.**

### The docmove act must NOT be allowed to run yet — bug #101

`ProbeDocMove` (`ReleaseCommand.Perform.cs:368`) derives its moves from **this** plan's `planDoc` and
`tracker`, and the repoint half rewrites **only this plan file**. Measured:
`docs/dev/NEXT-ERA-FINDINGS-2026-09-17.md` is still named by four live things outside this plan —

- `plans/peyk/watch.plan.json:8` (`planDoc`)
- `plans/peyk/watch.plan.json:210` (`readOrder`)
- `plans/peyk/templates/session.md:32`
- `plans/peyk/WATCH-TRACKER.md:3`

— so a `git mv` into `docs/history` would leave **plan B's every session opening nothing**, which is
trap 16's exact shape. Filed as **bug #101** (medium, PK6): docmove should scan sibling plans for
references to what it moves and either repoint them all or refuse by name. Until then the act has no
per-act skip (`MechanicalOrder` is a fixed list), so the owner's instruction is explicit in section 6:
**stop before docmove.**

**The plan doc is NOT moved by this checkpoint.** That is the acceptance, and it is recorded here and
in the handoff.

## 6. The owner's acts — printed, not performed

Verbatim from the rehearsal (full text in `pk6.4-perform-dryrun.txt` and `pk6.4-runbook.md`):

1. **version** — `0.6.0`, because the owner said so. Nothing in the repo can decide it.
2. **split** — one release or two. **Decided: one.** A single section covering both eras is one
   rename; splitting means tagging an intermediate commit and cutting a section in half by hand.
3. **corpus** — 4 runs have no GitHub record (Karvansara edge `9491891f...`, Karvansara core
   `9647f1b8...`, Sarban face `8cefa5de...`, Sarban core `e9e21d10...`). **DECLINED by the owner,
   2026-09-18** — bug #84's repo-wide sweep retires the other runs' issues, so backfilling one run
   costs the board for the rest. Printed, skipped, recorded as declined. Peyk's own record is the
   closing act, not something owed yet.
4. **reinstall** — `tools/install.ps1`, then `conductor version`. Cannot happen while a conductor is
   live; re-check the process table at the moment of typing, because another repository's run may
   have started (trap 3). **This is also what delivers bug #100's fix to the real courier.**
5. **publish** — `git push origin master` and `git push origin v0.6.0`. The merge and the tag are
   LOCAL; nothing this engine did has left the machine.

### What the owner types, in order, once this run has ended

```
git checkout feat/peyk-courier && git status --porcelain     # must be empty
conductor release preflight --tag 0.6.0                      # merge/changelog/docs/processes should clear
conductor release perform  --tag 0.6.0                       # rehearse once more on the clean tree
conductor release perform  --tag 0.6.0 --yes                 # see the docmove warning below
git push origin master
git push origin v0.6.0                                       # this starts the release build
tools/install.ps1                                            # delivers bug #100's fix to the real courier
conductor version                                            # must read 0.6.0
```

**Before the `--yes` line:** `docmove` runs last and will move the plan doc (section 5). Either
repoint plan B by hand in the same commit, or leave the plan doc where it is and let the act refuse —
a dirty tree refuses it — and take the era-close without the doc move, which is what this checkpoint
records as the correct state. **Do not perform docmove while plan B has not launched.**

**Not the owner's, and not owed:** the branch's own CI. The Courier complexity budget is fixed in
`4955e9f`; that is the only CI-only ratchet this branch ever failed.

## 7. What this checkpoint did NOT do, deliberately

- did not run `release perform --yes` (refused by design under a live run — and correctly so)
- did not rename the CHANGELOG heading (it is the mechanical act's, section 3)
- did not move the plan doc or the tracker (section 5)
- did not back-fill GitHub (declined, section 6)
- did not touch the real courier, its home, its token or its task (trap 4); it is still on the old
  binary, pid 20860
- did not open the live store for write; every figure came from a `.backup` copy (section 1)
