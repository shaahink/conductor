# CONDUCTOR-SARBAN — the era where conductor learns to say what it knows

> *Sārebān (ساربان): the caravan driver — the one who walks the night watch so the caravan
> moves while everyone sleeps.*

**Mission.** Three real runs ($224, $334, and a live round four) produced three field reports and a
driver's field log. Their verdict is consistent: **the engine's decisions were sound; what it *says*
is not.** Runs were judged healthy while parked, parked while healthy, "FULL battery green" was
logged when no battery existed, Telegram reported working while delivering nothing, and the one
surface a human reads — the transcript — stores truncated JSON nobody can parse. This era closes the
gap between what conductor knows and what it tells the person (or agent) watching it — then builds
the watcher itself.

**At a glance.**

| Plan | Stages | Checkpoints | Cap | Tracker | Driven by |
|---|---|---|---|---|---|
| `plans/conductor-sarban-core.plan.json` | SC1–SC8 | 26 | $260 | `SARBAN-CORE-TRACKER.md` | the engine published from this branch at launch |
| `plans/conductor-sarban-face.plan.json` | SF0–SF7 | 24 | $350 | `SARBAN-FACE-TRACKER.md` | the engine republished after core lands |

Core: SC1 Telegram · SC2 truthful surfaces · SC3 config traps · SC4 verdict correctness ·
SC5 wait/detach/board · SC6 clean history · SC7 structured transcript · SC8 version + update.
Face: SF0 the core run's leftovers · SF1 shed dead weight · SF2 honest state/time/money ·
SF3 cheap session reading · SF4 human queue · SF5 supervision · SF6 prompt bank ·
SF7 ship the era (owner-gated).

**Sources of truth** (read the one your stage cites, not all of them):

- `docs/dev/FIELD-NOTES-2026-07-29-devcontext.md` — 20 findings, DevContext graph-v2 run
- `docs/dev/FIELD-NOTES-2026-07-29-sk-platform.md` — 8 findings + proposals table, sk-fleet 09.6
- `docs/dev/FIELD-NOTES-2026-07-30-sk-fleet-round-four.md` — 5 findings, live round-four log
- `docs/dev/GAP-ANALYSIS.md` — the owner-commissioned defect map
- The owner's screenshot critique, folded into Part II below
- The `watch-run` skill (`.claude/skills/watch-run/SKILL.md`) — the supervision spec SF5 productizes

> The three `FIELD-NOTES-*.md` files stay **untracked** — they carry private client context and this
> repo is public (the same scrub rule as commit 5cf77f1). They exist on the dev machine; sessions
> may read them from disk. SF7.1's closure ledger appends into those local files or a sanitized
> summary — it must not commit them.

**How this era runs.** Two plans, self-hosted, in order:

1. **`plans/conductor-sarban-core.plan.json`** (stages SC1–SC8) — engine correctness, Telegram,
   versioning + update. Run FIRST, driven by the engine published from this branch at launch.
2. **`plans/conductor-sarban-face.plan.json`** (stages SF0–SF7) — the core run's leftover ledgers,
   face overhaul, human queue, supervision, prompt bank, era close. Launched only AFTER the core
   plan lands and `tools/install.ps1` republishes — so the improved conductor drives its own
   remaining development. **SF0 was added 2026-07-31**, after the core run finished, to own what it
   left behind: eleven open bugs in a run-scoped ledger the next run cannot see, and the
   `followups.md` rows whose owning stages have all closed.

**Era discipline (every session, non-negotiable):**

- Branch: `feat/sarban`. Commit per checkpoint, push. Trailer convention as in recent history.
- **Never run `tools/install.ps1` mid-run.** The engine driving you is the published copy; replacing
  binaries under a live run is undefined. The owner reinstalls between plans.
- **This repo's `.conductor/` belongs to the run driving you.** The claim/note verbs
  (`task`, `note`, `bug`, `bg`) and read-only verbs (`status`, `task --list`) are yours; never aim
  run-control verbs (`run`, `pause`, `resume`, `abort`, `approve`, `plan set/reload`, `goto`,
  `skip`, `rollback`, `kill`) at this repo. Live-run proofs spawn YOUR build against a scratch repo
  with its own tiny plan and its own `.conductor` (e.g. `%TEMP%\sarban-proofs`).
- **The `conductor` on PATH is the published engine, not your working tree.** Exercise your changes
  through the fresh build — `dotnet run --project src/Conductor -- <verb>` or the exe it builds. A
  new verb tested through the PATH shim proves only that the old engine lacks it. Task claims are
  the deliberate exception: they go through the PATH copy because they target the driving run.
- Gate battery (conductor runs it independently; you use the fast loop mid-work):
  `dotnet build Conductor.slnx` · `dotnet test Conductor.slnx --filter <narrow>` · in `face-go/`:
  `go build ./... && go vet ./... && go test ./<changed pkg>`. The ratchet
  (`tools/gates/ratchet.ps1`, Category=Architecture) forbids new analyzer pragmas — never raise the
  ceiling; fix the cause.
- face-go rules: pad plain text then style (never width-format an ANSI string); clamp with
  MaxWidth/MaxHeight; goldens live in `face-go/internal/tui/testdata/golden/` and pin UTC — visual
  changes regenerate them in a separate rebaseline commit. Read `face-go/STYLE.md` before face work.
- Orphaned `dotnet` test hosts flake the suite; `Get-Process dotnet | Stop-Process -Force` between
  full runs is sanctioned (never kill `conductor` itself).
