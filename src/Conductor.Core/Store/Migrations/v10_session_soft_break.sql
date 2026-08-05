-- v10 (K1.2): the cooperative rail's own measurement, per session — the threshold and ceiling that
-- governed it, how many times the wrap-up notice was actually delivered to the agent, when and at
-- what token count, and whether the session obeyed it (ended under its ceiling without the engine's
-- hard stop). Stored as the JSON of Core.SoftBreak.Outcome.
--
-- A column rather than a table, for the same reason as digest: exactly one per session, always read
-- with the session row. NULL means the session had no token ceiling or never crossed the soft
-- threshold — which is a different fact from "was nudged and ignored it", and the tuning pass this
-- exists to serve must be able to tell those apart.
ALTER TABLE sessions ADD COLUMN soft_break TEXT;
