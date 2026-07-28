-- v8: W1.1 — one work graph. The event-sourced task family (events table) is the single
-- runtime truth for work items; checkpoint state is a fold of that log (GetCheckpoints).
-- The mutable checkpoints table (v2/v6 — the ADR-0002 violation) is dropped. In-flight runs
-- recover their checkpoint state at next start: SeedCheckpoints re-emits it from the tracker
-- view, which carried every column of this table.

DROP TABLE IF EXISTS checkpoints;
