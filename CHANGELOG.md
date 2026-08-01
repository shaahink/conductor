# Changelog

All notable changes to Conductor are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**This file is not decoration — it is load-bearing.** The version a binary reports is derived from
the `v*` git tags by MinVer (`src/Conductor/Conductor.csproj`), and `.github/workflows/release.yml`
refuses to publish a tag whose section is missing from this file, using the section as the release
body. So: add to `[Unreleased]` as you work, and rename it to the version when you tag.

Between releases, `conductor version` answers with a tag-height prerelease such as
`0.1.1-alpha.0.54+1c2330f5a47e` — patch bumped, `alpha.0.<commits since the tag>`, plus the commit
it was built from. It orders above `0.1.0` and below `0.1.1`, and it is unique per commit.

## [Unreleased]

**The Sarban face era** — the watcher and the surfaces. 0.2.0 shipped an engine that could be trusted
to run unattended; this era is about being able to *see* what it did, and to be told when it needs
you. It was built the way the last one was: conductor driving itself against this repo, unattended,
with every claim verified by an independent gate battery rather than by the agent that made it.

The 0.2.1 and 0.2.2 releases were cut mid-era for the token-budget rails and are not repeated here.

### Added

- **`conductor watch` — supervision that belongs in the plan.** A babysitter blocks on the run's own
  file rather than polling a model, so watching a run overnight costs nothing. The wake conditions
  are plan config, not a line of shell history, and the wake can leave the machine: it reaches a
  remote listener over HTTP, proven end to end against a listener that is not conductor. What stays
  manual is written down rather than implied.
- **`conductor ps` — the fleet is visible.** Every run on the machine, read from the control-plane
  discovery files. Engine processes now carry the repo and run id in their process title, so a stray
  `conductor.exe` in Task Manager can be identified before it is killed. When more than one control
  plane answers, the Face probes, leads with the likely one, and asks rather than guessing.
- **An owner queue — the things only you can do.** Decisions that need a human are collected, served
  on the wire, and rendered as a surface in the Face instead of being buried in a log line. A queue
  item that arrives while you are away pushes to Telegram.
- **A session digest, and cards that say who moved them.** What a session actually did — tool mix,
  files touched, what it landed — rendered from the wire. The board's cards carry who moved them,
  when, and how many times.
- **Build identity on the wire.** `GET /state` carries the engine version, commit and Face build, and
  the Face shows them in the top bar and on Home. "Did my reinstall take?" is now answerable from the
  screen (`FU-OWNER-10`).
- **A real endpoint for verifier scores**, so a rendered report no longer needs SQL to exist.
- **`conductor init` scaffolds the whole prompt bank**, with commented advisor and telegram blocks,
  and its output passes `doctor` clean.

### Changed

- **Twelve Face tabs became ten, and the SQL console is gone.** The Dev SQL console, `/report/query`
  and `report --query` are removed; MCP `run_query` stays for chat, and the two Dev panels that were
  never the problem were re-homed rather than deleted. Console folded into Agent as a raw toggle;
  Sessions and Timeline merged into one History surface.
- **One clock vocabulary.** Local time with a relative age and a date when it is not today, from a
  single shared formatter. The Timeline's UTC mislabel is fixed, and three timestamps that were on
  the wire and on no screen now render.
- **Money is honest.** Over-budget renders as `OVER` in dollars rather than as zero-percent headroom,
  window spend is distinguished from lifetime spend, and the top bar shows in-flight session cost
  live.
- **Telegram pushes carry identity.** Every push, digest, command reply and test message is stamped
  with the plan name and session number at the single point they all pass through, so one chat
  receiving two machines' runs is readable (`FU-OWNER-11`).
- **The shipped prompt templates carry the field lessons** — claim before handoff, long commands under
  `conductor bg`, the deferred-MCP fallback, the anchor-commit rule for multi-repo plans — and the
  prompt bank is pruned, indexed and choosable.

### Fixed

- **The thirteen bugs the previous run left open.** Inert plan keys (`workflowStep.model`,
  `stage.overrides.model`, `plan.verifyEachDelivery`) are now either wired to their documented meaning
  or rejected at load — never readable-and-ignored. A claim made during a Verify or Audit session is
  counted, stamped and confirmed like any other. A confirmed last stage completes instead of spinning
  forever. One pid-liveness policy everywhere including MCP; `bg start` stops leaking the caller's
  stdout handle; `bg logs` can read a log that is still being written.
