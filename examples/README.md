# examples/

Concrete plan configurations that are **not** part of the Conductor engine — worked examples to read
and copy from.

Every plan here is derived from a real run. The paths are placeholders: point `repo` at your own
absolute path and `tracker` / `planDoc` at your own files before driving one. `conductor init` writes
a simpler starting plan; these show what one grows into.

## `loom/` — a multi-gate plan for a dotnet + pnpm monorepo

The shape worth stealing: `perPhase` gates, so a fast tier runs every session and the full battery
only at phase end — plus **stage-scoped gates**, so the UI check does not run during backend phases
and the eval suite runs only on the stages that can break it.

- `loom.opencode.plan.json` — opencode/deepseek agent, audit on, perPhase gates
- `loom.plan.json` — the same plan with a claude agent
- `templates/` — the prompt templates it uses

Derived from a real private project; identifiers are genericised (`App.slnx`, `src/App.Web`) and
`repo` is a placeholder. `tests/Conductor.Tests/PlanConfigTests.cs` parses the opencode variant to
pin the stage-scoping behaviour, so the gate **names** and their `stages` lists are load-bearing —
change the commands freely, but keep `pnpm-check` and `mcp-qa` scoped as they are.

## `shamshir/` — a strict tracker and a non-default id scheme

`parity-pipeline.TRACKER.md` is the strict `TRACKER.md` format copied into each iteration folder —
the reference for the "one format most of the time" convention (BATON-BRIEF D-2) — plus the plan that
drives it. Its `repo` is `.`, so it is the one plan here that runs where it sits.

> **Misplaced, on purpose.** `plans/shamshir-p0.plan.json` belongs here by the rule below, and is the
> worked example of `conventions.stageIdPattern` — overriding the default so a `P-0` / `P3.4b` style
> tracker parses. W6.4 tried to move it and the ratchet gate refused: relocating a plan file reads to
> `tools/gates/ratchet.ps1` as removing its gate commands, which is a decision the gate reserves for
> the owner. Left in place rather than worked around. See the W6.4 row in `CONDUCTOR-WORKGRAPH.md`.

## Where plans live

`plans/` (repo root) is reserved for Conductor's OWN plans — it drives itself, and those are the
plans it drives. Project plans live here.
