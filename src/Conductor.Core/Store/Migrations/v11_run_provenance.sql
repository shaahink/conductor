-- v11 (K3.3): which engine produced this run, and under which limits.
--
-- `runs.driver_ver` has existed since v1 and answers nothing: it carries
-- `Assembly.GetName().Version`, which is the 2.0.0.0 the csproj pins, identical for every build ever
-- made. Two binaries from different commits are indistinguishable in the record, and a run executed
-- on an uncommitted working-tree build looks exactly like one executed on a released tag. The only
-- way to know has been to ask the machine afterwards, which stops working the moment the machine
-- changes. The structured stamp goes in the three columns below; driver_ver is not dropped (its old
-- rows are all the history an imported v1..v10 database has) and from now on carries the same stamp
-- as one string, so a reader that only knows the old column gets a better answer than it used to.
ALTER TABLE runs ADD COLUMN engine_version TEXT;
ALTER TABLE runs ADD COLUMN engine_commit TEXT;
ALTER TABLE runs ADD COLUMN engine_dirty INTEGER;

-- The limits in force when the run started, as the JSON of Core.RunLimitsSnapshot: session token
-- cap, nudge ratio, run cost cap, run token cap, session cap, lane concurrency.
ALTER TABLE runs ADD COLUMN limits_json TEXT;

-- Per session, not only per run, and this is the point of the checkpoint. Limits are editable in
-- flight (Plan tab -> Settings, plan reload) and the engine binary can change between two sessions
-- of one run — a resumed run picks up whatever build is on PATH. A single run-level snapshot would
-- record only the first of those and silently claim it governed all of them. The Sarban run's cap
-- change at session 9 is the case in hand: today it has to be inferred from the shape of a token
-- curve; with these two columns it is a SELECT.
ALTER TABLE sessions ADD COLUMN engine TEXT;
ALTER TABLE sessions ADD COLUMN limits TEXT;