- **An open bug now outlives the run that found it.** `conductor bug` was run-scoped: the moment a new
  run started in the same repo, every open bug from the last one silently vanished — no error, an
  empty ledger that looked clean. Carried bugs now appear in `bug list` attributed to the plan that
  filed them, reach the next session's prompt, and are counted at run end.
- **A run says whether it can notify you at all**, once at startup, in the same sentence `doctor` and
  `/telegram/status` give — instead of only answering when asked (`FU-OWNER-12`).
- **A queued plan reload reads as *waiting*, not as *unconfigured*.** Telegram used to advise the plan
  edit you had made and it had accepted seconds earlier (`FU-OWNER-13`).
- **Home's honest connection line was being truncated into a lie**, and Next steps offered a live
  agent when no engine was running.
- **The docs were reconciled with the code.** `tracker.md` documented a `.conductor/` tree in which
  five entries did not exist after thirty-six real sessions and fourteen real artifacts were missing;
  the backlog listed ten shipped features as future work; `operating.md`'s known-gaps list was a
  three-week-old snapshot. All three are now pinned by tests that read the source, so the next drift
  is a red build rather than a re-read.

## [0.2.2] - 2026-08-01

0.2.1 built the rails and left them reading a gauge that was wired to nothing. This is the wire.

### Fixed

- **The live token counters now move while the session runs.** `ClaudeProvider` emitted each assistant
  message's usage to the event stream but never folded it onto the session state, so
  `TokensInput/Output/CacheRead` stayed **null** for the entire session and were first set by the
  terminal `result` envelope. Every rail that asks the live session what it has spent — the
  soft-break and the `maxSessionTokens` ceiling both do — therefore read zero until the session was
  already over. Observed on a real run within an hour of shipping 0.2.1: a session under a 6M ceiling
  reached **17.13M**, and the log recorded the nudge and the ceiling firing in the same second, as the
  agent was exiting anyway. Both rails now fire when they can still change the outcome.
  Accumulation is safe because `TryCountMessageOnce` already rejects the re-emitted content blocks
  that would otherwise count a message three or four times, and `ReadUsage` still ASSIGNS the
  envelope's totals at the end, so the CLI's own number remains authoritative.

Two tests asserted the old behaviour and were rewritten rather than deleted: they encoded the
reasoning that live deltas must not touch the session totals because double-counting would break the
cap. The first half was right; the conclusion was backwards. Leaving those totals null did not
protect the cap, it disabled it.

## [0.2.1] - 2026-08-01

The session budget stops being decorative. Everything below was already configurable, already
documented and already wired to a surface; none of it could change what a run spent. Two live runs
on one machine spent roughly $200 in a night with `maxSessionTokens` set, because every rail between
that number and the agent was open at one end.

### Fixed

- **A plan edit now applies by itself.** `limits` were only re-read on an explicit
  `conductor plan reload`, and nothing said so: the file could read `maxSessionTokens: 6000000`
  while the engine ran the plan it had loaded hours earlier, and the operator would watch sessions
  sail past a ceiling they had already set. The plan file is stamped when loaded and re-applied at
  the next session boundary when it changes. The reload line now names the budget it just put into
  force, so an edit that took hold says so.
- **The per-session token ceiling ends the session.** The check ran only *after* the agent exited,
  which made it a label rather than a limit — a session ran its full length and was then noted as
  having been over budget the whole time. It is now enforced live. This is the change that matters
  most for spend: a session is billed roughly turns × context and context only grows, so the last
  stretch of a long session costs several times the first, and that is exactly the part a ceiling
  has to be able to cut. Measured here, splitting one 164-turn session into three cuts its bill by
  about half for the same work.
- **A budget-killed session reports what it cost.** It emits no result envelope, so the one session
  the rail acts on was the one session reporting $0 — the ledger read as though stopping early were
  free. It is now priced at the run's own observed dollars-per-token, or left blank when no rate
  has been learned yet.
- **The run-level token total counts cache reads.** It summed input, output and reasoning while the
  per-session total also counted cache, so the two disagreed by roughly forty times on real work: a
  run that had read 79M tokens reported 2.9M. Every surface fed from it — the ledger, the report,
  `doctor`'s headroom, and `limits.maxRunTokens` — inherited that, which put a run cap set from
  observed numbers permanently out of reach. Runs carried over from an older engine step up once.

### Added

- **The cooperative soft-break reaches the agent.** It was written as: spend most of the budget,
  then be asked to land the current sub-task and hand off cleanly. Half of it was missing. The
  engine wrote `.conductor/soft-break` and emitted an event, and nothing carried either to the
  agent — a non-interactive session has no inbox and was never told the file existed — so the nudge
  fired into a void every time and the only rail that could still act was the hard one, which is a
  kill. A per-session `--settings` file now attaches a `PostToolUse` hook (`conductor hook-budget`,
  hidden) that speaks once, when and only when the signal is up, riding a tool call the session was
  making anyway.
