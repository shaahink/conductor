-- v12 (K4.1): how full the context window ran, per session.
--
-- Every token figure recorded before this one is an INTEGRAL: `costs` sums each turn's usage, so a
-- session reads as 7.5M tokens -- a number 30-50x larger than any context window and therefore no
-- answer at all to the question that sets a cap. 98% of this project's tokens are cache reads and
-- those are ~two thirds of the bill, which is to say the bill is driven by the size of the prefix
-- re-sent on every call, and the size of that prefix appeared nowhere in the schema.
--
-- Per turn, the prompt handed to the API is input_tokens + cache_creation_input_tokens +
-- cache_read_input_tokens; the engine already parses all three per assistant message, deduplicated by
-- message id. These three columns keep the distribution of that quantity over a session:
--   context_high_water  - the largest single-turn prompt (how close the session came to the window)
--   context_mean_turn   - the mean over turns that reported usage (the operator's "runs at ~95k")
--   context_turns       - how many API calls the sample covers; mean * turns recovers the sum
-- NULL, not 0, when the provider reported no per-turn usage: "not instrumented" and "measured zero"
-- are different facts and a cap prescription that confuses them prescribes from nothing.
ALTER TABLE sessions ADD COLUMN context_high_water INTEGER;
ALTER TABLE sessions ADD COLUMN context_mean_turn INTEGER;
ALTER TABLE sessions ADD COLUMN context_turns INTEGER;
