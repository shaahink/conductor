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

## Research + polish (queued)
- Survey comparable autonomous multi-session/agent orchestrators; blend useful patterns.
- Color-coding/readability + beauty pass inspired by opencode / Claude Code terminals.
