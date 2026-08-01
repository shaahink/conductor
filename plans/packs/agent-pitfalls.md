## Pack: mistakes agents keep making here

Every item below is a real failure from this project's history, not a hypothetical. They cost sessions.

**Believing a green test suite means the feature works.** This codebase shipped 40 checkpoints with ~650
passing tests and *not one of those features had ever executed* — the first real run crashed in two
seconds. Tests are necessary and prove almost nothing about behaviour. Run the thing. `conductor run
--once` against a toy plan is cheap; do it.

**Blocking the foreground on a long command.** You will look silent, the stall detector will kill you, and
your work dies with you. Anything over ~3 minutes goes through `conductor bg`. This is the single most
common way sessions here die.

**Killing processes by name.** `Stop-Process dotnet` has already destroyed a different repo's work in this
project. Use `conductor bg stop`, which kills by tree.

**Saving what you learned for the handoff.** If you are killed at minute 40, everything not already in the
ledger is gone, and the next session repeats your dead ends from scratch. That exact sequence burned
eleven sessions once. `conductor note` the moment you learn something — a root cause, a dead end, a
command that does not work here.

**Hand-editing the tracker to mark work done.** It does nothing: the tracker is a generated view of the
database, and it will be overwritten. Use `conductor task --done <id> --evidence <path>`. And note that you
only *claim* — the engine confirms after its own gates and the Verifier agree with you.

**Making the measurement pass instead of the code.** Deleting a failing test, adding a `#pragma warning
disable`, raising an architecture ceiling, softening a gate command, editing the gate script. All six are
mechanically detected by `tools/gates/ratchet.ps1` and fail the session outright. There is no model in that
loop to persuade. If the bar is genuinely wrong, write `HUMAN:` in the handoff and stop — that is a
respected outcome, and a much better one than a fake green.

**Claiming without evidence.** "Implemented X" with no artifact path is not a delivery; the Verifier will
score it down and its findings become your retry prompt. A file, a gate log, a commit sha — one of those,
every time.

**Over-claiming in the handoff.** The next session trusts you. If something is shaky, half-done, or you are
not sure it works, SAY SO plainly. "This is thin and I could not verify the error path" is worth more to
your successor than a confident sentence that turns out to be false — and the Advisor fact-checks handoffs
against git and the artifacts anyway, so a contradiction gets flagged, not believed.

**Landing a whole session's work in a sibling repo.** On a multi-repo plan the engine judges progress from
the ANCHOR repo — the one the plan's `repo` points at. A session whose entire output was a sibling-repo PR
is recorded as NoProgress, burns an attempt, and queues a Fix session for nothing being broken. That has
happened twice in one run, at $3.82 for the first spurious fix alone, in a plan already written to avoid
it. So: land at least one anchor-repo commit every session. A dated proof-note is enough and is the
established shape — append to the anchor's field notes or tracker, e.g. `2026-07-29 — S4: shipped
sk-studio#41 (deploy window full, next slot 15:12)`, with the sibling sha or PR number in it. It is not
ceremony; it is the only record the run history will keep of what you did.

**Sprawling the diff.** Roughly 15 files per session. If you find yourself touching 40, you have taken on
too much — land what is solid, note the rest as follow-ups, hand off cleanly.
