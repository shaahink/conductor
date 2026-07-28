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
in there keeps everything but `REPORT.md` (and audit handovers) out of your history.

```
.conductor/
  run.db                 THE live run state (SQLite, run_state table) — every transition persists
                          here; this is what `conductor run` resumes from, not state.json
  state.json             Legacy resumable-state carrier (pre-M2). Only a couple of standalone
                          verbs (e.g. `conductor gate`) still write it; the live run loop never
                          does. Kept as a fallback RunState.LoadOrNew reads first; harmless if
                          stale or absent — resume falls back to run.db either way
  events.jsonl           Append-only event log (event-sourced backbone — 22 event types)
  REPORT.md              The AFK report — the only file conductor commits
  conductor.log          Orchestrator text log
  control.json           Transient control verbs from the CLI/TUI/Telegram
  conductor.lock         PID lock (two conductors can't fight over one repo)
  lessons.md             Rolling lessons brief (bounded, rotating)
  followups.md           Tracked follow-up items from audits
  queue/                 Injected instruction chain for the next session
  logs/
    session-NNN.jsonl           Raw agent stream per session
    session-NNN.prompt.md       Exact prompt each session got
    conductor-YYYY-MM-DD.json   Structured JSON log (for conductor log --query)
  lanes/                 Analysis lane artifacts
  handovers/             Phase-end audit handover documents
  audits/                Post-hoc audit replay outputs
```

When something looks wrong, `session-NNN.prompt.md` is usually the file that explains it — it is the
exact text the agent received, not a reconstruction. See [troubleshooting.md](troubleshooting.md).
