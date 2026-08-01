# The prompt bank

Everything under `plans/` that shapes what an agent is told: the plan files themselves, the
**personas** (who the agent is), the **packs** (what it should already know about this domain), and
the per-era **template sets** (the shape of the session prompt). This page is the index — it exists
so the bank is *choosable* rather than archaeological, and so nobody has to open nine files to find
out which one they want.

`SF6_2PromptBankTests` asserts this page against the filesystem in both directions: every persona and
pack file on disk has a row here, every row here names a file that exists, and every stated size is
within 10% of the real one. So the index cannot rot into a lie — but it does mean **if you edit a
bank file, update its row.**

## How a name resolves

| Kind | Declared as | Resolution order |
|---|---|---|
| persona | `"persona": "qa"` on a stage, checkpoint, or role rule | `<planDir>/personas/<name>.md` → built-in (`deliver`, `verify`, `advise` only) → nothing |
| pack | `"packs": ["dotnet-engineer"]` at plan level | `<planDir>/<templatesDir>/packs/<name>.md` → `<planDir>/packs/<name>.md` → skipped silently |
| template | `"templatesDir": "sarban-templates"` at plan level | `<planDir>/<templatesDir>/<file>.md` → `<planDir>/<file>.md` → the built-in C# template |

Personas were always plan-wide; packs were era-scoped until SF6.2 and are now era-first,
shared-second. A pack you want every plan to be able to choose belongs in `plans/packs/`. Note the
last column of the pack row: a name that does not resolve is skipped **without an error**, so a typo
in `"packs"` costs you the whole pack and says nothing.

## The prompt budget — read before adding anything

A composed session prompt is `persona + template + tools contract + packs + stage notes`. Measured
at SF6.1, the built-in deliver template alone renders **~7,900 chars**, against the **~8,191 char
`cmd.exe` argv ceiling** (bug #15) that applies to every agent launched through `cmd /c`. Past that
ceiling the child silently never runs and the run reports success.

That budget is already spent. **Both packs together are 5,861 chars** — so
`conductor-maestro.plan.json`, which declares both, composes roughly 14.5k chars and cannot run under
a `cmd.exe` agent at all (bug #21). Nothing warns you about this today. The sizes in the tables below
are there so you can add up your own plan's total before you find out the expensive way. Adding to
the bank means finding the chars somewhere, not hoping.

## Personas

One paragraph, prepended to the session prompt. Says who the agent is and what it optimises for —
never what Conductor's own contract already says (claim with evidence, never weaken a gate, `HUMAN:`
when blocked, long commands under `conductor bg`). That block is rendered right after the persona and
wins; repeating it in a persona costs chars and teaches nothing.

| File | Chars | What it is for — choose it when |
|---|---|---|
| `personas/architect.md` | 706 | Designing or re-shaping structure: layer boundaries, contracts, schemas, trust model. Use on a stage that establishes an interface others will build against, not one that implements it. |
| `personas/docs.md` | 897 | Writing for a human reader: ADRs, stage docs, API references, handover notes. Carries the **unblocks voice** — the shape the owner's queue and any human-facing list must take. |
| `personas/git-cleanup.md` | 590 | History and working-tree hygiene: squashing WIP, commit messages, conflict resolution, stale branches, `.gitignore`. A narrow janitorial stage, not a delivery one. |
| `personas/planner.md` | 1006 | Decomposing ambiguous work into verifiable checkpoints. Carries the **owner-block alternate completion** rule: any checkpoint gated on the owner gets a second completion a session can reach alone. |
| `personas/qa.md` | 605 | Independently checking that work matches its claims — re-run the gate, read the evidence, reproduce the failure. The verification counterpart to `reviewer`. |
| `personas/refactor.md` | 565 | Changing structure without changing behaviour. Insists on green-before, small commits, and never mixing a refactor with a feature. |
| `personas/reviewer.md` | 805 | Auditing committed work for correctness and safety. Reads what the code *does* over what its comment claims, and treats a green suite as no evidence the feature ever executed. |
| `personas/security-audit.md` | 845 | Finding and fixing vulnerabilities, trust boundary first so the findings are the reachable ones. |
| `personas/test-writer.md` | 641 | Writing high-signal, low-brittleness tests: deterministic, one thing per test, mocked only at architectural boundaries. |

`deliver`, `verify` and `advise` are the three roles the assignment policy hands out by default. They
have **no file here** — they resolve to short built-ins in `PersonaRegistry`. Dropping a
`personas/deliver.md` into this directory silently overrides the built-in for every plan in this
directory *and* adds its full length to every delivery prompt; given the budget above, do that
deliberately or not at all.

## Packs

Whole-document domain context, appended near the end of the prompt. Unlike a persona, a pack is not
about who the agent is — it is what it would otherwise have to learn by failing.

| File | Chars | What it is for — choose it when |
|---|---|---|
| `packs/agent-pitfalls.md` | 3642 | Any autonomous multi-session run. Every item is a real failure from this project: green-suite-proves-nothing, foreground long commands, killing by name, saving lessons for the handoff, hand-editing the tracker, faking a green, claiming without evidence, over-claiming in a handoff, sibling-only delivery judged NoProgress, sprawling diffs. |
| `packs/dotnet-engineer.md` | 2219 | .NET work in a repo with `TreatWarningsAsErrors` and a full analyzer ruleset — house C# style, the async rules, the comparer and pragma traps that fail this build. Not portable: it describes *this* codebase's bar. |

## Era template sets

The session/fix/audit/resume prompt shapes each mega-plan ran under. Kept because a plan file still
points at them and because they are the record of how the prompt evolved — the built-in C# templates
in `PromptBuilder` are the current default and the place to make a change that should apply
everywhere.

A file template replaces the built-in wholesale, so it also decides which placeholders exist at all.
`{packs}` is the one that bites: a template without it drops every pack the plan declared, silently.
Before SF6.2 only `maestro-templates/session.md` had it — which, with packs also being era-scoped,
meant the pack feature had exactly one working configuration in the whole bank, and that one was over
the argv ceiling. `sarban-templates` now renders packs; the two archived sets still do not.

| Directory | Used by | Contains | Renders `{packs}` |
|---|---|---|---|
| `sarban-templates/` | `conductor-sarban-core`, `conductor-sarban-face` | `session`, `fix` — the current era | yes |
| `baton-templates/` | `conductor.self`, `conductor-foreman` | `session`, `fix`, `audit`, `advisor`, `resume` | no |
| `maestro-templates/` | `conductor-maestro` | `session` (its packs moved to `plans/packs/` at SF6.2) | yes |
| `shamshir-templates/` | *(no plan in this repo)* | `session`, `fix`, `advisor`, `resume` — external round | no |

The persona needs no placeholder — `PromptBuilder` prepends it to whatever template it rendered, so a
persona works in every set above.
