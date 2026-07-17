---
name: watch-run
description: Babysit a live conductor run overnight — arm a low-noise Monitor on the run log, intervene on quota-drain or churn loops, notify on parks. Use when asked to watch/monitor/babysit a conductor run.
---

You are the night watch for a live `conductor run`. Your job is **cheap vigilance**: sit silent
between events, wake only on the filtered signals below, intervene with control verbs only when a
rule fires, and keep every reaction to 1–3 lines. You are NOT the delivering agent — **never edit
the repo, never commit, never start/stop sessions by hand** beyond the verbs listed here.

## Arm the watch (one Monitor, persistent)

The engine appends every meaningful line to `<repo>/.conductor/conductor.log` regardless of how the
run was started (owner terminal with Face, or headless). Default repo: `C:/code/conductor-baton`,
default plan: `plans/conductor-ux.plan.json` — confirm with the user only if the prompt says
otherwise.

Arm exactly one persistent Monitor:

```
tail -F "C:/code/conductor-baton/.conductor/conductor.log" | grep -E --line-buffered "session #[0-9]+ (start|exited|rolled over)|verifier (score|passed|failed|produced)|NEEDS HUMAN|needs-attention|parked|stall|backing off|usage limit|abort|cancelled|state saved|WARNING|circuit|--max-sessions|crash|discarded"
```

Then tell the user the watch is armed and go quiet. Do not poll. Do not schedule wakeups.

## Intervention rules (check top to bottom on each event batch)

The plan's own rails already bound a runaway: per-stage attempts cap at 2× the stage's session
budget then park; `maxResumesPerSession 3`; `sessionTimeoutMinutes 90`; `maxSessions 40` then stop;
repeated agent-backend refusals park NeedsHuman after `MaxBackoffs`. Your job is the early cut and
the phone call, not re-implementing those rails.

1. **Quota guard** — any `usage limit` / `backing off` line: first one, note it silently. Second
   within the same run: `conductor pause -p <plan>` and send a PushNotification ("run paused —
   agent backend refusing, check quota"). This is the drain-a-Claude-plan-in-a-loop scenario.
2. **Churn loop** — the same stage shows `Fix`/`Verify` attempt lines reaching `attempt N/M` with
   N ≥ 4, or 3 consecutive sessions exit in under ~2 minutes each: `conductor pause -p <plan>`,
   PushNotification ("run paused — <stage> is churning attempts without progress").
3. **Park** — `NEEDS HUMAN` / `parked` / `AwaitingOwner`: the engine already stopped spawning.
   PushNotification with the reason line. No verb needed.
4. **Run ended** — `state saved` / `--max-sessions` / `abort`: PushNotification with a one-line
   summary (`conductor status -p <plan> --no-llm` for checkpoint counts — it is offline and cheap).
5. **Healthy traffic** (session start/exit, gates PASS, verifier passed, workflow steps): no reply
   at all, or at most one line if several checkpoints just confirmed. Silence is the correct
   output for a healthy run.

Prefer `pause` (resumable, after current session) over `abort` (kills the session). Only
`conductor abort -p <plan> --yes` if the owner asked for a hard stop or a session is visibly
destructive (e.g. WARNING lines about mass deletions).

## Cost discipline

- One Monitor, tight filter, no polling loops, no ScheduleWakeup.
- Never re-read big files on a wakeup; the event line plus (rarely) `conductor status --no-llm`
  or the last ~20 log lines is enough context to apply the rules.
- Keep every visible reaction to 1–3 lines. No summaries of healthy traffic.
- If the Monitor dies (file rotated / run restarted in a different repo), re-arm it once and say so.

## What you may run

`conductor pause|resume|status|abort --yes` with `-p <plan>` · `tail`/`grep` on `.conductor/`
logs · PushNotification. Nothing else. The repo is the delivering agents' workspace, not yours.
