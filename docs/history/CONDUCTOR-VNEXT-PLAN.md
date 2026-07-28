# Conductor v-Next — "The Foreman" Plan

**Written:** 2026-07-10 (Claude owner session, after auditing the Shamshir iter-land-fix run,
the Loom Gap Close run, Baton v2 source, and both repos' `.conductor/` histories).
**Supersedes:** `NEXT-ERA.md` stage map (its D/O/P items are absorbed or explicitly killed below).
**Development mode:** built BY the current stable conductor (master binary), one stage per session,
per the user's standing workflow. This document is the mega plan a conductor run executes.

---

## 0. Verdict on the current system (evidence, not vibes)

What the evidence says (Shamshir iter-land-fix, 11 sessions, $0.77, 0/6 checkpoints; Loom Gap
Close running; Baton v2 delivered in 91 sessions / $4.78):

| # | Failure observed | Evidence |
|---|---|---|
| C1 | **Stall-kill destroys knowledge.** s9 committed the CORRECT F17 fix at 02:16, was killed mid-verification, and its discovery died with it; s10/s11 chased stale theories. | `9962432` + REPORT.md timeline |
| C2 | **Stall detection is blind.** "No stdout for 15m" fired on sessions that were legitimately waiting on backtests/builds. ALL A1 stalls were this. | TRACKER stall note, session logs |
| C3 | **Attempt budget burns on identical failures.** 4 consecutive same-shape stalls on A1; 13 DNS-dead sessions in the other repo. Health pane SAW it ("same-failure-loop") and did nothing. | REPORT.md health block |
| C4 | **Verification claims are never independently checked.** Conductor "verdicts" trust the agent's tracker writes; A1 sat IN PROGRESS for 6 sessions after the fix landed. | tracker vs git history |
| C5 | **Green gate battery ≠ working software.** build/unit/integration were green through the entire F17 window; no gate ran the actual product. | every session's gate lines |
| C6 | **Human injection is load-bearing and unchecked.** The 003 injection rescued history but also ordered "do NOT run another tape backtest" — wrong — and carried a red herring forward. | `.conductor/queue/003-*.json` |
| C7 | **Conductor doesn't own processes.** 4 orphan opencode.exe from Jul 8/9 alive today; agents kill by name (`Stop-Process dotnet` — s9) and nuke unrelated repos' work. | live process table |
| C8 | **TUI is not fit to watch.** Overlapping header text, plan column 4 chars wide, "(no thinking captured yet)", 20-key chord bar with duplicates. | user screenshot |
| C9 | **Heartbeat commits pollute history** (95% of Shamshir commits) and templates are unmanageable (user needs an agent to author one). | git log; user report |
| C10 | **Engine fragility.** 121KB Orchestrator god-class, sync-over-async run loop, zero integration tests for the control loop, silent control-verb drops. | conductor-DEBT.md, source |

What WORKS and must survive: event-sourced spine (events.jsonl), tracker/handoff convention,
per-session prompt+jsonl logs, cheap DeepSeek economics, budget park/resume, Telegram plumbing,
the QA-catches-bugs effect the user observed, and the **scored-retry effect** (a failed scored
QA followed by a same-model retry produced a better delivery — this is a mechanism, exploit it).

---

## 1. Product definition — what Conductor v-next IS

> **A resilient foreman for long plans: it owns the processes, owns the truth, scores every
> delivery, never loses knowledge, and is pleasant to watch and talk to.**

Explicit NON-goals (kill list — delete code where it exists):
- ❌ Time-travel / replay / F8 (user: "not trying to create a time machine")
- ❌ 9 personas → keep **3 roles**: Deliver, Verify, Advise
- ❌ Confidence pane, AI-health scoring theatrics — replaced by the Verifier's hard score
- ❌ Heartbeat commits to the feature branch (report goes to `.conductor/`, period)
- ❌ Hierarchical template system — replaced by plan import (§3.1)

---

## 2. Architecture decision — engine/UI split (and the TS question)

**AD-1: Split the engine from the face.** The C# engine keeps: orchestration loop, process
supervision, event log, git, gates, providers, Telegram. It gains a **local control plane**:
HTTP + SSE on `127.0.0.1:<port>` (endpoints: state, task graph, session transcript stream,
thinking stream, control verbs, injections) — the same event stream already in events.jsonl,
served live. Everything below rides on it: TUI, `conductor chat`, Telegram, a future web page.

**AD-2: Rebuild the TUI in TypeScript + Ink** (user is open to TS; this is the stack Claude
Code/Gemini-class CLIs use). Rationale: Spectre.Console has no real flexbox/reflow — the broken
layout is structural, not a bug to patch; Ink gives component model, text wrapping, scrollable
panes, and an ecosystem of tested widgets. Rust/ratatui is more capable still but adds a third
language for marginal gain. The C# engine stays authoritative; the TUI is a *disposable client*
— if it crashes, the run does not (fixes "broken DOS app" and C8 in one move).
**Fallback:** engine keeps a `--headless` text mode (log tail + status line) so no TUI is ever
required to run a plan.

**AD-3: One task store, SQLite, in `.conductor/run.db`.** Tasks, sessions, attempts, gate runs,
scores, ledger entries, injections — queryable (`conductor log --query` falls out for free).
`TRACKER.md` becomes a *generated view* for humans/agents to read, written by conductor; agents
report progress through a tiny `conductor task` CLI/MCP verb set instead of hand-editing
markdown. (Markdown hand-edits still accepted as fallback input, parsed and reconciled.)

---

## 3. The workflow (tailored to how the user actually works)

### 3.1 Plan in, no templates
`conductor plan import <PLAN.md|paste>` — an LLM pass (advisor model) converts a mega plan into
the task graph: stages → sessions → checkpoints, dependencies, per-stage **truth gates**, read
order. It renders back a summary table for interactive confirm/edit, then writes `run.db` +
generated tracker. Re-import diffs instead of clobbering (mid-plan changes are a first-class
operation, not a restart).

### 3.2 Session cycle — Deliver → Verify(score) → merge-QA
```
DELIVER (agent)      — one checkpoint, full repo access, background-run primitives provided
VERIFY  (agent, fresh context, cheap) — re-runs the checkpoint's truth gate itself, checks
        claims against artifacts/git/DB, outputs JSON: {score 0-100, findings[], verdict}
   ≥ threshold (default 80) → checkpoint DONE, findings become follow-up tasks
   < threshold → structured findings injected into a RETRY of Deliver (same model —
        this is the observed fail-then-better-retry effect, now systematic)
QA-fix is MERGED into the retry (no separate fix session): the verifier's findings ARE the
        retry prompt. A standalone audit session exists only at phase boundaries.
```
Parallelism where it pays, nowhere else: Verify(N) runs against a pinned commit **while**
Deliver(N+1) proceeds when the next checkpoint doesn't depend on N's artifacts; independent
stages may run in worktrees (existing mutating-lane machinery, kept). No crazy fan-out.

### 3.3 Knowledge ledger (kills C1/C6)
- Append-only `ledger` table + generated `LEDGER.md`: every session MUST write findings as it
  goes (prompt contract: "when you learn something, log it immediately via `conductor note`,
  not at session end"). The A1 disaster is impossible if s9's discovery hits the ledger at 02:16.
- **Stall = debrief, not execution.** On stall detection, conductor first *injects* "wrap up:
  write ledger + handoff now, 3 minutes" and only kills after the grace window (soft-kill).
- The Advisor **fact-checks** handoffs and human injections against git/log/artifacts before
  they enter the next prompt; contradictions get flagged in the prompt instead of propagated.

### 3.4 Truth gates per stage (kills C5)
A stage declares gates in three tiers: `fast` (per session, seconds), `full` (per phase), and
**`truth`** — a product-level assertion the plan author writes (e.g. Shamshir A-stages:
`research doctor && research run start --venue tape … && research run validate --min-trades 1`).
The gate battery question ("is it useful?") resolves to: fast tier stays cheap, truth tier is
what actually protects the plan. Gate results cache by HEAD SHA (`lastGreenGateSig` exists —
extend to per-gate).

### 3.5 Process ownership (kills C2/C7)
- `ProcessSupervisor`: every child (agent, gates, app-under-test) spawned into a Windows Job
  Object → kill-by-tree, no orphans; PID registry in run.db; startup reaper for leftovers.
- **Sanctioned background-run primitive for agents:** `conductor bg start|status|logs|stop`
  (also exposed via MCP). Prompts mandate it for anything >3 min. Stall detection then watches
  *(a)* agent stdout, *(b)* tool-call events from the agent JSON stream, and *(c)* liveness of
  supervised bg children — "quiet but its backtest is running" is NOT a stall (C2 dies here).
- Output floods can't hurt: bounded ring buffers, spill to file.

### 3.6 Resilience
- Pre-flight per session: DNS/API reachability, disk, git clean, budget remaining. Fail → park
  with reason + Telegram ping, auto-recheck with exponential backoff (no attempt burned).
- Same-failure circuit breaker: 2 consecutive attempts with identical failure signature →
  Advisor session (not another Deliver); Advisor's structured verdict (Retry-with-injection /
  NeedsHuman / SkipStage / RerunGates) is **honored by the orchestrator** (today it's ignored).
- Budget: per-session + per-run caps persisted across restarts (fix FU-B3-3); park at cap,
  one-key resume with fresh budget.
- Conductor self-crash: control loop goes async (B4.7 debt), integration harness (B4.8) becomes
  a real gate for conductor's own releases: fake agent + temp repo, full cycle asserted.

### 3.7 Talk to it (kills the observability gap)
- `conductor chat` — spawns an agent wired (MCP) to run.db + ledger + logs + control verbs:
  "how did s9 die?", "update task A2 to include F19", "inject X into the next retry", "score
  history for stage B?" — the user's "spin up an agent and talk about the process" want.
- Telegram (exists) upgraded to the same verbs: session summary on end, NeedsHuman with inline
  buttons (retry / skip / inject / chat), reply-to-inject. Discord = same adapter interface,
  later, optional.
- End-of-run **interactive report**: generated REPORT.md stays, plus `conductor report --serve`
  renders the run (stages, scores, ledger, costs, diffs) in the TUI/browser view. "It knows
  what's delivered and what's not" = the Verifier's scores, not the agent's claims.

### 3.8 Speed program (build/test/UI must be quick)
Measured first, then: gate caching by SHA (above), `--no-build` test reuse where safe,
solution-filter builds for gate tiers, parallel test lanes, Angular staleness guard pattern
(already proven in Shamshir) generalized to a `skipIfFresh` gate attribute, and **per-stage gate
selection** (a docs-only checkpoint runs no dotnet gates — today every session pays the full
battery). Target: fast tier ≤ 60s wall on Shamshir. Companion repo-side rule (owner, 2026-07-10):
slow suites must test their own seam only — in Shamshir, cTrader/NetMQ tests cover transport,
never trading logic (kernel/tape/golden own that); see ENGINE-TRUTH.md §4b.

---

## 4. Stage map (each stage ≈ 1–2 conductor sessions, ordered)

```
F0  Foundations: kill list executed (replay/personas/confidence/heartbeat-commits deleted),
    async control loop (B4.7), integration harness (B4.8). Gate: 0w/0e, harness cycle green.
F1  run.db task store + tracker-as-view + `conductor task/note` verbs (writes ledger) +
    telemetry schema per addendum D8 (sessions/gates/scores/handovers/costs as rows).
    Gate: import→run→report round-trip on a toy plan; tracker regenerates byte-stable;
    `conductor report --query` answers a cost-per-stage question.
F2  ProcessSupervisor + Job Objects + orphan reaper + `conductor bg *` (+MCP). Gate: harness
    proves kill-by-tree, orphan reap, bg liveness feeding stall detector.
F3  Stall v2 (stdout+tool-events+bg-liveness) + soft-kill debrief + same-failure breaker +
    pre-flight/backoff. Gate: simulated quiet-but-working session survives; 2-identical-stalls
    triggers Advisor; DNS-down parks without burning attempts.
F4  Verifier role + scoring loop + findings-as-retry-prompt + advisor verdicts honored +
    handoff fact-check. Gate: rigged bad delivery scores <80, retry with findings passes;
    rigged good delivery isn't blocked (false-positive check).
F5  Control plane (HTTP+SSE on localhost) serving state/tasks/events/control — AND the command/
    query decomposition of the Orchestrator god-class (C10) that F5 is the natural seam for: a
    ControlDispatcher (Core/Commands/) owns what each control verb DOES, extracted out of
    Orchestrator's inline switch; the TUI queue, control.json, and the new HTTP POST /control all
    converge on it as three ingresses to one command executor. The read side (GET /state, /tasks,
    /events) is built entirely from RunStateProjection/SnapshotBuilder/TaskGraph over events.jsonl
    — it never touches Orchestrator internals, so it survives future engine refactors. HttpListener,
    not ASP.NET Core (ratchets against D12's build-speed goal; ~600 LOC added, Orchestrator net
    -150 LOC). Transcript/thinking-stream SSE deliberately deferred to land alongside F6's agent
    pane (the only consumer) rather than serving data nothing reads yet.
    Gate: curl-level contract tests; headless mode unchanged; control plane off by default, a bind
    failure is caught and logged, never fatal.
F6  Ink TUI v1 (TS): panes + palette per addendum D11 (that checklist IS the acceptance
    list — plan tree w/ scores, transcript WITH thinking, process pane, cost ticker).
    Gate: golden-layout snapshot tests at 80×24/120×30/200×50; crash of TUI leaves run
    alive. THIS is the "enjoy looking at it" stage — budget real design time. D12 build
    split lands here too (TS TUI outside dotnet build; engine incremental <10s).
F7  plan import (LLM) + re-import diff + truth-gate tier + per-stage gate selection + speed
    program. Gate: import the actual Shamshir iter-land-fix PLAN.md → correct graph; docs-only
    stage runs 0 dotnet gates.
F8  conductor chat + Telegram v2 per addendum D7 (long-poll, no hosting; buttons,
    reply-inject, /status from run.db, digest). Gate: scripted chat updates a task +
    injects into next session; full phone-only drive of a toy run.
F9  Dogfood close: run one real Shamshir stage (A2) end-to-end under v-next; fix what bleeds;
    final audit + this doc's checklist rated CONFORMS/DEVIATES.
```

Dependencies: F0→F1→(F2,F3)→F4; F5→F6; F7 after F1; F8 after F5; F9 last. F2/F3 and F5/F6 can
run as parallel lanes if the run is healthy.

## 4b. Addendum — owner requirements (2026-07-10)

**AFK control without hosting (D7).** Keep **Telegram long-polling** as primary: a Telegram bot
needs NO hosting — the conductor process polls `getUpdates` outbound, works behind NAT, zero
infra, and the plumbing already exists in Baton. Discord bot (gateway websocket, also
host-free) is the optional second adapter behind the same interface. WhatsApp/Signal are
explicitly out (Business API = hosting + approval pain). Required UX, not plumbing: session-end
one-liner with score, NeedsHuman ping with inline buttons [Retry] [Skip] [Inject…] [Chat],
reply-to-message = injection into next session, `/status` answers from run.db, daily digest.
Acceptance: full run driven from phone only, laptop lid closed.

**Telemetry & reporting (D8).** run.db is the single queryable truth: tables `runs, stages,
sessions, attempts, gates(name,sha,duration,outcome,cached), scores(session,verdict,findings),
ledger, handovers, injections, costs(tokens_in/out/think, usd, wall_ms, split agent|gate|advisor)`.
Handovers become rows (rendered to md for agents, stored structured for queries). REPORT.md is
generated FROM run.db; `conductor report --query "<sql|dsl>"` for ad-hoc questions ("cost of
stage R3?", "which gates fail most?"). Every session row carries cost, wall time, tokens,
outcome, score — nothing is only-in-a-log-line anymore.

**Gate battery dedup (D9).** One rule: a gate result is a function of (gate, HEAD sha, tier) —
cache it. Re-running an unchanged battery is forbidden by the engine, not by convention. Tiers:
`fast` per session, `truth` per stage (plan-defined product assertion), `full` once per stage
confirm. Agents are TOLD in the prompt which gates conductor already ran green at this sha, so
they don't re-run them out of paranoia (today they do, doubling the burden).

**Reduce human verification (D10).** The Verifier (§3.2) is the mechanism: machine truth gates
+ scored verification auto-advance ≥ threshold; humans appear only at plan-declared OWNER-GATE
stages and NeedsHuman breaks. Target: an iteration like alpha-loop runs R0→R5 with exactly two
human touches. Weekly digest replaces "watch the TUI to feel safe."

**TUI required-features checklist (D11 — acceptance list for F6):**
- Plan pane: tree with per-stage state/score/cost, current stage highlighted, prev/next visible;
  no truncation at 100+ cols (the 4-char column bug class is a release blocker).
- Agent pane: live transcript WITH thinking stream (deepseek reasoning is in the JSON — Baton
  drops it today), scrollback + search, tool-call folding.
- Process pane: supervised children (PID, purpose, runtime, last output line) — "what is it
  actually doing right now" answered at a glance. Data source (`ProcessSupervisor`, PID registry +
  Job Object tracking) already exists from F2 — zero UI currently consumes it (`Ui/*.cs` has no
  reference to `ProcessSupervisor`). This is new-UI-wiring work, not new capture; don't underestimate it.
- Live prompt/persona editor: `PromptBuilder.Render()` already re-reads `<plan-dir>/*.md` template
  files from disk on every call (not cached at startup), and `LiveDashboard`'s existing raw-JSON
  stage editor (`OpenStageEditor`/`HandleStageEditKey`) already proves live edits reach the running
  orchestrator at the next session boundary. F6 should build a proper *templated* editor (session.md
  / fix.md / verify.md / persona system prompts) with a rendered-prompt preview, writing to the same
  files the engine already hot-reloads — no new engine primitive needed, just a better editor surface.
- Agent/session history browser: a pane or command-palette action to page back through
  `state.History` / the `run.db` `sessions` table — "what did session N actually do." The data is
  already queryable (`conductor report --query` proves it); F6 just needs a UI over it.
- Control: command palette (`:` or Ctrl+K) replacing the 20-key chord bar; inject editor with
  preview; pause/skip/retry; every action acknowledged with a toast + log line (no silent drops).
  As of F5, `POST /control` on the HTTP control plane accepts the same command shape `control.json`
  does and routes through the same `ControlDispatcher` — the palette can drive either the file or
  the HTTP endpoint with identical semantics.
- Ticker: session + run cost, tokens, wall time, gate cache hits — always visible, one line.
- Renders correctly at 3 sizes (80×24, 120×30, 200×50) — golden snapshot tests in CI.
- Transcript/thinking-stream SSE (`GET /transcript/current`) was deliberately deferred out of F5
  (see F5 stage-map entry below) — build it alongside the F6 agent pane that will actually consume
  it, rather than serving an endpoint nothing reads yet.

**Conductor build/dev speed (D12).** Engine and TUI build independently (TS TUI is outside
`dotnet build` entirely); engine target: incremental build < 10s, full < 30s (today's mixed
net9/net10 multi-project solution misses this); stable driver ships as a published single-file
exe so running a plan never compiles anything. `conductor doctor` self-check < 2s.

## 5. Owner decisions (locked unless you veto)

| # | Decision | Choice |
|---|---|---|
| D1 | TUI stack | TS + Ink over local control plane (AD-2); C# engine authoritative; headless fallback |
| D2 | Task store | SQLite `run.db`; markdown becomes generated view (AD-3) |
| D3 | QA shape | Verify-with-score after every Deliver; findings feed the retry; standalone audit only at phase ends |
| D4 | Score threshold | 80 default, per-stage overridable in plan |
| D5 | Kill list | replay/time-travel, persona bloat, confidence pane, heartbeat commits, template dirs |
| D6 | v-next name | keep repo, new era branch `feat/foreman` (suggestion only) |

## 6. Immediate next actions (before any v-next code)

1. Land the Shamshir unblock (tracker updated; A2 next) using the CURRENT conductor + the
   stall-prevention prompt block that now lives in the tracker handoff.
2. Add to every Shamshir stage prompt until F2 ships: "use `Start-Process` + file logs for
   anything >3m; never `Stop-Process` by name; port is 5134."
3. Wire ONE truth gate into conductor.plan.json A2 (tape run validate --min-trades 1) — it
   costs one line today and would have saved 11 sessions.
