# CH5.2 — the era closed through CH4's machinery

**Session 9, 2026-08-27.** Everything below was driven from the **fresh build**
(`dotnet run --project src/Conductor --no-build -- release …`), never through the `conductor` on
PATH — that copy is the published 0.5.0 engine driving this run and it does not carry these verbs.
Nothing was merged, tagged, moved, installed, pushed or backfilled.

| artifact | what it is |
| --- | --- |
| `ch5-2-preflight.txt` | `release preflight`, verbatim, exit 1 |
| `ch5-2-perform-dryrun.txt` | `release perform` with no `--tag` and no `--yes`, verbatim, exit 1 |
| `ch5-2-charkh-runbook.md` | `release runbook` — the era-close as it stands, unnamed release |
| `ch5-2-runbook-tag-rehearsal.md` | `release runbook --tag 0.6.0` — the **rehearsal**; `0.6.0` is the example value `docs/cli.md` uses, not a decision |
| `ch5-2-bug88-ab.txt` | the bug #88 A/B — `release perform --tag 0.6.0` dry run against the same scratch rig twice, `CHANGELOG.md` the only difference |

---

## 1. The one act a session could perform, performed

**Bug #88 — the release notes exist before the tag can.** `CHANGELOG.md`'s `[Unreleased]` section
had said *"Nothing yet — entries for the next era go here"* since v0.5.0 shipped, through five
stages. That body is not decoration: `release.yml` runs `tools/changelog-section.sh` as the first
job of a tag build and uses its stdout **verbatim** as the release body, so tagging over it
publishes the scaffold's own apology to the world as the notes for an era.

It is also, by design, what blocked this era's close. The four mechanical acts are ordered —
changelog, merge, tag, docmove — and the sequence stops at the first refusal, so until the section
said something the engine would perform **nothing at all**:

- `ReleasePerform.Changelog` refuses over a placeholder body — `src/Conductor.Core/Release/ReleasePerform.cs:99-103`
- what counts as a placeholder — `src/Conductor.Core/Release/ReleasePreflight.cs:177-183`: four or
  fewer non-blank lines, **or** a body naming `Nothing yet`

Written in `35043fe` from the commit range `master..feat/charkh` (32 commits), each `feat`/`fix`
read for what it changed — not from memory. 108 non-blank lines where there were 2.

**Proven by the machine, not by reading — a real A/B, not an inference.** CH4.4's runbook was
generated *without* a tag, so its changelog refusal names the missing version and never reaches the
placeholder gate; comparing it to a tagged run would be comparing two different questions. So the
before-state was measured directly, in a scratch rig (`%TEMP%/ch52-changelog-ab`) with its own git
repo, its own state dir and the `github`/`telegram` blocks stripped, running the fresh build's
`release perform --tag 0.6.0` as a **dry run** twice. Same plan, same tag, same command; the only
thing that differs between the two runs is `CHANGELOG.md`. Verbatim in `ch5-2-bug88-ab.txt`:

| `CHANGELOG.md` | changelog act | the act after it |
| --- | --- | --- |
| **A** — at `35043fe^` (placeholder) | `✗ changelog … the [Unreleased] section is a placeholder (2 non-blank line(s))` | `✗ merge … not attempted - an earlier act refused or failed` |
| **B** — at `35043fe` (fixed) | `→ changelog … rename '## [Unreleased]' to '## [0.6.0] - 2026-08-27'` | `✗ merge … the preflight's merge line is not green: no such branch` |

In **B** the refusal has moved *past* the changelog and down to `merge`, for a reason that belongs
to the empty scratch repo rather than to the changelog. In this repository the merge line is green,
and the tagged rehearsal (`ch5-2-runbook-tag-rehearsal.md`) shows all four mechanical acts
**will run**: *"rename '## [Unreleased]' to '## [0.6.0] - 2026-08-27' over 106 lines"*.

And with it the whole sequence unblocks. The acts verdict moves from

> **STOPPED** — 4 act(s) refused or failed: changelog, merge, tag, docmove; would perform nothing.

to

> **OWNER** — would perform: changelog, merge, tag, docmove. 5 act(s) are yours and were stopped
> at: version, split, corpus, reinstall, publish  (`release perform` exits 2)

which is the exit code the runbook's own header calls *"what a finished era-close looks like"*.
The heading was **not** renamed here: the version number is the owner's input to that act.

## 2. What the preflight measured, and what moved since CH4.4

`release preflight` (no tag) — **NOT READY: 2 of 6 red: processes, courier; 2 waiting on the owner:
changelog, backfill**, exit 1.

| check | CH4.4 (03:09Z) | CH5.2 | why it moved |
| --- | --- | --- | --- |
| `merge` | **RED** — 33 ahead, tree not clean | **GREEN** — fast-forward, 33 ahead, 0 behind | the tree was committed |
| `changelog` | YOURS — no version named | YOURS — no version named | unchanged; with `--tag` it now measures the section instead |
| `processes` | **RED** — 4 reasons | **RED** — 3 reasons | pid 33884 (the courier) is gone; `CONDUCTOR_PID` 5248 is still here and always will be mid-run |
| `migration` | GREEN — tree v15 = installed v15 | GREEN — same | checked before any fresh-build verb touched the store (trap 18) |
| `courier` | GREEN — running pid 33884 | **RED** — *"the task is registered but nothing is polling for this machine"* | **the real courier died** — see §4 |
| `backfill` | YOURS — 3 runs with no record | YOURS — same 3 runs | not a session's call |