- Keep tracker handoffs, checkpoint titles and plan prose **brace-free** — a literal `{word}`
  reaches the composed prompt and kills the current engine (SC3.3 fixes this; until it lands, the
  discipline stands).

---

# Part I — SC: the core plan (`conductor-sarban-core`)

## SC1 — Telegram actually delivers

**Why.** devcontext #13: every status surface says "working", the feature is entirely dead. The
root cause is now known: `ConductorHost` is a composition-only root — its own doc comment
(`ConductorHost.cs:23-24`) says "no long-running IHostedService" — yet `TelegramService` is
registered as exactly that (`ConductorHost.cs:85-92`), and **nobody ever calls `host.StartAsync()`**
(`RunCommand.cs:114-138` resolves the Orchestrator straight out of the container). So `_started`
stays false forever; every `PushAsync`/`PushSessionEndAsync` returns early, silently; the poll loop
(two-way phone control) never runs. `TestConnectionAsync` bypasses `_started` and the queue
entirely, which is why the Face's Test button succeeds while the feature is dead. All six real
callers are `_ = …` fire-and-forget, so nothing could ever surface it.

- **SC1.1** The engine starts Telegram on every run path. Follow `ControlPlaneServer`'s precedent
  (explicit start next to `server.Start()` in RunCommand, honouring the host's no-hosted-service
  contract) or start the host properly — either way, on a live configured run `_started` is true,
  a session-end push arrives, and two-way `/status` answers. A regression test drives the REAL
  run-start path and asserts the service started — no existing test starts the host, which is
  exactly why this survived.
- **SC1.2** Status tells the truth: `GET /telegram/status` carries a derived `willDeliver` verdict
  (started AND token AND non-empty allowedChatIds) with doctor's sentence when false;
  `POST /telegram/test` routes through the real send queue (or its reply states loudly that it
  bypassed it); `StartAsync` logs on BOTH outcomes — started with interval, or early-returned
  naming the missing half.
- **SC1.3** Late configuration works or says it doesn't: token via `POST /telegram/token` and a
  Telegram block added later via plan edit either take effect without a full engine restart, or
  every surface (status endpoint, Face, doctor) honestly reports "restart required" — including the
  `plan.Telegram == null → NoOpTelegramService` path where today the real service never exists.
  The chat-id bootstrap (`getUpdates` after the owner messages the bot) lands in the setup docs.

## SC2 — Truthful surfaces

