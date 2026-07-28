-- v1: Initial schema — all core tables
-- Applied to fresh databases and as the baseline for migration from earlier versions.

CREATE TABLE IF NOT EXISTS schema_version (
    version INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS runs (
    run_id       TEXT PRIMARY KEY,
    plan_name    TEXT NOT NULL,
    repo         TEXT NOT NULL,
    branch       TEXT,
    driver_ver   TEXT,
    status       TEXT NOT NULL DEFAULT 'running',
    started_utc  TEXT NOT NULL,
    ended_utc    TEXT
);

CREATE TABLE IF NOT EXISTS stages (
    id            TEXT NOT NULL,
    run_id        TEXT NOT NULL,
    title         TEXT NOT NULL,
    status        TEXT NOT NULL DEFAULT 'pending',
    session_count INTEGER NOT NULL DEFAULT 0,
    started_utc   TEXT,
    confirmed_utc TEXT,
    PRIMARY KEY (id, run_id),
    FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

CREATE TABLE IF NOT EXISTS sessions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id          TEXT NOT NULL,
    stage_id        TEXT NOT NULL,
    number          INTEGER NOT NULL,
    kind            TEXT NOT NULL,
    started_utc     TEXT NOT NULL,
    ended_utc       TEXT,
    outcome         TEXT,
    agent_session_id TEXT,
    resume_count    INTEGER NOT NULL DEFAULT 0,
    attempt         INTEGER NOT NULL DEFAULT 0,
    gate_summary    TEXT,
    result_summary  TEXT,
    commit_count    INTEGER NOT NULL DEFAULT 0,
    newly_done      TEXT,
    FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

CREATE TABLE IF NOT EXISTS attempts (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id          TEXT NOT NULL,
    stage_id        TEXT NOT NULL,
    number          INTEGER NOT NULL,
    session_number  INTEGER NOT NULL,
    started_utc     TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

CREATE TABLE IF NOT EXISTS gates (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id          TEXT NOT NULL,
    session_number  INTEGER,
    stage_id        TEXT,
    name            TEXT NOT NULL,
    tier            TEXT NOT NULL DEFAULT 'full',
    scope           TEXT NOT NULL DEFAULT 'session',
    sha             TEXT,
    passed          INTEGER NOT NULL,
    skipped         INTEGER NOT NULL DEFAULT 0,
    optional        INTEGER NOT NULL DEFAULT 0,
    exit_code       INTEGER NOT NULL,
    duration_ms     INTEGER NOT NULL,
    tail            TEXT,
    FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

CREATE TABLE IF NOT EXISTS scores (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id          TEXT NOT NULL,
    session_number  INTEGER NOT NULL,
    stage_id        TEXT,
    score           INTEGER NOT NULL,
    verdict         TEXT,
    findings        TEXT,
    FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

CREATE TABLE IF NOT EXISTS ledger (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id          TEXT NOT NULL,
    session_number  INTEGER,
    stage_id        TEXT,
    kind            TEXT NOT NULL,
    content         TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

CREATE TABLE IF NOT EXISTS handovers (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id          TEXT NOT NULL,
    session_number  INTEGER NOT NULL,
    stage_id        TEXT NOT NULL,
    content         TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

CREATE TABLE IF NOT EXISTS injections (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id          TEXT NOT NULL,
    kind            TEXT NOT NULL,
    source_session  INTEGER,
    target_stage_id TEXT,
    content         TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

CREATE TABLE IF NOT EXISTS costs (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id          TEXT NOT NULL,
    session_number  INTEGER NOT NULL,
    category        TEXT NOT NULL,
    tokens_in       INTEGER NOT NULL DEFAULT 0,
    tokens_out      INTEGER NOT NULL DEFAULT 0,
    tokens_think    INTEGER NOT NULL DEFAULT 0,
    tokens_cache    INTEGER NOT NULL DEFAULT 0,
    cost_usd        REAL NOT NULL DEFAULT 0,
    wall_ms         INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (run_id) REFERENCES runs(run_id)
);
