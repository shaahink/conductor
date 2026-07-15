-- v7: tracked bugs (M7.2) — a found bug becomes a row that OUTLIVES the session that found it,
-- is injected into later prompts, and feeds the audit phase, so agents stop re-finding the same bug.

CREATE TABLE IF NOT EXISTS bugs (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id         TEXT NOT NULL,
    title          TEXT NOT NULL,
    detail         TEXT,
    severity       TEXT NOT NULL DEFAULT 'medium',   -- low | medium | high
    status         TEXT NOT NULL DEFAULT 'open',      -- open | fixed | wontfix
    stage_id       TEXT,
    found_session  INTEGER,
    fixed_session  INTEGER,
    created_at     TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at     TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (run_id) REFERENCES runs(run_id)
);
