# Changelog

All notable changes to Conductor are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**This file is not decoration — it is load-bearing.** The version a binary reports is derived from
the `v*` git tags by MinVer (`src/BuildStamp.targets`, `MinVerTagPrefix` = `v`), and
`.github/workflows/release.yml` refuses to publish a tag whose section is missing from this file,
using the section as the release body (`tools/changelog-section.sh`). So: add to `[Unreleased]` as
you work, and rename it to the version when you tag.

A section that quotes a run's own score is quoting a **measurement with a date on it**, not a
constant: every further session moves it. Re-run `conductor budget` and `conductor money` when you
rename the section — the section is what the world reads on the releases page.

Between releases, `conductor version` answers with a tag-height prerelease such as
`0.1.1-alpha.0.54+1c2330f5a47e` — patch bumped, `alpha.0.<commits since the tag>`, plus the commit
it was built from. It orders above `0.1.0` and below `0.1.1`, and it is unique per commit.

## [Unreleased]

### Changed

- **Licence is MIT.** Conductor is now plain MIT — free for any use, commercial included. The
  PolyForm Noncommercial 1.0.0 text that briefly sat in `LICENSE` was a mistake and is gone; no
  version of this software is under a noncommercial grant.
- **The one-line description says what Conductor is.** It is an engineering tool that turns a plan
  into verified, committed work; running unattended is a consequence of the verification, not the
  pitch. README badge, tagline and licence section updated to match.

## [0.4.0] - 2026-08-05

**The Karvan core era** — the engine knows what it did and what it cost. 0.3.0 made a run visible;
this era makes it *accountable*. Every claim conductor makes about itself — what a session landed,
what it spent, where its state lives, what its own docs say — is now measured by the engine and
checkable against the ledger, because in the run that produced 0.3.0 several of those claims were
wrong and nothing caught them. Built the same way as the last two: conductor driving itself against
this repo, unattended, with every checkpoint confirmed by an independent gate battery rather than by
the agent that claimed it.

Its own score, produced by the tools this era shipped (`conductor budget` and `conductor money`, measured
at the tag): **24 checkpoints at 16.8M tokens and $13.24 each, zero rollovers in 30 costed sessions**
— against the previous era's 17.0M, $14.86 and 30%. The run cost $317.84 for 403.9M tokens, 98.3% of
them cache reads.

### Added

- **`conductor budget` — the token budget stops being folklore.** It reads a run's own ledger, splits
  it at the session where a ceiling took effect, and prints floor, wrap-up, cap, nudge-versus-floor,
  nudge-versus-median-closer and rollover rate for each window — then *prescribes* a `limits` block
  to paste. Run against this repo it found four wrong numbers in `docs/dev/TOKEN-BUDGET-TUNING.md`
  and one rule that was too weak; all five are corrected in place.
- **`conductor money` — what a project cost.** Per checkpoint, per stage and per month, with the
  cache-read share and the before-and-after windows that say what a cap bought.
- **Context size per turn.** A high-water and a mean per session, derived from the stream, so the
  thing everyone tunes by feel has a number. Live session tokens, the distance to the nudge, a burn
  rate and a projection sit beside live money in the Face and on the wire — honest when no cap is set.
- **`conductor history`, and state that outlives a repo.** State has a machine-level home with a
  catalogue keyed by repo and plan, an environment override, and a migration that *imports* existing
  `run.db` files rather than orphaning them. Past runs open read-only from the catalogue, and the
  Face's run picker offers them.
- **Run provenance.** Every run records the engine version, its commit, its dirty flag and a snapshot
  of the limits that governed it. A dirty build warns at launch.
- **Evidence as a first-class artifact.** Path, kind, checkpoint, session, sha, created-at — written
  as an event when an agent registers one or a watched directory gains a file, with non-text kinds
  first-class and a Face surface. The existing free-text evidence field still works.
- **A message-composition layer for Telegram.** Owner-editable per-event templates, repo and branch
  and stage title and checkpoint in every push, commits and PRs as links, money with headroom, photo
  and document sending so evidence arrives, a thread per run, severity mapped to notify-or-silent,
  and 4096-character chunking. ADR 0005 records the push-only remote posture.
