# Tracker format and runtime files

## The tracker

The tracker is a Markdown file — `TRACKER.md` is what `conductor init` writes — that is both the
human-readable progress document and the machine-parsable state Conductor reads.

Since W1 the tracker is a **generated view**: the work graph in `.conductor/run.db` is the runtime
truth, and the tracker is re-rendered from it after every session. Hand-editing a checkpoint row no
longer claims anything — `conductor task --done <id>` (or the Face's board) is the one claim path.
The handoff block stays yours to write.

### Handoff block

```markdown
## Handoff  (overwrite this block, ≤12 lines, no history)
last: s12 L3 refactor DONE — all 3 checkpoints flipped. Gate battery: build OK,
  tests OK. Evidence: docs/evidence/L3/.
stage: **L4 Delivery — IN PROGRESS** (attempt 1/4).
gate: PASS.
next: **L4.1 Extract TfmScore** — rename + extract + wire.
trap: L4 depends on L3 being confirmed; pre-hook runs before first session.
```

Two things in here are load-bearing. `HUMAN:` anywhere in the handoff parks the run at NeedsHuman
and notifies you. A row flipping to `BLOCKED` does the same.

### Checkpoint table

```markdown
Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| L0.1 | Stub A | DONE | abc1234 | docs/evidence/L0.1-test.md |
| L0.2 | Stub B | IN PROGRESS | | |
| L0.3 | Stub C | TODO | | |
```

Checkpoint ids must share a prefix with a stage id — `L0.1` belongs to stage `L0`. The parsing rule
is `conventions.stageIdPattern`; override it if your ids look different (see
[plan-config.md](plan-config.md)).

### OWNER-PENDING marker

A `DONE (OWNER-PENDING: need creds to verify)` cell means the agent marked it done but left a note
for the human. The checkpoint is auto-promoted.

### QA-previous block

```markdown
> QA-previous (s12 QA of s11/L3): **confirmed.** Full gate battery re-run: build OK,
> tests OK. Verified 2 claims: TfmScore exists, interface extracted.
```

## Runtime files (`.conductor/`)

Conductor writes everything about a run into `.conductor/` inside the target repo. A `.gitignore`
in there keeps everything but `REPORT.md`, `followups.md` and `handovers/` out of your history.

The tree below is the whole set — every name the engine can compose under the state dir. It is
pinned by a test (`SF7_1DocsMatchRealityTests`) that scans the source for those paths, so a new
artifact that never reached this page is a red build, not a mystery file.

```
.conductor/
  .gitignore             Written on first run: ignore everything here except the three below
  run.db                 THE live run state (SQLite) — run_state, events, sessions, costs, gates,
                          checkpoints, bugs, ledger. Every transition persists here, and this is
                          what `conductor run` resumes from. `run.db-wal` / `run.db-shm` beside it
                          are SQLite's own write-ahead files, not artifacts
  REPORT.md              The AFK report — the only file conductor commits
  RUN-SUMMARY.md         Written when a run ends: the artifact that outlives the control plane, so
                          `status` can still answer after the engine is gone
  OWNER-QUEUE.md         The things only you can do — HUMAN lines, owner gates, parks with age —
                          regenerated at session boundaries
  conductor.log          Orchestrator text log
  conductor.lock         PID lock (two conductors can't fight over one repo)
  control.json           Transient control verbs from the CLI/TUI/Telegram; consumed and deleted
  lessons.md             Rolling lessons brief (bounded, rotating)
  followups.md           Tracked follow-up items from audits (versioned)
  transcript.jsonl       Append-only agent transcript — every stdout envelope, unbounded, kept
                          apart from the event spine because of its volume. `transcript.jsonl.runid`
                          beside it stamps which run the file belongs to
  mcp-journal.jsonl      What the agent did through the MCP tools, per session
  mcp-config.json        Generated MCP wiring handed to the agent. Two shapes because the CLIs
  mcp-config.claude.json  disagree: opencode reads the first, `claude --mcp-config` the second
  settings.budget.json   Generated agent settings carrying this session's budget hook
  supervisor-fires.log   The supervisor's hourly cost fuse — a file, so it survives the fresh
                          process every `watch` wake starts
  soft-break             Signal file: the run asked the live session to wrap up. `soft-break.delivered`
  soft-break.delivered    records that the session was told, so it is not nudged twice
  logs/
    session-NNN.jsonl           Raw agent stream per session
    session-NNN.prompt.md       Exact prompt each session got
    conductor-YYYYMMDD.json     Structured JSON log (for `conductor log --query`), with a .log twin
    crash-*.log, detach-*.log   Crash net output; `run --detach` startup log
  sessions/              Per-session dossier — NNN/{prompt,handover,transcript,verdict}.md +
                          cost.json, with an INDEX.md over them
  handovers/             Phase-end audit handover documents (versioned)
  evidence/              Where checkpoints put the artifact `task --done --evidence` points at
  reviews/               Review-workflow artifacts (`kind: "review"` steps, which mutate nothing)
  bg-logs/               Output of commands started with `conductor bg`
  queue/                 Injected instruction chain for the next session
  lanes/                 Analysis lane artifacts
  audits/                Post-hoc audit replay outputs (`conductor audit --replay`)

  events.jsonl           LEGACY. The event-sourced backbone through M-era; nothing constructs its
                          writer any more, so a live run does not produce this file — the spine is
                          run.db's `events` table (SqliteRunStore is the registered IEventSink).
                          Several readers still fall back to it so an old run.db-less state dir can
                          be replayed
  state.json             LEGACY resumable-state carrier (pre-M2). A couple of standalone verbs
                          (e.g. `conductor gate`) still write it; the live run loop never does.
                          RunState.LoadOrNew reads it first and falls back to run.db, so it is
                          harmless stale or absent
```

The last five directories are created on demand — a run with no `bg` command, no injected
instruction and no analysis lane simply has no `bg-logs/`, `queue/` or `lanes/`.

When something looks wrong, `session-NNN.prompt.md` is usually the file that explains it — it is the
exact text the agent received, not a reconstruction. See [troubleshooting.md](troubleshooting.md).
