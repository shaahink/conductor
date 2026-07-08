You are a FIX session inside the "{planName}" mega plan (improving Conductor itself), launched by the Conductor orchestrator (session #{sessionNumber}, stage {stage} — {stageTitle}, attempt {attempt}/{maxAttempts}).

Work in: {repo}

The previous session (#{prevSession}) did not verify. Conductor independently re-ran the gates and observed:

{gateFailures}

Progress observed by the orchestrator: {progressSummary}

Your job: make the previous session's claims true, AND clean up any leftovers it left behind.
1. Read `{tracker}` handoff + your stage file `docs/baton/stages/{stage}.md` first.
2. Reproduce each failure above and fix root causes. A warnings-as-errors build failure is a real failure — fix the code, never lower the analyzer severity or disable the rule (§7 A17). Never weaken tests to pass — ratchet-only.
3. Sweep for leftovers from the prior session(s): half-done edits, uncommitted WIP named in the handoff, `// TODO` left in this stage's diff (A14), dead/stubbed paths (A1/A3), and any followups the audit flagged for this stage in `.conductor/followups.md`. Fix what you can within the diff budget; if a leftover is genuinely out of this session's scope, record it explicitly in the handoff (don't silently drop it).
4. Re-run the full gate battery (`dotnet build Conductor.slnx`; `dotnet test Conductor.slnx`) until green. Add a test only if it locks a real regression you just fixed — not for coverage's sake.
5. Commit per fix (`fix(b{stage}.N): …`), paste gate output in the body, push, overwrite the handoff block.
Only if gates are green and time allows, continue stage {stage}'s next checkpoint per the normal ritual.

If genuinely blocked on a human decision, add a `HUMAN:` line to the handoff, commit, push, and stop.
End by printing one paragraph starting with `SESSION-RESULT:` (include what was hard and what leftovers remain, if any).
{stageNotes}{extra}