- **`ARCHITECTURE.md`, and tests that keep it true.** A real map of the tree with a file-organisation
  convention, backed by architecture tests in the ordinary suite that fail the build when a boundary
  is crossed, each naming the offending type and the rule.
- **ADR 0006 — TUI conventions**, written after an actual read of glow, soft-serve, gh-dash and
  lazygit: pager keys, focus model, help, one scroll idiom, viewport versus list versus table.

### Changed

- **`Conductor.Core` is a library.** The domain, orchestration and store moved out of the CLI with no
  Spectre and no HTTP hosting left in them; `Conductor` is CLI plus hosting. The reference direction
  points one way and a test says so. The thirty-file DTO pile became per-feature endpoint contracts.
- **The session result has one format conductor owns** — short headline, at most three outcome
  bullets, artefacts, evidence paths, explicit gaps. Five consumers used to cut the same paragraph at
  five different lengths, mid-word; they now read fields. A legacy result degrades rather than throws.
- **The Face's tabs own themselves.** Each tab has its own model, state, update and view, so the root
  update is a dispatch instead of 826 lines and 80 cases; the mnemonic map and the help legend are
  derived from one source. `bubbles` v2 is a declared dependency and four panes scroll through a real
  viewport. One theme-aware markdown renderer serves everywhere markdown belongs, and it memoises —
  200 frames of unchanged prose invoke glamour once.
- **The front door reads.** `AGENTS.md` cut to current state with superseded handoffs archived and
  indexed, closed-era trackers out of the repo root, the divergent duplicate workgraph doc resolved
  to one file, and the docs indexes updated.

### Fixed

- **A rolled-over session records what it actually did.** `SessionRunner` used to set the outcome and
  return *before* the pass that populates commit count and closed checkpoints, so every rollover
  reported nothing landed — on this repo's own history, ten of eleven rollovers had in fact committed.
- **The soft break is re-stated until it is obeyed**, names the actual remaining budget, and states
  the wrap-up order (claim first, handoff second). The session record now says whether it was
  delivered, re-delivered and obeyed. In the 0.3.0 run the rail converted **zero of ten** kills.
- **The five Telegram defects that made the feed unreadable**: two identity blocks from two sources,
  a stage id with no title, the structured result cut mid-word, a rollover that reported nothing, and
  pushes with no progress line.
- **A spawned session sees the operator's own MCP servers.** The per-session config is now a merge
  with whatever the machine already has, not a replacement that named only `conductor-tasks`.
- **Three small untruths died as a class**: a thinking-token column that was zero on all 125 rows, a
  lessons file that was a diary and repeated one entry twice, and a `go.mod` that called a
  directly-imported package indirect while carrying two lipgloss majors.
- **Face scroll offsets no longer run away past the end of a document** — 389 keystrokes into the
  Report pane used to leave it blank. Four panes, one clamp, one idiom.
- **`conductor demo` no longer leaves anything on your machine.** It has always deleted its throwaway
  repo; since this era moved `run.db` to a machine-level store it had been leaving the database, and a
  permanent `conductor history` row pointing at the directory it had just deleted, behind on every
  run. The demo's state now lives and dies inside the throwaway directory. (Windows also used to be
  told to "delete it by hand" — a pooled SQLite handle held the file open past cleanup.)
- **The rehearsal the README points contributors at is green again.** Moving `run.db` broke the shared
  helper the live-control-plane rigs read their evidence through, so five of its checks had been
  failing on a perfectly healthy engine, with a message that blamed the engine. Those rigs also wrote
  their throwaway runs into the operator's real store; they now keep them in their own scratch.
- **The front page describes the Face that ships.** It advertised eleven tabs and named three that had
  been merged away an era earlier, while omitting the one the Face opens on. Its session-outcome table
  was missing `AuthFailed` — a dead credential parks the run for good — along with `BlockedUntil` and
  `AgentError`. Both lists are now checked against the source by a test.

## [0.3.0] - 2026-08-01

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
