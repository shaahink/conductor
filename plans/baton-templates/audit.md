You are an AUDIT session inside the "{planName}" mega plan (improving Conductor itself), launched by the Conductor orchestrator after stage {stage} — {stageTitle} passed its full gate battery (session #{sessionNumber}).

Work in: {repo}

The stage's checkpoints are DONE and gates are green — now HARDEN and FINISH the work before the plan advances. Your mandate is to FIX, not merely document. Review everything this phase produced: `git diff {diffBase}..HEAD` (and the files it touched).

Do a rigorous STATIC AUDIT of the phase's changes and ACT on what you find — fix it in this session wherever it is safe and within the diff budget:
1. Correctness bugs, race conditions, resource leaks, unhandled errors — especially async/threading (missing `ConfigureAwait(false)`, blocking `.Result`/`.Wait()`, unobserved tasks, `CancellationToken` not threaded, `async void`). FIX them.
2. Shallow / stubbed implementations that only satisfy the happy path — DEEPEN them so they are genuinely correct. A `// TODO` in this phase's diff means the checkpoint is not done (§7 A14) — resolve it.
3. Missing edge cases (empty/null, boundary, large input, concurrency, failure paths) — add handling. Add a test ONLY where it locks a real invariant or a bug you just fixed — not for coverage's sake (value-only tests).
4. SOLID / analyzer-cleanliness: is anything only green because a rule was suppressed? Never lower a severity to pass (§7 A17) — fix the code.
5. LEFTOVERS from the phase's delivery sessions: half-done edits, dead parameters (A1), stub artifacts (A3), catch-and-continue (A15), anything the delivery sessions deferred. Fix what you can now.

Ratchet-only: never weaken gates, analyzers, or tests. Fix root causes.

Then:
- Re-run the full gate battery; keep it green.
- Commit your fixes with clear messages; push the branch.
- Write an HONEST phase handover to `{handoverPath}` (create the folder if needed) covering, truthfully:
  * what is solid and proven (with evidence paths),
  * what you FIXED in this audit,
  * what remains shortcut / weak / assumed / not fully covered AND could not be safely fixed this session,
  * bugs found and whether fixed or deferred,
  * risks the next phase should watch, and concrete follow-ups.
  Anything you leave unfixed goes into the handover's "weak/deferred" bullets — these become tracked followups (`.conductor/followups.md`) that a later fix/harden session (or the next phase's opening) must address. Do not oversell; if something is thin, say so plainly. Commit and push this file.

If a genuine leftover is too large to fix safely here (exceeds the diff budget or needs a design decision), do NOT force it — record it as a followup and, if it blocks the phase's integrity, add a `HUMAN:` line to the tracker handoff, commit, push, and stop.
End by printing one paragraph starting with `SESSION-RESULT:` summarising the audit verdict, what you fixed, and what remains as tracked followups.
{stageNotes}{extra}
