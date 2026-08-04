# Karvan — the era bundle

Everything this era needs, in one folder, the same shape the `ci-health` bundle uses.

| file | what it is |
|---|---|
| `core.plan.json` | Plan 1 of 2 — the engine knows what it did and what it cost. **Launch this one first.** |
| `CORE-TRACKER.md` | Plan 1's checkpoint table and handoff block. The engine parses this; it is not decoration. |
| `lanes.plan.json` | Plan 2 of 2 — two client sites, or two pages of one site, at the same time. Authored, not launch-ready. |
| `LANES-TRACKER.md` | Plan 2's checkpoint table. |
| `templates/session.md` | The deliver-session prompt. No QA-of-the-previous-session step, no pre-session battery. |
| `templates/fix.md` | The repair-session prompt, used when a gate battery comes back red. |

**The spec is `docs/history/CONDUCTOR-KARVAN.md`** — Part I is the core plan, Part II the lanes plan,
Appendix C is the launch drill, Appendix D the traps. Every stage's `notes` field points at its section.

**The research it was written from is `docs/dev/NEXT-ERA-FINDINGS-2026-08-04.md`** — the measurements,
the code citations, and the survey of what the rest of the 2026 orchestrator ecosystem does.

## Order

1. Reinstall the engine from a clean tree (the drill's step 1 — as of authoring, the engine on PATH is
   a dirty build from a side branch).
   **DEFERRED at launch, 2026-08-04, deliberately.** A second conductor run (`C:/Code/sk-studio`) was
   live on this machine and drives from the same published `conductor.exe`. Publishing over it would
   have failed on the file lock at best and broken a running engine at worst. So this era launched on
   `0.3.1-alpha.0.6+98a426af63d6.dirty` — the same build that drove the Sarban face era. It carries
   `hook-budget`, so the cooperative soft-break rail does reach the agent. **FU-OWNER-14 is still
   owed**, and K7.2 is where it gets paid: that checkpoint's reinstall is the first of this run.
2. `git switch -c feat/karvan` **from master** (master already carries the Sarban merge and four
   commits beyond `feat/sarban`), commit this bundle. The plan's `branchPattern` is `^feat/karvan$`,
   and a branch mismatch is currently only a **warning** — the run will proceed on the wrong branch.
3. `conductor doctor -p plans/karvan/core.plan.json` — 0 fail, and the `work` line must read
   `25 work item(s) cover all 7 stage(s)`.
4. `conductor journey -p plans/karvan/core.plan.json` — the Model column must not read `(default)`.
5. `conductor run -p plans/karvan/core.plan.json --dry-run` — read the prompt; it must not contain the
   QA-the-previous-session ritual (if it does, `templatesDir` did not resolve).
6. `--once` supervised, then detached with **stderr redirected**.

Plan 2 launches only after plan 1 completes and K7.1 has re-measured the token budget. Its `limits`
block carries plan 1's numbers until then.

## Editing these files

`conductor plan set` round-trips the JSON through the serialiser and **strips every comment**. Both
plan files are annotated; if you must edit live, expect to restore the comments, or edit the file by
hand and `conductor plan reload` instead (a `plan set` alone never reaches a running engine).

A literal brace token in anything under `templates/` **kills the engine** — the refusal goes to stderr
only. Sweep after every template edit; `K5.1` edits `session.md` on purpose.
