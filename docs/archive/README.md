# Archive

Finished eras. Nothing here is current, and nothing here should be edited — these files are kept
because they are the record a claim can be checked against, not because they still describe the
system.

The live tracker is [`CONDUCTOR-WORKGRAPH.md`](../../CONDUCTOR-WORKGRAPH.md) at the repo root, and
its design authority is [`docs/CONDUCTOR-WORKGRAPH.md`](../CONDUCTOR-WORKGRAPH.md). Start at
[`docs/README.md`](../README.md).

## `trackers/` — the checkpoint tables each era was driven against

Each is a handoff block plus a checkpoint table with `Status | Commit | Evidence` per row. The
commits are real and still in this repo's history, so any row can be re-read against the diff that
claims to satisfy it.

| Tracker | Era | Design authority |
|---|---|---|
| [`CONDUCTOR-START.md`](trackers/CONDUCTOR-START.md) | **B — Baton** (v2 foundations; 67 checkpoints) | [`docs/baton/BATON-BRIEF.md`](../baton/BATON-BRIEF.md) |
| [`MAESTRO-TRACKER.md`](trackers/MAESTRO-TRACKER.md) | **M — Maestro** (v4) | [`docs/MAESTRO-PLAN.md`](../MAESTRO-PLAN.md) |
| [`CONDUCTOR-ERA3-START.md`](trackers/CONDUCTOR-ERA3-START.md) | **Era 3** | — |
| [`CONDUCTOR-VNEXT-PLAN.md`](trackers/CONDUCTOR-VNEXT-PLAN.md) | **F — Foreman / v-next** | [`docs/CONDUCTOR-VNEXT-PLAN.md`](../CONDUCTOR-VNEXT-PLAN.md) |
| [`CONDUCTOR-AI-NATIVE.md`](trackers/CONDUCTOR-AI-NATIVE.md) | **G — AI-native** | [`docs/CONDUCTOR-AI-NATIVE.md`](../CONDUCTOR-AI-NATIVE.md) |
| [`CONDUCTOR-PLANNER.md`](trackers/CONDUCTOR-PLANNER.md) | **P — Dynamic planner** | [`docs/CONDUCTOR-PLANNER.md`](../CONDUCTOR-PLANNER.md) |
| [`CONDUCTOR-UX-START.md`](trackers/CONDUCTOR-UX-START.md) | **U — UX** (the Go face) | [`docs/CONDUCTOR-UX.md`](../CONDUCTOR-UX.md) |

The matching plan files are still under [`plans/`](../../plans/) and still point here, so an old era
can be re-read (or re-driven against a fixture repo) without reconstructing anything.

## Loose historical documents

| File | What it is |
|---|---|
| [`NEXT-ERA.md`](NEXT-ERA.md) | The strategic roadmap written after Baton closed, which became Era 3. |
| [`FUSION.md`](FUSION.md) | A cross-project "finish everything" mega-plan spanning Conductor and a separate private repo. Kept for the multi-repo planning shape; the other repo is not public. |
| [`conductor-DEBT.md`](conductor-DEBT.md) | The B-era debt-and-followups ledger. Superseded in flight by `.conductor/followups.md`, which the fix lanes read directly (B12.4). |
