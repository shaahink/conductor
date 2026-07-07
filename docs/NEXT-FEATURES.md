# Conductor — next features backlog

Ideas captured during the v2 dashboard work. Not yet implemented; kept here so they survive
across sessions. Each should stay **resume-friendly** (persisted in RunState / `.conductor/`) and
must never disrupt an in-flight run.

## Smarter session management (needs care)
- **Token-budget rollover.** Track cumulative session tokens (already captured). When a session
  exceeds a configurable threshold (`limits.maxSessionTokens`), conductor decides it's cheaper/safer
  to end it cleanly and start a *fresh* session rather than continue a bloated context.
  - The current session must first write a compact handoff (tracker handoff block + `SESSION-RESULT`)
    so the fresh session resumes without loss.
  - Distinct from stall/timeout: this is a *healthy* rollover, not a failure — new `SessionOutcome`
    (e.g. `RolledOver`) and no attempt burned.
  - Config: `limits.maxSessionTokens`, maybe `maxSessionTurns`.

## Learning pipeline / instruction "batteries" (dynamic, resume-friendly)
- **Struggle log → briefing.** At session end, ask the agent to emit a short "what I struggled with /
  gotchas for the next agent" note. Conductor appends it to a rolling brief that is injected into the
  next session's prompt (via the `{queuedInstructions}` / a new `{lessons}` var).
- **Bounded AGENTS.md.** Fold durable lessons into `AGENTS.md`, but cap its size: keep a rotating
  "Lessons (last N)" section, summarise/evict oldest when it grows past a byte budget so it never
  balloons. Consider a separate `.conductor/lessons.md` that is periodically distilled into AGENTS.md.
- **Other candidate batteries** (all opt-in per plan, composed into the prompt builder):
  - *Repo map / hot files*: cache the files most-touched this phase and surface them next session.
  - *Recent-failure digest*: last few gate failures summarised so the agent front-loads them.
  - *Definition-of-done recap*: the active checkpoint's acceptance criteria pulled from the doc.
  - *Time/cost budget hint*: remaining attempt/cost budget so the agent scopes appropriately.
  - Design: a pluggable `IPromptBattery` list in PromptBuilder, each contributing a named section;
    plan config chooses which are active. Keep every battery bounded and deterministic.

## Deeper child-process / shell visibility (bonus A, partial today)
- Live gate timers already surface conductor's own shell (gates/hooks). Could go further: a small
  "processes" lane showing nested CLI invocations (agent bash tools + conductor gates + hooks) with
  live status, à la Claude Code's tool tree.

## Enforced rituals / "batteries" & skills (research + implement)
The agent is *told* the rules; conductor should also *enforce* the safe ones so AFK runs stay clean.
- **Branch hygiene.** Optionally create/checkout the per-stage branch (`feat/loom-l<stage>`) itself and
  assert `branchPattern` before letting a session commit (today it only warns).
- **Commit/push discipline.** After a green session: assert working tree is clean (commit-or-revert
  policy), assert the branch is pushed, assert per-checkpoint commit convention — surface violations.
- **Git safety.** Refuse to run on a detached HEAD / wrong remote; guard against force-push; ensure
  `.conductor/` stays gitignored except REPORT.md.
- **Skill batteries** (composable, opt-in per plan, bounded, resume-friendly): pre-session ritual
  checklist, definition-of-done recap from the doc, recent-failure digest, repo-map/hot-files,
  lessons brief (see learning pipeline). Design as pluggable `IPromptBattery` sections.
- Deliverable: research common practices, report findings, and define which become enforced vs advisory.

- Gate: optionally fail phase-confirm if the handover lists an unacknowledged critical gap.

## Handover gaps → follow-up work (close the loop)
The audit fixes what it can, then writes an honest handover listing what's still weak / shortcut /
deferred. Today those noted gaps can persist (audit is `maxAttempts:1`, and it may *document* rather
than *fix* risky/low-priority items). Better pipeline options:
- Parse the handover's "weak / deferred / bugs-not-fixed" bullets into tracked follow-up items
  (e.g. `.conductor/followups.md` or synthetic checkpoints) so they're not silently forgotten.
- Feed them into the next phase's session prompt as "known debts to address if cheap".
- Optionally allow >1 audit pass, or a dedicated "harden" session when the handover flags material gaps.
- Gate: optionally fail phase-confirm if the handover lists an unacknowledged critical gap.

## Reporting fidelity (AFK)
- Mid-session, the checkpoint table stays TODO until the agent writes the tracker at the end, and the
  header "▸ L1.1" can lag the agent's real focus (it's the first not-done row). Improve by:
  - Writing REPORT.md on a heartbeat during long sessions (not only between sessions) with the latest
    agent tool-calls + current thinking, so the AFK GitHub view reflects live progress.
  - Encouraging incremental tracker updates in the ritual, and/or inferring an in-progress checkpoint.

## Lifecycle: pause → redeploy → resume (verify + document)
- Common flow: pause today, deploy a new conductor build, resume tomorrow. Mostly supported already
  (control `pause`, atomic `state.json`, pid lock, crash recovery→resume). Consolidate and document:
  - `conductor pause` (or `Q` quit-after-session) → clean stop at a session boundary.
  - Swap `bin\conductor.exe` (state schema is additive/back-compatible — covered by StateCompatTests).
  - `conductor run` → resumes from `state.json` (fix/resume/phase-gate/audit all persisted).
  - Add a `conductor doctor`/`status --verbose` that prints exactly what will happen on resume.

## Zero-config bootstrap ("just run it in the folder")
- `conductor` with no plan in the cwd should offer to scaffold: detect repo, tracker, gates
  (build/test/lint by ecosystem), write a starter `conductor.plan.json`, then run.
- Auto-detect ecosystem batteries (dotnet/node/pnpm/cargo…) for sensible default gates.

## Observability & diagnostics (next iteration)
- **Serilog structured logging** — replace StringBuilder/Console.WriteLine with proper structured logging
  (Serilog, file+console sinks, levels). No more silent failures. Include session id, stage, attempt,
  gate name as context properties.
- **Diagnostic console** — a simulated scrollable log view in the dashboard (like Claude Code's debug
  pane) so you can watch what conductor is doing without tailing the log file.
- **Graceful Ctrl+C** — already safe (state saved, sessions resumed), but enhance: on Ctrl+C, write a
  final heartbeat REPORT.md, queue resume, flush logs, then exit — zero log loss.
- **Resume with enhanced prompts** — when resuming after an interrupt, inject a short "you were
  interrupted because X" context into the resume prompt (already partly there in the base template).

## Research + polish (queued)
- Survey comparable autonomous multi-session/agent orchestrators; blend useful patterns.
- Color-coding/readability + beauty pass inspired by opencode / Claude Code terminals.
