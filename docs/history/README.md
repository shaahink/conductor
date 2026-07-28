# History — receipts, not documentation

**Nothing in this directory is a guide.** If you are trying to *use* Conductor, go back to
[`docs/README.md`](../README.md); if you are trying to *change* it, go to [`docs/dev/`](../dev/).

Conductor was built in named eras — B (Baton), M (Maestro), F (Foreman/v-next), G (AI-native),
P (Planner), U (UX), W (Work graph). Each left behind a design brief, per-stage notes, and gate
evidence. That material is kept deliberately, but it is kept the way receipts are kept: it exists so
a past "gates green" can be **audited** rather than believed. It is verbose, it quotes absolute paths
from the machine that produced it, and nothing reads it automatically.

The current era's brief lives in [`docs/dev/`](../dev/), not here. A brief moves into this directory
when its era closes.

## Era briefs

| Era | Brief | Alongside it |
|---|---|---|
| **B — Baton** (v2: the engine's foundations) | [`baton/BATON-BRIEF.md`](baton/BATON-BRIEF.md) | [`baton/stages/`](baton/stages/) per-stage briefs · [`baton/evidence/`](baton/evidence/) gate transcripts · [`baton/audits/`](baton/audits/) · [`baton/tooling/`](baton/tooling/) B0 reference drafts (not active build files) · [`baton/CONDUCTOR-NEXT.md`](baton/CONDUCTOR-NEXT.md) |
| **M — Maestro** (v4) | [`MAESTRO-PLAN.md`](MAESTRO-PLAN.md) | [`maestro/`](maestro/) delivery plan, pre-session notes, final audit |
| **F — Foreman / v-next** | [`CONDUCTOR-VNEXT-PLAN.md`](CONDUCTOR-VNEXT-PLAN.md) | — |
| **G — AI-native** | [`CONDUCTOR-AI-NATIVE.md`](CONDUCTOR-AI-NATIVE.md) | — |
| **P — Planner** | [`CONDUCTOR-PLANNER.md`](CONDUCTOR-PLANNER.md) | — |
| **U — UX** (the Go face) | [`CONDUCTOR-UX.md`](CONDUCTOR-UX.md) | [`../../face-go/STYLE.md`](../../face-go/STYLE.md) is the *live* keybinding + layout reference — that one is current, not history |
| **Era 3** | — | [`era3/evidence/`](era3/evidence/) gate transcripts |

## Other archives

| Path | What it is |
|---|---|
| [`archive/trackers/`](archive/trackers/) | The checkpoint tables each era was driven against. Only the live tracker stays at the repo root. |
| [`qa-reports/`](qa-reports/) | Whole-project QA sweeps. |
| [`workflows/`](workflows/) | Session workflows used to drive specific eras. |

## Why the evidence directories are so large

`baton/evidence/` and `era3/evidence/` hold raw gate transcripts — `dotnet build` / `dotnet test`
output captured at the commit a checkpoint claimed. They are the single biggest thing in `docs/`,
and they are the point: this project's whole thesis is that a claim is worth nothing without an
independent check, and these are the checks, preserved.

`MAESTRO-PLAN.md` has one live duty despite being history: it is the fixture for the plan-import
truth gate (`tests/Conductor.Tests/MarkdownPlanImportTests.cs`), which asserts that importing it
yields stages M1…M9. Moving or editing it will fail the suite.