`processes` and `courier` are the two red lines, and **neither is a session's to clear**: the live
engine is the run asking the question, and the courier is the owner's daemon holding the real bot
token (trap 4).

## 3. What was refused, and by whom

`release perform` — no `--tag`, no `--yes` — refused **before planning anything**:

```
refusing: a conductor run is live in C:/code/conductor\.conductor (engine pid 5248).
this verb rewrites the CHANGELOG, moves the plan's tracker and repoints the plan itself —
doing that under a live session pulls the ground out from under it. Let the run end first.
```

That is the correct outcome and the design working: **the four mechanical acts cannot be performed
from inside the run they are closing.** They are not a session's to perform, and they are not
parked because a session declined — they are parked because the engine refuses. The five owner acts
below it are refused for a different and older reason: they are judgement.

**Everything still standing, from the rehearsal, in order:**

1. `conductor release perform --tag <x.y.z> --yes` — changelog, merge, tag, docmove, after the run ends
2. **version** — MinVer derives a build id, not a release name
3. **split** — one release or two is a call about what the world reads
4. **corpus** — 3 finished runs have no GitHub record; run each backfill **once** (bug #79)
5. **reinstall** — `tools/install.ps1`, only once no conductor is live
6. **publish** — `git push origin master` and `git push origin v<x.y.z>`

## 4. What the machinery got wrong — and one thing it got right

Four findings, all filed with their measured output. Three are defects in the machinery; the first
is a live outage the machinery **caught**.

- **#93 (high) — the real courier died with exit 1 and did not come back, and nothing anywhere says
  why.** Found *by* the preflight's courier check going red. Cross-checked three ways: `conductor
  courier status` says *"running: no"*; `Get-ScheduledTaskInfo "Conductor Courier"` says State
  `Ready`, `LastRunTime 26/08/2026 23:04:43` (= the 22:04:43Z start the 03:09Z runbook saw),
  **`LastTaskResult 1`**; and the task *is* configured to restart — `RestartCount 99`,
  `RestartInterval PT1M` — yet `LastRunTime` never advanced. It cannot be diagnosed: the courier
  home holds exactly `courier.json` and `courier.secret`, no log and no stdout capture, and
  `Microsoft-Windows-TaskScheduler/Operational` is disabled on this machine. Telegram discards an
  undelivered update after 24 hours. **Not restarted from here** — trap 4 forbids a session running
  `courier run`/`restart` against the real courier home. *This is the preflight earning its keep:
  an era-close check found an outage nobody had noticed.*
- **#94 (medium) — `release perform` refuses its own dry run.**
  `src/Conductor/Commands/ReleaseCommand.Perform.cs:44` computes `var dryRun = !settings.Yes;` and
  the live-holder refusal at `:47-54` never consults it, returning 1 before `RunActsAsync` at `:62`.
  The stated reason — *"this verb rewrites the CHANGELOG, moves the plan's tracker…"* — is untrue of
  a dry run, which writes nothing. Section 2 of **every runbook this engine generates** says *"Drop
  `--yes` to rehearse it"*, an instruction that cannot work in the situation the document was
  generated in. `release runbook` is the working substitute and nothing at the point of refusal
  says so.
- **#95 (medium) — the era-close has an act nobody taught it.** Eight rows across `docs/cli.md`
  (150, 151, 152, 356) and `docs/operating.md` (76, 77, 78, 148) read *"New since `v0.5.0`; not in
  the released binary yet"*. Every one becomes **false** the instant the tag lands, and that caveat
  is why `tools/ch3/docs-surface-diff.py` exits 1 against the installed binary by design. Grepping
  the generated runbook for `cli.md`, `operating.md` or `released binary` returns **nothing** — not
  a precondition, not a mechanical act, not one of the five owner acts. This is CH4.4's own thesis
  failing in CH4.4's own output: *a document cannot notice an act it never mentioned*. It is
  derivable, not judgement — the phrase is a fixed string and the version it names is the tag being
  cut.
- **#96 (low) — the changelog check's remedy contradicts the changelog act.** With `--tag 0.6.0`
  the check is red and advises *"rename the heading to '## [0.6.0] - <date>' and re-run"*
  (`ReleasePreflight.cs:152`) while, in the same document, the act says it will do exactly that
  mechanically. Red-before/green-after is correct — preflight measures the post-rename state — but
  the remedy text should name `release perform --tag <x.y.z>`. CH4.1 made the preflight's merge
  verdict the *only* opinion the merge act consults so two verbs could not disagree; the changelog
  pair never got the same treatment.

## 5. What this checkpoint deliberately did not do

No tag, no merge, no push, no reinstall, no backfill, no `--yes`, and no courier restart. The
version number and the published corpus are the owner's; the mechanical acts are the engine's and
it will not perform them while this run is live. A session pre-flights and parks.
