-- v15 (KS4.2): the PASS-TO-PASS baseline -- the set of checks a regression gate last reported
-- passing, so that "nothing that worked broke" is a set difference the engine can compute instead of
-- a rule the session prompt asks an agent to obey.
--
-- ONE ROW PER (run, gate), overwritten in place. The history of the set is deliberately not kept:
-- the gates table already records every run of every gate, and a full name list per battery would
-- add thousands of rows per session for a suite the size of this one's, to answer a question nobody
-- asks. What the class needs is the last CLEAN set and nothing else.
--
-- `names` is the set, newline-joined and sorted, because it is compared as a set and read by humans
-- in that order. `count` is denormalised so a report can say "3000 checks green" without parsing it.
--
-- WHEN IT IS WRITTEN is the whole of the anti-laundering property, and it lives in GateRunner rather
-- than here: the row advances only when the gate passed AND lost nothing. A regressing battery
-- leaves the previous set exactly where it was, so the session after it sees the same regression
-- rather than inheriting the smaller set as the new normal -- which would make one red session the
-- price of deleting a test forever.
CREATE TABLE IF NOT EXISTS gate_pass_sets (
    run_id      TEXT    NOT NULL,
    gate        TEXT    NOT NULL,
    sha         TEXT,
    names       TEXT    NOT NULL,
    count       INTEGER NOT NULL,
    updated_utc TEXT    NOT NULL,
    PRIMARY KEY (run_id, gate)
);
