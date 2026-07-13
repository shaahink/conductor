-- v6: Add confirmed column to checkpoints (M4.1 claims vs confirmations)
-- 0 = claimed by agent (not yet confirmed by engine)
-- 1 = confirmed by engine after green gates + verifier pass

ALTER TABLE checkpoints ADD COLUMN confirmed INTEGER NOT NULL DEFAULT 0;
