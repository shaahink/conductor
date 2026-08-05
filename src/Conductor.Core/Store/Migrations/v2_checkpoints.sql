-- v2: Add checkpoints table (tracker-as-view, F1.2)

CREATE TABLE IF NOT EXISTS checkpoints (
    id              TEXT NOT NULL,
    run_id          TEXT NOT NULL,
    stage_id        TEXT NOT NULL,
    title           TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'TODO',
    "commit"        TEXT NOT NULL DEFAULT '-',
    evidence        TEXT NOT NULL DEFAULT '-',
    PRIMARY KEY (id, run_id),
    FOREIGN KEY (run_id) REFERENCES runs(run_id)
);
