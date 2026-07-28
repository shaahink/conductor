-- v3: Add pids table (process tracking for orphan reaper + liveness, F2.2)

CREATE TABLE IF NOT EXISTS pids (
    pid             INTEGER NOT NULL,
    purpose         TEXT NOT NULL,
    stage_id        TEXT,
    session_number  INTEGER,
    started_utc     TEXT NOT NULL,
    exited_utc      TEXT,
    exit_code       INTEGER,
    run_id          TEXT NOT NULL
);
