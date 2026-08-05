-- v4: Add created_at column to ledger (the missing column that motivated single-source schema)

ALTER TABLE ledger ADD COLUMN created_at TEXT NOT NULL DEFAULT (datetime('now'));
