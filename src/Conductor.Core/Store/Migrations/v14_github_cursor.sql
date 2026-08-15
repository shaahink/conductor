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

-- The local map: what this run has already created on that destination, and where.
--
-- MEASURED on the live rig, against the real API, in one process: the REST issues LIST endpoint is
-- EVENTUALLY CONSISTENT. A pass created four issues; a second pass two seconds later listed the
-- repository, saw none of them, and created four more. Eight issues, two complete copies of the same
-- board. (The rig's own `gh api` call, a different client with a different token, agreed with the
-- stale view -- so this is GitHub's replica lag, not a cache in the engine.) KS9.1's two live passes
-- only looked idempotent because they were minutes apart.
--
-- So identity may not depend on asking GitHub what exists. The marker in the issue body stays the
-- identity a HUMAN can read and the thing that lets a lost map be rebuilt, but the authority on "have
-- I already made this" is local: one row per thing this run has put there. That is also what D-7 /
-- A16 / ADR 0005 asked for -- decide from the fold and the local map, never from what GitHub says --
-- for an entirely different reason.
--
-- `key` is the task id for a card, `run:<run id>` for the diary issue, and the session marker key for
-- a diary comment. `kind` separates them so a comment key can never collide with a task id.
CREATE TABLE IF NOT EXISTS github_map (
    run_id       TEXT    NOT NULL,
    repo         TEXT    NOT NULL,
    key          TEXT    NOT NULL,
    kind         TEXT    NOT NULL,
    issue_number INTEGER NOT NULL,
    created_utc  TEXT    NOT NULL,
    PRIMARY KEY (run_id, repo, kind, key)
);
