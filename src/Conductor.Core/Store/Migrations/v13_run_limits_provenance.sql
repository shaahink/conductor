-- v13 (KS1.1): the limits a run LAUNCHED under, kept apart from the limits it is running under NOW.
--
-- v11 added `runs.limits_json` and the upsert in InitializeRun immediately gave it two meanings. The
-- column is refreshed on every process start, so on a resumed run it says what the RESUME loaded; and
-- a live plan reload -- the one moment limits actually change mid-run, and the reason K3.3 was written
-- at all -- did not touch the row at all. The column was therefore neither the launch value nor the
-- current one, with nothing on the row to say which of the two a reader was holding.
--
-- So the two values get two columns. `limits_json` keeps its v11 name and becomes honestly the value
-- NOW (the reload writes it at the session boundary), and the launch value moves into a column that
-- nothing but the very first INSERT of the row ever writes -- not a resume, not a reload.
ALTER TABLE runs ADD COLUMN limits_json_at_launch TEXT;

-- Provenance for the "now" value, recorded at the boundary rather than inferred from a diff: how many
-- reloads this run has APPLIED, and when the last one landed. A reload that is skipped (no plan file,
-- or a file that does not parse) applies nothing and so writes nothing -- the count is the number of
-- swaps that really happened, which is exactly what a diff of the two snapshots cannot tell you: two
-- reloads back to the original value read as "unchanged" and are still two reloads.
ALTER TABLE runs ADD COLUMN limits_reload_count INTEGER NOT NULL DEFAULT 0;
ALTER TABLE runs ADD COLUMN limits_reloaded_utc TEXT;

-- Deliberately NOT backfilled from limits_json. On a row this engine did not create, that column is
-- the LAST process start's value, and copying it into the launch column would be inventing provenance
-- for a launch nobody recorded. Absent reads as unrecorded, which is the truth, and `history` then
-- prints the single line it has always printed.
