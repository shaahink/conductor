-- v5: Add events table (append-only event spine, replaces events.jsonl)
--     and run_state table (orchestrator mutable state, replaces state.json)

CREATE TABLE IF NOT EXISTS events (
    seq         INTEGER NOT NULL,
    ts          TEXT NOT NULL,
    run_id      TEXT NOT NULL,
    session_id  TEXT,
    type        TEXT NOT NULL,
    payload     TEXT NOT NULL,
    PRIMARY KEY (seq, run_id)
);

CREATE TABLE IF NOT EXISTS run_state (
    run_id      TEXT PRIMARY KEY,
    plan_name   TEXT NOT NULL,
    state_json  TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES runs(run_id)
);
