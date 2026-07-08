# examples/

Home for concrete, ready-to-run plan configurations that are NOT part of the Conductor engine.

Baton B1 relocates the Loom plan here:

- `examples/loom/loom.opencode.plan.json` — opencode/deepseek agent, audit on, perPhase gates
- `examples/loom/loom.plan.json` — claude agent variant
- `examples/loom/templates/` — the Loom prompt templates

Until B1.1 runs, the Loom plans still live at `plans/loom*.json` (do not move them by hand — B1.1 does
it with the path/reference updates and a dry-run gate).

`examples/shamshir/` holds the strict `TRACKER.md` template the owner copies into a Shamshir iteration
folder — it is the reference for the "one format most of the time" convention (BATON-BRIEF D-2).

> Note: `plans/` (repo root) is reserved for Conductor's OWN self-plan
> (`plans/conductor.self.plan.json`) and its `plans/templates/`. Project plans live under `examples/`.
