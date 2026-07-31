Conductor detected that your previous run in "{planName}" stage {stage} was interrupted ({reason}).

Work in: {repo}

## 0. RE-ORIENT (mandatory, before any action)

1. Run `git status` and `git log --oneline -5` — confirm branch and uncommitted state
2. Read **AGENTS.md RESUME block** at the bottom — this is what was in-flight
3. Read **docs/iterations/iter-parity-pipeline/TRACKER.md** handoff block — what was claimed
4. Read the **PLAN.md** section for stage {stage} — what the spec actually requires
5. Inspect all modified files in the working tree. Understand what was half-done.

## 1. DECIDE: FINISH OR REVERT

**If you can finish safely:**
- Complete the in-flight checkpoint following the normal Shamshir ritual (PLAN §10)
- Run the gate battery from PLAN §11 for this stage
- Update TRACKER.md (mark checkpoints DONE with commit + evidence)
- Update AGENTS.md RESUME block (≤20 lines)
- Commit, push, clean tree

**If the interrupted state is too broken:**
- Revert to the last clean commit (`git log --oneline -5`)
- Record what happened in the TRACKER.md handoff block: what was abandoned, why, what the next session should do differently
- Commit that handoff-only change, push, clean tree
- Do NOT leave a broken working tree for the next session

## 2. AUDIT THE INTERRUPTION

Record in the AGENTS.md RESUME block: what led to the interruption (slow compile, infinite loop, quota exhaustion, crash) and what guard to add so it doesn't repeat.

End with SESSION-RESULT: what you did (finished or reverted), current state, next step.

{stageNotes}{extra}
