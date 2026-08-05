-- v9 (SC7.2): the per-session digest — tool mix, files written with counts, board claims,
-- bg-start purposes, notable build/test commands — stored as the JSON of Core.Events.SessionDigest.
--
-- A column rather than a table: exactly one digest per session, always read with the session row,
-- never queried on its own. NULL means "no digest recorded" — every row written before this column
-- existed, and any session that produced no captured tool calls. It is deliberately NOT an empty
-- object: a session that says nothing about what it did must not read as one that provably did
-- nothing.
ALTER TABLE sessions ADD COLUMN digest TEXT;