**Why.** The engine repeatedly reports last-known or wrong state as current: `status` calls a
healthy run "interrupted mid-session" for the whole exit→verdict window and advises the one command
that would hurt it (round-four F1 finding; `StatusReportBuilder.cs:84-108` scans spawned pids only);
`what hurt` and `attentionReason` are sticky for hours; nine of thirteen stages logged
"CONFIRMED (full battery green)" when **no battery existed** (sk #2); the two log lines around a
phase-gate RED disagree about the attempt number (devcontext #19); `/state` reads $0.00 for a whole
55-minute session then jumps (devcontext #1, sk #5); and when the run completes, the control plane
dies with the process and nothing summarises it (sk #6).

- **SC2.1** `status` never calls a healthy run interrupted: liveness accounts for the engine's own
  work (a gate executing IS liveness), and the interrupted message appears only when the engine is
  actually gone. Regression test covers the verdict window.
- **SC2.2** Failure fields age or clear: `what hurt` and `attentionReason` carry their timestamp (or
  clear when the condition clears). Phase-gate lines emit the same canonical `gates GREEN|RED` token
  the session verdict uses, and the confirmation line distinguishes three honest states — battery
  green (naming the gates), no gates configured for this stage, RED. `doctor` warns on stages with
  zero gates. Attempt numbering is consistent between the phase-RED line and the fix-session line.
- **SC2.3** `/state` shows live spend: in-flight session cost/tokens folded in (or a separate
  `currentSessionCostUsd`), plus `costSpent`, `costCap`, `costRemaining`, `meanSessionCost`,
  `checkpointsRemaining`, and — after any budget approval — both lifetime and window-since-approval
  so the takeover subtraction stops lying (field log 19:03 entries).
- **SC2.4** The run outlives the engine: completion writes `.conductor/RUN-SUMMARY.md` (plan, wall
  clock, sessions, per-stage attempts + cost, spend vs cap, non-Advanced outcomes);
  `conductor report`/`status` work offline from run.db; `conductor log` opens the live log with
  `FileShare.ReadWrite` (round-four #3) instead of crashing; and the three SSE streams stop
  re-reading the entire backlog every second (incremental tail from last offset).

## SC3 — Config traps die at authoring time

**Why.** The skill's silent-failure table is eight rows long and every row is an engine defect with
a cheap load-time fix. The worst: a literal brace in a stage's `notes` killed a 13-hour run at a
stage boundary with the refusal written to stderr only (round-four field log, 12:38) — `doctor`
passed the broken plan.

- **SC3.1** `doctor` FAILS (not warns) when `agent.model` is set but `{model}` appears in neither
  `args` nor `resumeArgs` (devcontext #2 — the single most dangerous trap). Unknown
  `RunIf`/`SkipIf` tokens fail at plan load naming the valid vocabulary
  (`WorkflowEngine.EvaluateCondition` currently defaults unknown → true, devcontext #4).
- **SC3.2** `plan set` refuses an absent leaf key unless `--create`, suggests the dotted path when a
  single-segment key matches exactly one nested leaf ("did you mean limits.maxRunCostUsd?"), warns
  when its rewrite will strip comment lines, and either auto-queues `reload-plan` for a live run or
  prints the exact reload command (field log: two silent failures stacked on one two-word command).
- **SC3.3** Brace safety end to end: plan load validates every stage's `notes`/`promptExtra` for
  unresolvable placeholder shapes so `doctor` catches them pre-launch; at runtime an unresolved
  placeholder PARKS the run (NEEDS HUMAN) with the refusal written to `conductor.log`, never a
  silent stderr-only exit; `{{word}}` escapes to a literal brace for prose that needs one.
- **SC3.4** The advisor works by default or refuses loudly: shipped default args are a working
  headless invocation (the current default launches a bare interactive REPL that hangs 6 minutes
  and returns null — devcontext #3); when `args` is empty the advisor is refused at load with a
  doctor line; `docs/plan-config.md`'s advisor section corrected (it documents a default that does
  not exist).

## SC4 — Verdicts judge the work, not the environment

**Why.** The most expensive wrong verdicts: a battery started 1 second after the agent exited and
failed on the session's own teardown, queuing a paid fix session for a defect that did not exist
(devcontext #12); a session that delivered a checkpoint entirely in a sibling repo scored
**NoProgress** — twice, in a plan written to avoid it (sk #3, field log S4.2); the gate cache
served a 40-minute-old result for a tree that had changed (sibling repo invisible to the key —
and the key also omits the gate's own command text, so editing a gate mid-run serves stale passes);
an injected correction rendered 113 lines BELOW the stale evidence it corrected (devcontext #15).

- **SC4.1** The battery settles before it judges: conductor waits for the session's tracked bg
  children to actually exit before starting gates, and retries a failed required gate once,
  unconditionally, before declaring GatesRed (devcontext #12's analysis: the duration heuristic is
  refuted; the unconditional retry is strictly better). The failure log line carries duration vs
  last passing duration.
- **SC4.2** NoProgress means no progress: the judgement becomes no-commits AND no-newly-DONE
  (`VerdictEngine.cs:355` — today `newlyDoneCount` never enters it), and conductor's own
  `chore(conductor):` commits are excluded from the verdict's commit count (devcontext #14.5).
- **SC4.3** Multi-repo honesty: an optional `satelliteRepos` list the verdict also diffs for
  `hasCommits`; the per-gate cache key covers the gate's own working directory HEAD (or declared
  watch paths) and the gate's command text; `skipIfFresh` accounts for a dirty working tree.
- **SC4.4** Injections outrank stale evidence: queued instructions render at the top of the prompt,
  immediately after the role line; when an injection is queued for a fix session the
  `gateFailures` block is stamped SUPERSEDED (or dropped) so the two never stand as peers.

## SC5 — The engine can wait, detach, and correct the board

**Why.** S4 of the sk run burned **$51.98 — 23% of total spend — re-reading a clock** because a
session has no way to say "blocked until 15:12" (sk #1: three paid sessions re-derived the same
timestamp, then a HUMAN resumed it one minute after the window opened — precisely the job an
orchestrator exists to do). A run's lifetime is hostage to its launching shell (devcontext #16 —
the engine died to an unrelated harness cleanup). The board is one-way from the CLI (round-four #2:
a mis-drag needed a hand-rolled HTTP POST to undo) and `task --in-progress` prints success for a
transition it silently refused (round-four #1).

- **SC5.1** BlockedUntil is a first-class outcome: `conductor task --blocked-until <iso8601>
  --reason <text>` (CLI + MCP) feeds a session outcome the run loop honours by sleeping until the
  timestamp and respawning once — no attempt burned, no fix session queued. Status/state/face all
  show "waiting until T — reason".
- **SC5.2** `conductor run --detach` spawns the engine into its own process group, prints pid +
  control-plane URL, and returns; the launching shell's death cannot take the run. The stall
  warning names the likely cause and remedy (long foreground command → `conductor bg`).
- **SC5.3** The board is two-way and honest: `task --todo | --blocked | --skipped` land through the
  same `TaskWrites` path the other ingresses use; `--in-progress` reports the post-fold status
  exactly as `POST /tasks/update` already does; `task --amend <id> --note <text>` records a
  mid-run acceptance correction (field log S3.2: a checkpoint encoding a false premise had no
  correctable path).
- **SC5.4** `bg` maps cleanly: `bg logs` on an agent row points to the session's actual stream
  (`.conductor/logs/session-NNN.jsonl`); `bg status` runtimes computed in one timezone (round-four
  #4's negative runtime).

## SC6 — Clean history without lying about it

**Why.** Status transitions each land their own commit (three in eight minutes, two four seconds
apart — devcontext #14), the P4 squash meant to clean them up failed at 4 of 6 stage closes with
git's reason discarded (devcontext #20), reported success for a no-op, ran BEFORE the final state
write so even a real squash is re-polluted one second later, and — engine map — a failed rebase
permanently marks the stage squashed (`VerdictEngine.Phase.cs:405` adds before, `:414` never
removes on the non-zero path) while `Git.cs:87-147` is Windows-only PowerShell with unescaped
single-quote interpolation and no `rebase --abort` recovery.

- **SC6.1** Pure status transitions stop landing commits: Idle/Paused/Aborted REPORT.md updates are
  disk-only (state already lives in run.db; the report is regenerable); whatever still commits is
  coalesced, and any squash runs AFTER the final state write of the stage.
- **SC6.2** The squash is honest and safe: works on a dirty tree (stash-around or temporary
  worktree/index), reports what actually happened (`squashed N into 1` / `nothing to squash`),
  logs git's exit code and stderr on failure, un-marks the stage on failure, aborts a half-started
  rebase, and degrades gracefully (with a log line) on non-Windows.

## SC7 — The transcript captures structure

**Why.** devcontext #10, severity "high for usability": every agent event reaches
`transcript.jsonl` as tool name + raw JSON args **truncated at ~150 chars, cut mid-string**
(`ClaudeProvider.cs:74-76` — `ProviderText.Trunc(inp.GetRawText(), 150)`). The data is lossy at
capture: `file_path` and `command` are unrecoverable downstream, so the Face, the timeline and the
report can only ever show escaped JSON fragments. The same capture gap hides out-of-repo writes
from the verdict (devcontext #11). This stage is the enabler for the whole SF3 display layer.

- **SC7.1** Events are stored structured: tool name + extracted fields (path, command, taskId,
  purpose, byte/line counts) with VALUES truncated, JSON never cut; transcript schema v2 with
  back-compat reading of v1 lines; writes outside `plan.repo` are extracted and the session verdict
  notes them (`note: 2 file(s) written outside the repo`).
- **SC7.2** The wire carries readable lines: the provider emits a one-liner per call
  (`Edit LibrarySurfaceRenderer.cs (+12/-3)` · `Bash dotnet build src/App` ·
  `conductor task_update G1.1 -> done`), and a per-session digest (tool mix, files touched with
  counts, claims, bg-start purposes as a storyline, notable build/test commands) is computed,
  stored, and served on `/sessions` — devcontext #10's worked example is the acceptance shape.

## SC8 — The program knows what it is and can update itself

**Why.** There is no version identity end to end: the csproj says 2.0.0, `release.yml` keys off
`v*` tags nothing reads, no verb reports what the installed binary is, and `install.ps1` publishes
silently — after "rebuild before trusting it", the operator cannot confirm the rebuild took (field
log, day one). GAP-ANALYSIS lists "is the run using stale engine code" as a defect that burned
three sessions. The owner asked for: update mechanism and proper automatic versioning.

- **SC8.1** `conductor version` + `GET /version`: semver, git sha, build date — stamped at build
  (`InformationalVersion`), reported by both the CLI and the control plane; `install.ps1` prints
  the version it installed (before → after).
- **SC8.2** Versioning is automatic: tag-height versioning (MinVer or equivalent) so every build
  carries a unique, monotonically ordered version reconciled with the `v*` tags `release.yml`
  builds from; a `CHANGELOG.md` section per release; CI stamps releases so a downloaded binary
  answers `conductor version` with its tag.
- **SC8.3** `conductor update`: checks the latest GitHub release, compares to the running version,
  downloads the matching platform asset, verifies, and swaps binaries safely (rename-dance;
  REFUSES while a run is live); `doctor` gains an update-available line. Face rendering of the
  version pair arrives with SF2.

---

# Part II — SF: the face plan (`conductor-sarban-face`)

**The screenshot critique** (owner-supplied, three frames from the sk-fleet rounds), folded here so
every item lands in a stage:

| # | Observation | Stage |
|---|---|---|
| 1 | Home shows a COMPLETED run header while the panel says "No run attached. Start one:" — two states in one frame | SF2.1 |
| 2 | "mode live — not connected" is an oxymoron; the raw `connectex: No connection could be made…` string is user-hostile | SF2.1 |
| 3 | Budget line "$224.21 / $125.00 · 0% headroom" renders over-budget as zero headroom — after a window reset the comparison is meaningless and silently wrong | SF2.3 |
| 4 | Agent tab: bottom bar reads `$0.00 · ↑0 ↓0` while the pane above shows the session cost $13.07 — two contradictory cost readouts in one frame | SF2.3 |
| 5 | Tool lines are truncated JSON blobs (`Edit {"replace_all":false,"file_path":"C:\\code\\…`) | SF3.1 |
| 6 | Times show `17:47:51` with no date, nothing relative; a run spanning midnight is unreadable | SF2.2 |
| 7 | Sidebar: checkpoint titles truncated at ~28 chars with no way to read them; `4×` attempts marker unexplained; stages render in execution order (S4 after S10) with no cue | SF3.2 / SF2.1 |
| 8 | Workspace paths mix `C:/code/…` and `C:\Code\…` casing | SF2.1 |
| 9 | Disconnected banner is good but has no age ("retrying…" since when?) | SF2.1 |

## SF0 — The ledger closes (the core run's leftovers)

**Why.** The core run finished 26/26 and left two ledgers behind it. `conductor bug list` carries
**eleven open bugs** the run filed against itself (ids 2,3,4,5,6,8,9,10,11,12,13 — #1, #7 and #14
were fixed in flight), and `.conductor/followups.md` still carries the pre-era rows nobody owns.
Neither ledger has a stage that will ever open again, which is the exact failure the 2026-07-28
triage pass described: *a row pointing at a stage that will never open again is a row nobody will
ever clear.*

**The finding that forces this stage to exist** (measured 2026-07-31 while authoring it):
`conductor bug list -p plans/conductor-sarban-face.plan.json` answers **"No run found in run.db.
Initialize the run first."** The bug ledger is **run-scoped**, not repo-scoped — so the moment the
face run starts, those eleven bugs become invisible to every session working in this repo. They are
transcribed into `followups.md` (section "Carried forward from the core run's bug ledger") so the
lane has a durable source; SF0.4 makes the disappearance itself stop happening.

Every bug id below is the core run's ledger id. Full repro text lives in `run.db`'s `bugs` table
(`detail` column) and in the transcribed followups rows.

- **SF0.1 Keys nothing reads, lines nobody can trust.** Bug **#6** (`workflowStep.model` and
  `stage.overrides.model` are read by nothing — a model pinned there is inert) and bug **#11**
  (`plan.verifyEachDelivery` has one reader, `VerdictEngine.ShouldVerify`, which is called from
  nowhere; the live decision is `Qa.EffectiveSkipVerification`) are the same shape as the traps SC3
  was written to kill, and they survived it. Each key is either **wired to its documented meaning**
  or **deleted and rejected at plan load** — never left readable-but-inert, and `doctor` says so at
  authoring time. Bug **#2**: `Run services started: TelegramService` prints even when the service
  early-returned and started nothing. FU-OWNER-12: with no `telegram` block a run logs *nothing*
  about notifications, so a silent chat cannot be told from an undeliverable one — the sentence
  `doctor` and `/telegram/status` already agree on gets logged once at run start, at the same level
  as the control-plane URL.
- **SF0.2 The verdict counts every claim, and names the session it is about to queue.** Bug **#10**:
  a checkpoint claimed during a Verify or Audit session is counted in **no** session's `newlyDone`
  (`ComputeVerdict` returns before `GraphClaimsDuringSession`), so the claim belongs to nobody in
  history, the report, the timeline or `PendingConfirmation`, and the engine-side commit/evidence
  stamp never runs — fix the `rec.GateSummary ?? completed` evidence fallback in the same change or
  it stamps empty evidence over the agent's. Bug **#4**: a phase-gate RED logs `queuing fix session`
  and the next line is `session #N start — Verify` — the attempt number agrees, the session kind
  does not. Bug **#3**: a confirmed LAST stage with a queued verify session **spins the run loop
  forever** instead of completing — the only outright hang on the list. Bug **#8** rides here
  because it is why none of this was caught: `HarnessTests`' `GitRun` splits on spaces, the initial
  commit fails unchecked, and every harness assertion about `NewCommits` is **vacuously true**;
  `SC42NoProgressTests`' params-array `GitRun` that asserts the exit code is the pattern to adopt.
- **SF0.3 Pids and background work tell the truth.** Bug **#9**: `McpTaskServer.IsProcessAliveMcp`
  answers DEAD for a pid it cannot inspect — the exact inversion of the policy SC4.1 established in
  `PidLiveness.LooksAlive` (cannot-inspect means ALIVE), so MCP `bg_status` can mark a live child
  dead. Bug **#5**: `bg status` crashes with a Win32 access-denied on the same uninspectable pid.
  Bug **#12**: `bg start` leaks the caller's stdout handle to the detached grandchild, so piping
  `bg start` blocks until that child exits. Bug **#13**: `bg logs` opens without
  `FileShare.ReadWrite` and cannot read a **live** background log — the one case the verb exists
  for. And FU-OWNER-9, the most consequential row in `followups.md` and the reason this group is not
  cosmetic: a fix session read `locked by: conductor (15300)`, inferred a stale orphan, and killed
  **the conductor that was running it**. The agent side of the tool contract still has no self-PID
  guard. It is now worse than when filed — this machine runs more than one conductor at a time, so
  the pid an agent decides is stale may belong to **another repo's live run**. Deliver the guard and
  the "locked by conductor (PID) usually means the run you are inside" warning in the fix prompt.
- **SF0.4 The ledger closes and stays closed.** Two halves. (a) **Stop the disappearance:** open
  bugs must survive the run that found them — `conductor bug list` from a new run in the same repo
  shows the previous run's open rows (or an equivalent documented export written at run end), and
  `run ended` with open bugs says how many, where. (b) **Reconcile what is left:** every row still
  open in `followups.md` is either fixed, closed with the evidence that closed it, or re-homed to a
  named owner that can still act — no row is deleted. Check the ones the core era may already have
  closed without saying so: **FU-F1-07** (the "exhaustive" completion test hardcodes its verb list)
  looks closed by SC8, whose handoff records *"the verb-parity test now SCANS Program.cs instead of
  a hand-typed list"* — verify and close it with the commit, or say why not. **FU-B10-2**
  (token-per-checkpoint before/after battery collapse, deferred for want of a real model) is now
  answerable from the core run's 28 real sessions in `run.db` — measure it or retire the row.
  `FU-B11-3` stays `HUMAN:` (real credentials, real money) and is stated as such, not silently
  carried.

## SF1 — The face sheds dead weight

**Why.** Owner: "delete this stupid sql query report and its traces. silly." The SQL console is the
**Dev tab** (`face-go/internal/tui/tab_dev.go` — its own header says "report being sql is stupid").
One real coupling: the Report tab's "Verifier scores" section is a canned SELECT through the same
`GET /report/query` endpoint — it needs a real DTO first. Owner also: "we got a few tabs for
observability and report; some might merge and be consolidated" — 13 tabs today.

- **SF1.1** Verifier scores get a real wire type (`GET /scores`) and the Report tab renders it
  without SQL.
- **SF1.2** The SQL console is gone with its traces: `TabDev` (enum, keys, help, goldens),
  `QueryReport` across api/demo/types/messages/model, the `/report/query` endpoint, and
  `conductor report --query`. The MCP `run_query` tool STAYS (it serves `conductor chat`, not a
  report surface). The two non-SQL panels currently inside Dev — the wiring/health internals and
  the per-session token/cost stats — are re-homed (Report or Home), not deleted.
- **SF1.3** Tabs consolidate to one mental model (design note first, then implement): Console folds
  into Agent as a raw-stream toggle; Timeline and Sessions merge into one history surface (a
  session IS a timeline span); target ≤10 tabs; keys remapped via the `tabKey` single source; help
  legend + goldens regenerated.

## SF2 — The face tells the truth kindly (state, time, money)

- **SF2.1** Home reorganized around one question — "what is happening and what should I do":
  connection state becomes one honest line with age ("engine not running — last run COMPLETED 2h ago"
  instead of `connectex…`); the start-a-run instructions appear ONLY when no run exists; a
  last-run summary card (from run.db / RUN-SUMMARY.md) renders when offline; one `Connected`
  definition (today three indicators fight — `update.go:80-153`); paths normalized to one casing;
  the disconnected banner carries its age. Home stays non-scrolling — new content declares a shed
  tier.
- **SF2.2** One shared time formatter (`internal/timefmt`): local time + relative age
  ("14:32 · 2h ago"), date appears when not today; the Timeline "UTC" mislabel
  (`tab_timeline.go:139` — local time labelled UTC) dies; the never-rendered timestamps render
  (ledger/bug created-at, session start/end, telegram lastPoll age); one process-runtime
  implementation replaces the two.
- **SF2.3** Money honesty: over-budget renders as "OVER by $99.21", never "0% headroom";
  window-vs-lifetime distinguished after an approval (from SC2.3's fields); the top bar shows
  in-flight session cost live (kills the `$0.00` beside `$13.07`); the sidebar's `N×` attempts
  marker gets a legend and per-stage cost/attempts read consistently.

## SF3 — Reading a session becomes cheap

**Why.** Owner: "the agent output display can have another layer to parse those aggregated text and
show useful summaries for someone who is watching… fold is nice. still we are not fully clear with
kanban on where we at." SC7 put structure on the wire; this stage spends it.

- **SF3.1** The digest layer: tool calls render as one-liners (from the v2 wire); a per-session
  digest panel (tool mix, files touched, claims, bg-purpose storyline — devcontext #10's example is
  the target shape) lands in the merged history surface and the report; fold stays and becomes
  rune-safe (`foldTools`' byte-slice truncation corrupts multi-byte glyphs).
- **SF3.2** Kanban says where we are: cards grouped by stage with the active stage highlighted; card
  meta visible without selection (session #, in-progress-since, attempts); column headers show
  n/total; skipped separated from Done; in-column scroll; a "you are here" ribbon — stage x/y,
  checkpoints n/m, next gate, current session — so the board answers the owner's actual question.
- **SF3.3** Git awareness: the engine serves branch, dirty/clean, ahead/behind, HEAD sha, and
  last-commit subjects; the face shows a branch chip in the top bar, repo state on Home, and commit
  subjects in session history. Sidebar carries a cue when execution order diverges from declared
  order.

## SF4 — The human queue is a first-class surface

**Why.** Owner: "i liked the manual list created for me… feels like conductor could do with this,
displaying what human need to do." The reference is `SHAHIN.md` from the sk-platform round: items
phrased as *the things only you can do*, each saying **what it unblocks**, kept current at every
close-out. Conductor already knows most of it: `HUMAN:` lines, ownerGate stages, parks and their
reasons, blocked-until waits, credential-gated checkpoints.

- **SF4.1** The engine collects owner-work into `.conductor/OWNER-QUEUE.md` + `GET /owner/queue`:
  every open HUMAN: line, ownerGated stage, park (with reason + age), blocked-until wait, and
  explicitly-marked owner item — each entry carrying what it unblocks and the exact command or
  click that clears it. Regenerated at every session boundary; items clear when their condition
  clears.
- **SF4.2** The face surfaces it (Home section when short, its own view when not) with age and
  unblocks; a NEW queue item triggers a Telegram push (via SC1) — the away-from-keyboard case is
  the whole point.

## SF5 — Supervision without a polling meter

**Why.** The owner babysat runs with an agent polling a log tail — and named the flaw precisely: a
polling babysitter wastes tokens on **accumulation**, not on the polls; over 10 hours ~95% of ticks
say "still running". The optimal shape is event-driven: **waiting is a shell condition, zero
tokens; the expensive model thinks only at moments needing judgment.** The wake set is small and
the don't-wake set matters as much (usage-limit backoffs self-resume — 2 of the last 3 events on a
real run were exactly this). The watch-run skill is the spec; the primitives (watchdog, circuit
breaker, control inbox, SSE) all exist — this is composition, not new mechanism. GAP-ANALYSIS
names the gap: "the watch-run skill exists precisely because these rails don't hold."

- **SF5.1** `conductor watch` — a blocking verb that subscribes to the run's events and returns
  (or fires a hook) ONLY on the wake set: NEEDS-HUMAN / pauseOnBlocked park, circuit breaker,
  budget/token-cap park, phase gate RED twice on one stage, engine process gone, run ended. It
  stays silent through usage-limit backoff, stall backoff, session start/exit, gate PASS, phase
  advance. `--json` emits a compact brief (~30 lines): what fired, run state, spend vs cap, stage
  board, suggested verbs. `--timeout <min>` provides the long-fallback heartbeat.
- **SF5.2** The supervisor hook: a `supervisor` plan block naming a command conductor runs on wake
  with the brief on stdin (the babysitter agent — e.g. a headless claude invocation); zero cost
  while quiet; `docs/operating.md` gains the wake/don't-wake table and the standing-order pattern
  (what the babysitter may decide alone vs must escalate).
- **SF5.3** Remote supervision documented and proven once: the Telegram wake path (SC1 + SF4.2),
  and the cloud pattern — `conductor watch` fires a webhook/notification that reaches a cloud
  Claude Code session with repo access, which reads the brief and acts via the control verbs. One
  spike proof (a wake reaching a remote listener) plus an honest write-up of what stays manual.
- **SF5.4** Fleet basics, because the websites are plural: `conductor ps` lists every run on the
  machine (repo, plan name, run id, port, pid, status) by scanning control-plane discovery files;
  process titles carry repo + run id; the face gains a run picker when more than one control plane
  answers. Concurrent runs already coexist (field log) — this makes them visible.

## SF6 — The prompt bank compounds

**Why.** Owner: "optimise the prompt bank and enrich based on the new lines of work." The bank
today: built-ins as C# string literals (`PromptBuilder.cs:224-420`), three era template sets +
9 personas + 2 packs under `plans/`, only session+fix scaffolded by `init`. The field notes carry
concrete prompt lessons a fresh bank should encode; the sk round's SHAHIN.md voice ("what it
unblocks") measurably worked on the owner.

- **SF6.1** The built-in session/fix templates carry the field lessons: mark in-progress FIRST
  (devcontext #9 — the board sat all-TODO for 56 minutes); claim BEFORE writing the handoff; the
  deferred-MCP note with the CLI fallback on the same line (devcontext #8); long commands under
  `conductor bg` (devcontext #5); on multi-repo plans, at least one anchor-repo commit per session;
  brace discipline.
- **SF6.2** The bank reorganized and enriched: stale persona/pack content pruned; new material from
  the rounds folded in (the dated proof-note pattern for sibling-repo work, owner-block alternate
  completions written INTO checkpoint acceptance, the unblocks-voice for anything human-facing);
  an index doc says what each persona/pack is FOR so the bank is choosable, not archaeological.
- **SF6.3** `conductor init` scaffolds the refreshed set (all templates it should, not just two),
  wires telegram + supervisor hints into the scaffold, and its output passes doctor clean.

## SF7 — Ship the era

- **SF7.1** Docs reconciled with reality: `plan-config.md` advisor default (wrong today),
  `tracker.md` runtime-files table (documents files runs don't produce), `operating.md` gains the
  supervision section, `NEXT-FEATURES.md` refreshed against what now exists, the three field-notes
  files gain a closure ledger (finding → stage that fixed it → commit); era CHANGELOG written.
- **SF7.2** `feat/sarban` merges to master — **owner-signed** (ownerGate) — the release is tagged
  through the SC8 pipeline, and `tools/install.ps1` refreshes the installed binary. The era closes
  with `conductor version` reporting a version that exists on the releases page.

---

# Appendix A — owner ask → stage traceability

| Owner ask | Stage(s) |
|---|---|
| "telegram fix" | SC1 |
| "core fixes first that affect the program" | SC1–SC7 |
| "update mechanism and proper versioning auto" | SC8 |
| "delete this stupid sql query report and its traces" | SF1.1–SF1.2 |
| "tabs… merge and consolidated" | SF1.3 |
| "home can be more organized and informative" | SF2.1 |
| "show date time friendly way" | SF2.2 |
| "agent output… another layer… useful summaries; fold is nice" | SC7 + SF3.1 |
| "still not fully clear with kanban on where we at" | SF3.2 |
| "git detection and awareness. indicators" | SF3.3 |
| "conductor displaying what human need to do (SHAHIN.md)" | SF4 |
| "improve the agent supervised mode… wake on demand" | SF5.1–SF5.2 |
| "cloud claude session supervise it" | SF5.3 |
| "tweak multiple websites in parallel" | SF5.4 |
| "optimise the prompt bank" | SF6 |
| "branch management and so on" | era branch + SF7.2, SC6 |
| screenshots critique | SF2, SF3, Part II table |
| "set up the rest of the run for the leftovers" (2026-07-31) | SF0 (core-run bug ledger + open followups) |
| dogfooding the v0.2.0 install (2026-07-31) | FU-OWNER-10→SF3.3 · FU-OWNER-11→SF4.2 · FU-OWNER-13→SF4.2 · FU-OWNER-12→SF0.1 |

# Appendix B — field-notes finding → stage index

devcontext: #1→SC2.3 · #2→SC3.1 · #3→SC3.4 · #4→SC3.1 · #5→SC5.2 · #6→SC3.2 · #7→SC1.2 ·
#8→SF6.1 · #9→SF6.1 · #10→SC7 · #11→SC7.1 · #12→SC4.1 · #13→SC1 · #14→SC6 · #15→SC4.4 ·
#16→SC5.2 · #17→SF7.1 · #18→SC2.2 · #19→SC2.2 · #20→SC6.2
sk-platform: #1→SC5.1 · #2→SC2.2 · #3→SC4.2 · #4→SC4.3 · #5→SC2.3 · #6→SC2.4 · #7→SC2.3
round-four: #1→SC5.3 · #2→SC5.3 · #3→SC2.4 · #4→SC5.4
field log extras: plan set traps→SC3.2 · budget window semantics→SC2.3 · engine liveness→SC2.1 ·
brace landmine→SC3.3 · squash silence→SC6.2 · no version verb→SC8.1 · MCP-absent-in-sessions→SF6.1
(documented; engine-side merge of user MCP config is deliberately OUT of this era — file it in
NEXT-FEATURES with the field-log evidence)

# Appendix C — run discipline for whoever drives these plans

**Before launching the core plan (all free, in order):**

1. **Telegram token.** Set it machine-wide so every process tree inherits it:
   `setx CONDUCTOR_TELEGRAM_TOKEN <token>`, then restart the shell. SC1's proof needs the token in
   the *session's* environment — without it SC1 parks on a `HUMAN:` line. Chat id 99205495 is
   already in both plans.
2. **Publish the branch build**: `powershell -File tools\install.ps1` (done 2026-07-31 — repeat
   only if engine source moved since; NEVER mid-run).
3. **Working tree**: commit plan/tracker/spec edits to `feat/sarban`. The doctor warn about the
   untracked `FIELD-NOTES-*` files and `plans/shamshir-templates/` is expected — those stay
   untracked deliberately (private content, public repo).
4. **Pre-flight**: `conductor doctor -p plans\conductor-sarban-core.plan.json` (0 fail, and after
   step 1 the telegram warn must be gone) → `conductor journey -p …` (Model column reads
   claude-opus-5, never "(default)") → `conductor run -p … --dry-run`; read the whole first
   prompt — it must open with the sarban-templates session text (a silent fallback to built-ins
   means `templatesDir` broke) — then sweep it:
   `[regex]::Matches($out,'\{[A-Za-z_][A-Za-z0-9_]*\}')` must return nothing. After ANY plan
   edit: dry-run again.
5. **Launch detached with stderr redirected** (a prompt refusal is stderr-only until SC3.3 lands):

   ```powershell
   Start-Process conductor -ArgumentList 'run','-p','plans\conductor-sarban-core.plan.json','--headless' `
     -WorkingDirectory C:\code\conductor -WindowStyle Hidden `
     -RedirectStandardOutput $env:TEMP\sarban-core-out.txt -RedirectStandardError $env:TEMP\sarban-core-err.txt
   ```
6. **Arm the log-tail monitor** with the conductor-drive skill's canonical filter. During the core
   run Telegram cannot push — the driving engine still has the bug SC1 fixes — so the monitor is
   the only wake signal. A `stage → X` line with no `session #N start` within two minutes means
   the engine is gone: read the stderr file from step 5.

**Budget:** caps are the authorised totals ($260 core — raised to $450 mid-run after one park;
$350 face, authorised 2026-07-31 when SF0 was added, sized from the core run's realized $12.86 per
session across 21 declared sessions plus the slack the core run actually used). If more is authorised, raise
EARLY and past the tripwire while the counter is still honest (`plan set limits.maxRunCostUsd <n>`
then `plan reload`). `approve` on a budget park RESETS the window counter — before approving, set
the cap to the *remaining* allowance.

**Between plans, in order:**

1. ~~Verify core landed~~ **DONE 2026-07-31 18:33**: `conductor version` answers and a Telegram test
   push arrived (message_id 18) — SC1's fix confirmed live.
2. ~~Reinstall~~ **DONE 2026-07-31 18:33**: `0.1.1-alpha.0.57+2fea7032749d`, since superseded by the
   v0.2.0 release build `0.2.0+f638ba6f7f14`. Kill any `conductor face` / `conductor-face` first —
   they lock the install dir.
3. **Re-derive the monitor filter from the NEW engine's log strings** before arming it for the
   face run — SC2.2 canonicalises the gate vocabulary and SC3.3 turns the brace exit into a park,
   so the old filter's tokens may no longer match. Grep the source for the actual `Log($"…")`
   lines; never reuse a filter on faith.
4. Pre-flight and launch the face plan exactly as steps 4–6 above (swap in the face plan path and
   `sarban-face-*.txt` redirect names).

**The face run launches onto a machine that is already running conductor** (from 2026-07-31: the
NINE STREETS plan in `C:/Code/sk-studio`, headless on port 4317, plus the owner's `conductor face`
attached to it). That is supported — concurrent runs coexist, which is what SF5.4 makes visible —
but it changes four things, and every one of them is a way to break **someone else's** run:

1. **`tools/install.ps1` is off the table for the whole run**, not just "not mid-run". Both runs
   execute the same published binaries; republishing swaps the engine underneath a live third-party
   run and the publish itself fails on the locked files.
2. **Never kill a `conductor.exe`, `conductor-face.exe` or stray `dotnet` process by pid.** Trap 6's
   "stopping stray dotnet processes between full runs is sanctioned" was written for a single-run
   machine; a `dotnet` host here may belong to the other run's gate battery. Check the command line
   (`Get-CimInstance Win32_Process`) and leave anything whose path is not this repo alone. This is
   FU-OWNER-9 as a live hazard rather than a filed row.
3. **Scratch-rig proofs must not collide.** The other run holds port 4317; give every rig under
   `%TEMP%\sarban-proofs` its own `--port` and its own state dir, and read the port back from the
   rig's own discovery file rather than assuming a default.
4. **Both runs draw on the same subscription.** Usage-limit backoffs will be more frequent and are
   NOT an intervention — they self-resume. Check `/usage` before launching, not after the first
   backoff.

**Token at launch (this bites every time):** `CONDUCTOR_TELEGRAM_TOKEN` is set user-wide, so only
shells started *after* that `setx` inherit it. Any agent harness shell older than it must read the
value across without echoing it:

```powershell
$env:CONDUCTOR_TELEGRAM_TOKEN = [Environment]::GetEnvironmentVariable('CONDUCTOR_TELEGRAM_TOKEN','User')
Start-Process conductor -ArgumentList 'run','-p','plans\conductor-sarban-face.plan.json','--headless' `
  -WorkingDirectory C:\code\conductor -WindowStyle Hidden `
  -RedirectStandardOutput $env:TEMP\sarban-face-out.txt -RedirectStandardError $env:TEMP\sarban-face-err.txt
```

`conductor doctor` warning `telegram configured but no bot token` in a given shell means that shell,
not the machine — check with `[Environment]::GetEnvironmentVariable(...,'User')` before believing it.
