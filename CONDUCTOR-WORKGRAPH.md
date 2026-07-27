# CONDUCTOR-WORKGRAPH — W-series tracker (One Work Graph)

**Design brief:** `docs/CONDUCTOR-WORKGRAPH.md` (read it + `docs/GAP-ANALYSIS.md` before any
checkpoint). **Plan:** `plans/conductor-workgraph.plan.json`.

## Handoff

**Series opened 2026-07-28.** Plan + brief + tracker authored from the 2026-07-27 gap analysis
(`docs/GAP-ANALYSIS.md`, commit `875169c`). Baseline gate battery GREEN at authoring time:
dotnet build 0w/0e · C# suite 918/918 · face-go build/vet/test green · ratchet OK (38≤38).
One flaky test fixed en route (`HostLoggingTests.DryRunWritesStructuredLogWithRunIdCorrelation`
waited for the first flushed marker, then asserted a later one — now waits for the last).
`conductor journey -p plans\conductor-workgraph.plan.json` validates: 6 stages, 20 checkpoints
parsed (4/3/3/4/2/4). Nothing delivered yet; **next = W1.1**.

Driving mode: Claude Code drives W1–W4 + W6 directly (owner directive 2026-07-16) — per
checkpoint: pre-session ritual (tracker + brief stage section + cited docs, gate battery first,
never build on red) → deliver → gate battery → fill the checkpoint row (Status DONE + Commit +
Evidence) → overwrite this Handoff block → commit + push. W5.1 = conductor drives a toy plan
(credential-free); W5.2 = `HUMAN:` owner-started real-model run.

Owner decisions pending (needed no earlier than the stage that names them):
- `HUMAN:` W5.2 — owner starts + pays for the real-model proof run.
- `HUMAN:` W6.1 — ~~license choice~~ **MIT (decided 2026-07-28, delivered `51911f9`)**;
  `publish/` un-tracked from HEAD same commit. Still open: whether to also purge `publish/`
  from git *history* (`git filter-repo` + force-push — rewrites remote history).

## Checkpoints

| Checkpoint | Title | Status | Commit | Evidence |
|---|---|---|---|---|
| W1.1 | Unify checkpoints + tasks into one event-sourced graph (kind, provenance; checkpoints table → projection; ADR-0002 amended) | TODO | | |
| W1.2 | WorkGraphSync at every boundary (start, reload, plan edit/import, add-stage); coverage validation in CollectErrors + doctor | TODO | | |
| W1.3 | Claims from the graph; tracker demoted to generated view; bug #6 fixed (verify consumes PendingVerify.StageId) | TODO | | |
| W1.4 | One projection for all views (sidebar/chips + Kanban); card moves = claims through legal transitions | TODO | | |
| W2.1 | Claude-shaped MCP config + CONDUCTOR_PLAN in child env; in-worker task/bug/note verbs work | TODO | | |
| W2.2 | Live board mid-session (journal folded on read or direct events; single id allocator) | TODO | | |
| W2.3 | One prompt composition (PromptBuilder renders PromptComposer blocks); ToolContract rewritten to one claim path | TODO | | |
| W3.1 | Independent watchdog timer (hard timeout + stall, bg-only liveness, clock-jump check, hung-session notification) | TODO | | |
| W3.2 | Auth failure first-class (401 classified → auth-park; doctor/preflight auth smoke test) | TODO | | |
| W3.3 | Process rails (CTRL_CLOSE graceful stop; pid-reuse guard; unbounded-spend warning; bg log pump fix) | TODO | | |
| W4.1 | Import carries checkpoints end-to-end; imported plans drivable immediately; deterministic default gates | TODO | | |
| W4.2 | conductor init --from-idea + advisor block scaffold (one command: idea → drivable plan) | TODO | | |
| W4.3 | AI split-into-subtasks on a card; stage-level rough-card add (schedulable) | TODO | | |
| W4.4 | Per-item QA dial (qa: inherit/verify/off on the card, honored by QaPolicy) | TODO | | |
| W5.1 | Credential-free dress rehearsal: imported toy plan driven end-to-end, all in-flight levers exercised, first RunFinished | TODO | | |
| W5.2 | HUMAN: real-model unattended proof run start → RunFinished; five criteria audited in docs/workgraph/W5-AUDIT.md | TODO | | |
| W6.1 | HUMAN: LICENSE; un-commit publish/ (± history purge); .gitignore/.gitattributes hardening | DONE (HEAD only) | 51911f9 | MIT chosen by owner 2026-07-28; publish/ (81 files) un-tracked from HEAD; .gitignore + .gitattributes hardened. History purge NOT done — needs explicit owner go-ahead (rewrites remote history). |
| W6.2 | CI: .github/workflows/ci.yml (windows full battery + ubuntu dotnet/go), born green | TODO | | |
| W6.3 | README overhaul (prereqs, platform, badges, VHS demo GIF); quickstart fixed; docs index | TODO | | |
| W6.4 | Repo hygiene (archive trackers, scrub foreign refs, CONTRIBUTING/SECURITY); merge to master | TODO | | |
