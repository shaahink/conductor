-- v14 (KS9.2): the live mirror's cursor -- how far into a run's event log GitHub has been told.
--
-- The mirror is a RECONCILER, not a sink. It wakes at the boundaries the engine already treats as
-- boundaries, asks the store what has happened since the last row here, and pushes. So the one piece
-- of state it needs is a high-water mark, and it needs it to survive a process restart: a resumed run
-- that started from zero would re-diff the whole board on every boundary, and a resumed run that
-- started from "now" would leave the sessions it missed off the board forever.
--
-- Keyed by (run_id, repo) because the destination is part of the fact. The same run mirrored into a
-- scratch repository and then into a real one has told each of them a different amount, and one row
-- per run would make the second destination inherit the first's progress and skip everything it has
-- never been sent. This is exactly the shape KS9.2's own proof needs: the live rig pushes a run to a
-- scratch repo while the owner's real backfill (KS10.3) is a different destination entirely.
--
-- `seq` is the event seq of the LAST event in the last batch that was pushed WITHOUT errors. It is
-- written after the push, never before: a crash between the push and this write costs one repeated
-- reconcile pass, which is free because the push is idempotent, whereas the other order costs a
-- silently unmirrored batch that nothing will ever come back for.
CREATE TABLE IF NOT EXISTS github_cursor (
    run_id       TEXT    NOT NULL,
    repo         TEXT    NOT NULL,
    seq          INTEGER NOT NULL DEFAULT 0,
    updated_utc  TEXT    NOT NULL,
    -- Bookkeeping, for the operator rather than the algorithm: how many passes have landed and what
    -- the last one said. A mirror that has been failing all night is otherwise indistinguishable from
    -- one that has had nothing to say, because both leave `seq` where it was.
    passes       INTEGER NOT NULL DEFAULT 0,
    last_error   TEXT,
    PRIMARY KEY (run_id, repo)
);