- **Sessions are told what they may spend.** When a plan sets a per-session ceiling, the prompt
  carries the budget, the arithmetic behind it (turns × context, context only grows) and the few
  habits that actually move the number: commit early and often, read the sections a checkpoint
  names rather than whole design documents, never re-read what is already in context. It names the
  opposite failure too — a session that reads a few files, declares the problem complex and exits
  without landing anything has spent its whole context and delivered nothing.

## [0.2.0] - 2026-07-31

The Sarban core era: the engine says what it knows. Truthful surfaces, board correction verbs,
detach, structured transcripts — and the engine now knows what version it is and can update itself.

### Added

- `conductor version` and `GET /version` report the semver, the git commit, whether the tree was
  dirty at build time, the build date, and **which binary answered** — stamped by the build itself
  rather than typed into a source file, so "is this run using stale engine code" has an answer.
- Automatic tag-height versioning. The csproj no longer carries a hand-typed version number; the
  `v*` tags are the single source of truth, and a release binary answers with its own tag.
- `conductor update` — checks the latest release, and swaps this binary for it. It verifies the
  download against the release's `SHA256SUMS.txt`, then **runs the downloaded engine and asks its
  version** before replacing anything, and swaps by rename so a failure puts the old binary back.
  It **refuses while a run is live**, because every task claim and background start during a session
  spawns the engine again — a mid-run swap means the back half of a session runs on different code.
  `--check` looks without installing.
- `doctor` gained an `update` line: which engine is running and whether a newer release exists. Never
  a failure (an offline machine is not a broken one), memoised for six hours in a user-level cache so
  it costs nothing, and switchable off with `--no-update-check` or `CONDUCTOR_NO_UPDATE_CHECK`.
- Releases now publish a `SHA256SUMS.txt` manifest alongside the platform archives.
- `CHANGELOG.md` (this file), enforced at release time by `tools/changelog-section.sh`.
- `conductor task --blocked-until`, `--todo`, `--blocked`, `--skipped` and `--amend`, so the board
  can be corrected rather than only appended to.
- `conductor run --detach`: the engine takes its own process group, prints its pid and control-plane
  URL, and survives the shell that launched it.
- Per-session digests on `/sessions` — tool mix, files touched with counts, claims, and background
  work as a storyline — built from structured tool events rather than truncated JSON blobs.
- A `RUN-SUMMARY.md` at the end of a run; `report` and `status` work offline from `run.db`.

### Fixed

- The release build could never have compiled. `-p:PublishSingleFile=true` — the flag every platform
  in `release.yml` publishes with — enables the single-file analyzer, and two reads of
  `Assembly.Location` were IL3000 errors under `TreatWarningsAsErrors`. Since that workflow only runs
  on a `v*` tag push, nothing had ever exercised it. Both reads now use `AppContext.BaseDirectory`.

### Changed

- `install.ps1` and `install.sh` print the version they replaced and the version they installed
  (before → after), so an operator can confirm a rebuild actually took. `install.ps1` gained
  `-SkipShim`.
- Telegram starts on every run path, and `/telegram/status` carries a derived `willDeliver` verdict
  instead of leaving delivery to be discovered at the end of a run.
- `conductor status` no longer reports a healthy run as interrupted while a gate is executing.
- Gate verdicts wait for the session's tracked background children, retry a failed required gate
  once, and judge the work rather than the environment.
- `doctor` fails on configurations that used to fail silently at runtime: a model set without the
  model token, unknown `RunIf`/`SkipIf` tokens, literal braces in prompt text, zero-gate stages.
- History squashing runs after the stage's final state write, works on a dirty tree, reports real
  counts, and un-marks the stage when it fails instead of claiming success.

## [0.1.0] - 2026-07-28

### Added

- First public release. Conductor is a stateful execution environment for days-long AI software
  engineering: a .NET orchestrator (`conductor`) that drives a plan of stages and checkpoints one
  verifiable session at a time, and a Go TUI (`conductor-face`) that watches it.
- `conductor demo` — a complete plan driven to a confirmed finish with no credentials and no spend.
- Self-contained release archives for linux-x64, linux-arm64, macos-arm64, macos-x64 and
  windows-x64, each holding the engine and the Face side by side; neither .NET nor Go is needed to
  run them.
- `tools/install.ps1` and `tools/install.sh` for building and installing from source.
