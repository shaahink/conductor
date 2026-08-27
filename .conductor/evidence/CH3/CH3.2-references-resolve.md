# CH3.2 - every reference resolved against the disk, and the rule for the ones that are a record

Measured 2026-08-27, session 4, on `feat/charkh` at `ea75bda`.

## The rule, decided once

**A path is rewritten if and only if something still READS it.**

| Zone | What it covers | On a move |
|---|---|---|
| **Live** | the plan in flight with its tracker, contracts and templates; every document that plan's `readOrder` names; the published surface (`README.md`, `docs/*.md`, `ARCHITECTURE.md`, `AGENTS.md`, `CONTRIBUTING.md`); `docs/dev/README.md` and `docs/dev/NEXT-FEATURES.md`; a path a test prints in a **failure message** | repointed in the same checkpoint as the move |
| **Record** | a closed era's plan, contracts and tracker; everything under `docs/history/`, `ci-health/` and `.conductor/`; every ADR, finding, field note, closed-era brief, workgraph runbook and template under `docs/dev/` | **reported, never rewritten** |

An ADR states a decision as it was made and a finding states what was measured on a date. Bringing
either "up to date" does not fix it, it falsifies it. What makes leaving the record alone *safe* is
the bridge: `docs/dev/README.md` now carries a **where-it-went table**, so a reader who lands on a
stale path resolves it in one hop instead of grepping. The table is derived, not hand-kept -
`python tools/ch3/link-sweep.py --redirects` rebuilds it from the moves themselves.

Written into `docs/dev/README.md` under "When a file moves", as the checkpoint asks.

## The sweep

`python tools/ch3/link-sweep.py [--all] [--zone live|frozen] [--redirects]`

Four kinds of reference, because they rot independently:

| kind | what it reads |
|---|---|
| `markdown` | `[text](target)` and `[text]: target` in every `.md` |
| `prose` | a backticked path in any `.md` - the form the traps, the plans' notes and this repo's own briefs are written in |
| `contract` / `plan` | every **string value** of `.conductor/contracts/*.json` and `plans/**/*.plan.json`. A plan's `notes` is one long prose string, so the paths in it are not quote-delimited and matching on quotes finds none of them |
| `message` | a path inside a **sentence** in a `*Tests.cs` / `*_test.go` file - what a failing assertion prints at the next reader |

Result, committed beside this file as `CH3.2-link-sweep.txt`:

```
== LIVE - a broken reference here is a defect == 0 broken
== FROZEN - the run's own record; REPORTED, never rewritten == 431 broken
== counts == checked=3185 resolved=2751 excused=3 live-broken=0 frozen-broken=431
exit 0
```

**431 broken references inside the record were found and not one was edited.** They are listed in
full in the artifact - `.conductor/handovers/F1.md` (29), `docs/history/archive/conductor-DEBT.md`
(29), `docs/history/workflows/conductor-era3-workflow.md` (26), `docs/history/baton/audits/B0-baseline.md`
(24) and 100-odd others. `git status` after this checkpoint touches `AGENTS.md`,
`docs/dev/README.md` and `tools/ch3/` and nothing else.

## What was actually broken in the live zone

One, after the false positives were driven out by measurement rather than by judgement:

- **`AGENTS.md:295`** cited `Core/Http/ControlPlaneServer.cs`. `ControlPlaneServer.cs` is in the
  **shell**, at `src/Conductor/Http/ControlPlaneServer.cs` - the `Core/` prefix names the wrong
  assembly, on the line that tells a contributor which assembly owns the read surface. Repointed.

Three references are quoted rather than followed and are excused by name in
`tools/ch3/sweep-ignore.txt`, each with a one-line reason the sweep prints back:

- `docs/dev/CHARKH-PLAN-2026-08-26.md:111-112` cites the two moved briefs at their **old** paths.
  That is the sentence's subject - it names `docs/history/` as their new home in the next clause -
  so repointing it would delete the meaning. This is the very case the checkpoint asks about, and
  the answer is that the brief is right and the tool needed a way to say so.
- `docs/dev/README.md` - the left column of a where-it-went table is old paths by construction.

## The false positives, and what each one taught

The sweep started at **1270** broken references and ended at 431-in-the-record / 0-live. Every step
down was a rule about what a path *is*, not a name added to a list:

| Was reported | Why it was not a defect |
|---|---|
| `.conductor/run.db`, `.conductor/REPORT.md` | resolved against the citing file's directory. Only `./` and `../` are relative; `.conductor/x` is repo-rooted |
| `.conductor/control.json` (`docs/operating.md:93`) | **real** - `CtlCommand.cs:34` writes it, and it exists only while an intent is queued. A `.conductor/` path in prose is an artifact the engine MAY write, and whether the docs name the right ones is `SF7_1DocsMatchRealityTests`'s question, not a link's |
| `Orchestration/RunLoop.cs`, `Tasks/TaskSplitDtos.cs`, `divan/core.plan.json` | people cite a file the way they SAY it. Every one is a real file's path **suffix**, so the tool indexes the repo by basename and matches on suffix - which cannot rot the way a list of roots would |
| `Core/Commands/ControlDispatcher.cs` | `Core/` is the house alias for the `Conductor.Core` assembly. One alias, applied by name |
| `logs/session-NNN.json`, `tests/.../X.cs`, `conductor-YYYYMMDD.json` | a shape, not a path |
| `~/.config/opencode/opencode.json` | home-relative, with the `~` outside the backtick |
| `src/Foo.cs` in `"M src/Foo.cs\nM src/Bar.cs"` | a path a test file mentions **more than once** is data the test moves through itself - once as the input it builds, once in the assertion that reads it back. A pointer at the next reader is written once |
| `ui/pager.go`, `pkg/ssh/ui.go` (`adr/0006`) | prior art in other projects, in an ADR - which the zone rule now classes as a record anyway |
| `examples/shamshir/…` | an example plan describes ANOTHER repository's layout on purpose; the tree is out of scope |

## Two bugs the measurement caught in the measurement

Recorded because "the tool said zero" is worth exactly as much as the tool.

1. **The contract scan was silently deleted mid-edit.** A block replacement that spanned the JSON
   scan removed it; the sweep still printed a confident `live-broken=0`. Caught by comparing
   per-kind counts against the previous run - `contract` had gone from 688 references to absent.
   Restored; the sweep now reads 3185 references where it read 2587.
2. **`\.cs` matched `Conductor.csproj`.** `src/Conductor/Conductor.csproj` was reported broken twice
   as `src/Conductor/Conductor.cs`, in `docs/troubleshooting.md:54` and `docs/dev/NEXT-FEATURES.md:50`.
   Both docs were right. Fixed with a boundary after the extension.

## Verification

- `python tools/ch3/link-sweep.py` - live-broken 0, exit 0. Artifact: `CH3.2-link-sweep.txt`.
- `python tools/ch3/link-sweep.py --redirects` - the derived table. Artifact: `CH3.2-where-it-went.txt`.
- `dotnet test Conductor.slnx --filter FullyQualifiedName~Docs` - **Passed! 45/45**, after the
  `docs/dev/README.md` edit that `SF7_1DocsMatchRealityTests.Karvansara` reads.
- `git status --short`: `AGENTS.md`, `docs/dev/README.md`, `tools/ch3/`. No file under
  `.conductor/`, `docs/history/`, `ci-health/` or a closed era's `plans/` directory was modified.
