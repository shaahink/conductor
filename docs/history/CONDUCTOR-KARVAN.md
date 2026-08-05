# CONDUCTOR-KARVAN — the era where conductor remembers, counts, and splits

*Sarban was the caravan leader: one driver, one road, and the engine finally learned to say what it
knew. Karvan is the caravan itself — many loads moving together, carrying what the last journey
learned.*

**Authored 2026-08-04**, from `docs/dev/NEXT-ERA-FINDINGS-2026-08-04.md` (the research), the owner's
field notes after three real runs, `docs/dev/TOKEN-BUDGET-TUNING.md` (the measurements), and a survey
of what the 2026 agent-orchestrator ecosystem ships. Every "today" claim in this document was checked
against the tree at `feat/sarban` @ `0bbc972` on 2026-08-04; file and line references are verified, not
remembered.

**Two plans, in order:**

| plan | file | tracker | checkpoints | what it is for |
|---|---|---|---|---|
| **Karvan core** | `plans/karvan/core.plan.json` | `plans/karvan/CORE-TRACKER.md` | 25 in 7 stages | The engine knows what it did, what it cost, and where its own code lives |
| **Karvan lanes** | `plans/karvan/lanes.plan.json` | `plans/karvan/LANES-TRACKER.md` | 23 in 7 stages | Two client sites, or two pages of one site, at the same time |

Karvan core runs first and alone. Karvan lanes is authored now so the design is settled while the
evidence is fresh, and launches after core's stage-boundary re-measure (K7.1) has corrected the budget.

**The three sentences this era is built on:**

1. **98%+ of every token this project has ever spent is a cache read**, and cache reads are ~66% of the
   bill. Context size × turns is the lever. Output brevity is not.
2. **The engine cannot see the numbers it is judged by.** It records cumulative tokens but not context
   size; it records zero commits on every rollover; it cannot tell you what a project cost this month;
   and its state dies with the machine it ran on.
3. **The substrate for parallel work is already in this tree** — worktrees, a lane pool, a merge gate,
   a stage DAG, path claims. What is missing is a scheduler, a queue, and a lane that is a real
   session.

---

# Part I — K: the core plan (`plans/karvan/core.plan.json`)

## K1 — The ledger stops lying

**Why first.** Every measurement in K4 is computed from the ledger. A ledger that records zero commits
on 100% of rollovers, a permanent zero in the thinking column, and a lessons file that repeats itself
will produce confident wrong answers. This stage is cheap, mechanical, and it is the precondition for
believing anything later.

### K1.1 — A rolled-over session's commits and claims are recorded like any other session's

**Today.** `SessionRunner.cs:411-420` sets `rec.Outcome = SessionOutcome.RolledOver`, extracts the
result summary, writes the handoff and **returns before the verdict pass** that populates `NewCommits`
(whence `commit_count`) and `newly_done`. Consequence, measured: `commit_count` is 0 on 100% of
rollovers in both Sarban runs, while git ground truth over each rolled-over session's own
`started_utc..ended_utc` window says **91%** of conductor's rollovers and **56%** of the sk run's left
real agent commits. Every board, digest, report and Telegram push under-reports on every rollover, and
one client-site run reported two of fifteen sessions as having done nothing when one of them had
shipped a pull request.

**Done when.** A rolled-over session records the commits it actually made and the checkpoints it
actually claimed; a rollover with commits shows them on the board, in `REPORT.md` and in the push.
The proof is a harness test that drives a session to the ceiling and asserts a non-zero
`commit_count` — this repo's fake-agent fixtures exist for exactly this. Do not "fix" it by moving the
whole verdict before the rollover return if that changes what a rollover *means* (it still must not
consume an attempt, and it still must not run the phase gate); record the facts, keep the semantics.

### K1.2 — The soft break is delivered until it is obeyed, carries the remaining budget, and is measured

**Today.** The cooperative nudge fires at `softBreakRatio × maxSessionTokens` and is delivered **once**,
by a `PostToolUse` hook riding a single tool call. Measured across the face run's 33 post-cap sessions:
**11 rollovers, and all 11 ended at 8.00–8.13M — the hard ceiling. Not one stopped at the 6.0M
nudge.** The hard path is `SessionRunner.OverSessionTokenBudget` → `EndOnBudget` → `agent.Kill()`: a
mid-turn kill that loses the in-flight reasoning and writes a synthetic handoff. So the only graceful
exit a capped session has converted zero times out of eleven.

**Done when.** The nudge is re-stated rather than announced once (on an interval, a token threshold, or
both); it names the actual remaining budget rather than the fact of a limit; it states the wrap-up order
explicitly — **claim first, handoff second**, because the claim is the only thing that survives a kill;
and the session record carries whether it was delivered, re-delivered and obeyed, so the next tuning
pass reads a measurement instead of inferring one. Prove it against a scratch run of your own build with
a deliberately tiny ceiling, and show a session that took the nudge and exited clean.

### K1.3 — Three small untruths die as a class

1. **`costs.tokens_think` is 0 on all 125 rows ever written.** Claude bundles reasoning into output and
   `ClaudeProvider` correctly leaves it unset. The column, the DTO field `TokensThink`, the Report
   column and `SqliteRunStore.Queries` carry it anyway, and a permanent zero in a money report reads as
   "no thinking happened". Either drop it end to end or label it not-applicable-for-this-provider —
   decide, and make the surfaces agree.
2. **`.conductor/lessons.md` is a diary, not lessons.** It is a rotating log of session narratives,
   near-duplicates of the handovers, truncated mid-sentence, and it currently contains the same SF7-38
   entry **twice**. `LessonsBattery` pastes it into the next prompt, so it pays cache-read rent on prose
   that teaches nothing. Extract rules — one line each, deduped, capped — and fix the duplicate-append
   bug that produced the repeat.
3. **`face-go/go.mod` misdescribes its own graph.** `glamour` is imported directly by
   `internal/tui/markdown.go` but marked `// indirect`, and the graph carries two lipgloss majors
   (`charm.land/lipgloss/v2` direct, `github.com/charmbracelet/lipgloss` v1 via glamour). `go mod tidy`,
   pin the intent, and fix the one `gofmt -l` miss (`internal/widgets/ticker_test.go`).

**Done when.** All three are closed with the surfaces agreeing, and the go build/vet/test gate is green
with a tidy module.

### K1.4 — conductor's MCP config merges the operator's servers instead of replacing them

**Today.** `SessionRunner.Mcp.cs` `WireMcpServer` writes a config containing *only* `conductor-tasks`,
replacing rather than merging whatever MCP servers the operator has configured. Verified in the field:
a user-scope chrome-devtools server was invisible to spawned sessions. Compounding it, in at least one
shipped harness conductor's own tools arrive **deferred**, so the agent must search for `task_update`
before it can claim anything. SF6.1 shipped the prompt-side workaround (the template tells the agent to
search, and names the CLI fallback); the engine-side fix was filed and never owned.

**Done when.** A session sees conductor's task tools **and** the operator's own servers; the merge is
tested against a config that already has servers in it; and the prompt-side fallback stays, because a
deferred tool is the harness's choice and conductor cannot assert otherwise.

---

## K2 — The architecture becomes navigable

**Why here, before memory.** K3 moves the store, which touches every `IRunStore` caller. Deciding where
the store *lives* after that move costs the same work twice. And the owner's requirement is explicit:
this repo is used daily and shown deliberately, so structure is part of the product. The rule for this
stage is **assess, then reshape — do not rewrite.** 1,547 test methods are the safety net; keep them
green at every step, and keep each checkpoint independently revertable.

### K2.1 — `Conductor.Core` exists and the dependency direction is a compiler error, not a convention

**Today.** `Conductor.csproj` holds CLI + orchestration + HTTP control plane + Telegram + store +
providers; `Conductor.Planning` is the only extracted piece. Nothing prevents a command from reaching
into `SessionRunner`, or the store from formatting console output. 92 files declare a
`public static class` (`Git`, `SatelliteRepos`, `LiveMetrics`, …) — pragmatic, but static means
unfakeable, and it is why some tests reach for real git.

**Done when.** `Conductor.Core` holds the domain, orchestration and store with **no** reference to
Spectre.Console and no HTTP hosting; `Conductor` is CLI + hosting and references Core, never the
reverse. The seams that already exist and work (`IRunStore`, `IEventSink`, `IAgentProvider`,
`IPromptBattery`, `IPlanner`, `IQaPolicy`, `IWorkflowResolver`, wired in `ConductorHost`) move as they
are — this is a relocation with an enforced direction, not a redesign. Publish, `doctor` and the Face's
discovery path all still work; `tools/install.ps1` still produces a working engine.

### K2.2 — Architecture tests fail the build when a boundary is crossed

**Today.** `AGENTS.md` documents a *Command/Query/Event layering (F5+)* rule and `LaneCoordinator`'s own
doc-comment cites it as the seam it was extracted along. Nothing checks either. The industry answer is
architecture assertions in the ordinary test suite; ArchUnitNET is the maintained option (NetArchTest
has had no release since 2023).

**Done when.** The suite contains executable statements of the rules — core does not reference
Spectre.Console or ASP.NET; the store does not write to the console; commands do not reach into
orchestration internals; event types stay in the event namespace — each failing with a message that
names the offending type and the rule. They run in the existing `engine-full` gate, so a future session
that crosses a boundary meets a red gate instead of a reviewer. Land them **with** K2.1 so the
extraction is verified rather than asserted.

### K2.3 — The partial-file piles become responsibility folders, and one convention is written down

**Today.** `ControlPlaneDto` is **30 partial files**; `ControlPlaneServer` 11, `ConductorEvent` 11,
`VerdictEngine` 8, `TelegramService` 8, `SqliteRunStore` 7, `SessionRunner` 5, `RunLoop` 5. No file
exceeds 500 lines, which looks healthy and is not: the *type* still owns every responsibility, the
compiler enforces no boundary, and "where does X live" needs a grep. The DTO pile in particular is
really per-endpoint contracts wearing one type name.

> ⚠ **Corrected by K2.3 (session 7), measured before the edit.** ~~"`ControlPlaneDto` is 30 **partial
> files**"~~ — they were not partials. `ControlPlaneDto` (30), `ConductorEvent` (11) and the three
> `TelegramService` Dto files each declared *independent top-level records*; the shared prefix was a
> filing convention pretending to be a type, which is a different defect from a real partial pile and
> wants a different fix. `ControlPlaneServer`, `VerdictEngine`, `SqliteRunStore`, `SessionRunner` and
> `RunLoop` are the genuine partials. The checkpoint split both, and `ARCHITECTURE.md` says which is
> which so the distinction survives the next reader.

**Done when.** The worst piles are split by *responsibility* — a folder per feature for the endpoint
contracts, not thirty files of one type — and the repo has **one written file-organisation convention**
(where a new endpoint's contract goes, where a new event goes, when a partial is legitimate and when it
is hiding a second responsibility) that `ARCHITECTURE.md` states and reviewers can cite. Targeted:
name the piles you split and the ones you deliberately left, with the reason.

### K2.4 — The front door: `ARCHITECTURE.md`, an `AGENTS.md` that is current-state only, a root a newcomer can read

**Today, measured at the repo root.** `AGENTS.md` is **80KB** of append-only handoffs — twenty-three
`## Resume here` / `## Previous resume point` sections stacked back to 2026-07-09, the live era buried
among nine superseded ones, with the C# standards, the read order and the architecture notes interleaved
between them. Beside it, `CONDUCTOR-WORKGRAPH.md` (44KB) is a **divergent duplicate** of
`docs/dev/CONDUCTOR-WORKGRAPH.md` — the two files have different hashes and have drifted apart. Two
closed-era trackers (`SARBAN-CORE-TRACKER.md`, `SARBAN-FACE-TRACKER.md`) are still loose at the root.
There is **no `ARCHITECTURE.md`**: the only architectural map in the project is a section inside the
80KB handoff log. The newest plan bundle on another branch (`ci-health/` on `chore/ci-health`: plan,
tracker, gates, templates and README in one folder) shows the better convention already exists in the
owner's practice and has never been applied here.

**Done when.**
- `ARCHITECTURE.md` exists and is the map: the assemblies and the allowed dependency direction (with
  K2.2's tests named as the enforcement), one session's lifecycle end to end (dispatch → prompt
  composition → agent → verdict → gate → tracker), the seams and what implements them, the two surfaces
  (control plane, Face) and how they connect, and a "where do I add X" table.
- `AGENTS.md` is current-state only — this era, the standards, the read order, the traps — with
  superseded handoffs moved under `docs/history/handoffs/` and an index line each. It must stay useful
  to the *next* session on day one; this is a reorganisation, not a deletion.
- The root holds only what a newcomer needs. Closed-era trackers move under `docs/history/trackers/`
  (which already exists); the duplicate `CONDUCTOR-WORKGRAPH.md` resolves to one file with the other a
  pointer or gone; era artefacts live one folder per era, as this plan's own `plans/karvan/` does.
- `docs/README.md` and `docs/dev/README.md` index what is now where, so nothing becomes unfindable.

**Trap.** `readOrder` and the tracker path are read by the running engine. If you move a file this run
depends on, update the plan and reload it in the same checkpoint, or the next session reads nothing.
Do not move this era's own live tracker or spec.

---

## K3 — Conductor remembers

### K3.1 — State has a machine-level home with a catalogue, and existing runs are imported

**Today.** `PlanConfig.cs:109` — `StateDir => Path.Combine(Repo, ".conductor")`. Hard-coded, one line,
no override. `.conductor/.gitignore` is `*` with four exceptions (`REPORT.md`, `followups.md`,
`handovers/`), so `run.db` — every session, cost, gate, bug and event — is untracked and machine-local.
Clone the repo elsewhere and the project has no past. The schema is not the problem: `run.db` already
holds two runs. The **location** is.

**Done when.** A machine-level state home (`%LOCALAPPDATA%/conductor` on Windows,
`~/.local/share/conductor` elsewhere) holds one catalogue keyed by repo path + plan; `CONDUCTOR_STATE_HOME`
overrides it; `.conductor/` is demoted to per-run scratch (logs, transcripts, evidence) plus a pointer,
and keeps the tracked deliverables that belong in the repo (`REPORT.md`, `handovers/`, `followups.md`).
A migration **imports** existing `.conductor/run.db` files rather than orphaning them, is idempotent, and
says what it moved. The Face's discovery path, `conductor ps`, `watch` and the control plane all still
find a live run.

**Decided, do not re-litigate.** Not a synced folder — SQLite on OneDrive or Dropbox corrupts under
concurrent writers, and this machine already runs two engines at once. The catalogue is also the thing
the lanes plan needs (a lane worktree has no `.conductor/` at all), so keep the resolution path explicit
enough that a second working tree can point at the same run.

### K3.2 — `conductor history` browses past runs, and the Face can open one

**Today.** The Face's History tab reads the *live* run only; `conductor report` regenerates the current
`REPORT.md`. There is no "show me that run from July", no cross-run comparison, no per-project view.

**Done when.** `conductor history [--repo|--plan|--since]` lists past runs from the catalogue with their
outcome, checkpoints, sessions and cost; a run can be opened read-only and its spine replayed; and the
Face lists past runs in its run picker (`runpicker.go` already exists — extend it, do not write a
second one). Read-only means read-only: no verb in this checkpoint may mutate a finished run.

### K3.3 — Every run records which engine, which plan and which config produced it

**Today.** "Which build produced this run" is unanswerable from the run record. A client-site run
executed on `0.2.3-alpha…dirty`, an uncommitted working-tree build, and the only way to know was to ask
the machine afterwards. As of this authoring the engine on PATH is `0.3.1-alpha.0.6+98a426af63d6.dirty`
— commit `98a426a`, which is on `chore/ci-health` and **not an ancestor of master**.

**Done when.** Each run row carries the engine version, its commit, its dirty flag, and a snapshot of
the limits that governed it (cap, nudge ratio, cost cap, concurrency); a `.dirty` build warns at launch;
and `conductor history` shows the stamp. This is what makes K4's comparisons honest — "the cap was
raised at session 9" should be readable from the record, not inferred from a token curve.

---

## K4 — Token truth

**The measurement that ranks this stage.** 98%+ of all tokens are cache reads, ~66% of the bill;
output is ~15%. Anything that shrinks what a session *carries* attacks two-thirds of the spend.
Anything that shrinks what the agent *says* attacks fifteen percent. So the instrument comes first, and
every lever waits for it.

### K4.1 — The engine measures context size per turn, not just cumulative tokens

**Today.** `LiveMetrics.SessionTokenTotals` folds `TokenDelta` into input/output/reasoning/cacheRead
**integrals**. Nothing tracks *context size at a turn* — the number Claude Code's `/context` shows and
the number that actually drives the 66%. `context_high_water` appears nowhere in the tree.
`TOKEN-BUDGET-TUNING.md` §1 states the rate-versus-area distinction correctly; the engine implements
only the area.

**Done when.** Per-turn context is derived from the stream (the cache-read delta per turn approximates
the re-sent prefix), a `context_high_water` and a mean-turn-context are recorded per session, and both
are surfaced. The acceptance is a sentence an operator can act on: "your sessions run at 210k context",
which is the actual diagnosis behind a badly-set cap. Sanity-check the derivation against a session
whose context you can independently estimate, and say in the evidence how close it lands.

### K4.2 — `conductor budget` reads the catalogue and prescribes the cap and the nudge

**Today.** The method is written down and implemented nowhere: measure the repo's **session floor**
(the smallest session that ever closed a checkpoint), measure the **wrap-up spend** (final tokens minus
the nudge threshold, for sessions that ended clean), set `maxSessionTokens = floor + 1.5–2× wrap-up`,
and choose `softBreakRatio` so the nudge clears the floor and headroom stays ≥1.5× wrap-up. One client
run was *worse than uncapped* for three stages because the cap sat below the floor. This repo shows the
subtler failure: the cap was defensible and the **nudge** sat below the median finishing session, so
every session was interrupted before it could have finished naturally.

**Done when.** `conductor budget` (or `doctor --budget`) prints floor, wrap-up, current cap, the
nudge-versus-floor comparison and the rollover rate, then prescribes: "your nudge is 0.8× your median
closing session — raise the cap to 12M". `doctor` warns when a plan's cap is below the measured floor
**or** its nudge is below the median closing session. Verify it against this repo's own two runs: it must
reproduce the numbers in `TOKEN-BUDGET-TUNING.md` and in the research doc's *The 8M cap's real score*
without being told them.

### K4.3 — `conductor money` answers what a project cost, per checkpoint and per month

**Today.** `REPORT.md` has a per-session cost column, the Face has a money widget, `run.db` has `costs`.
Missing: $/checkpoint, $/M blended rate, cache-hit share, the agent-versus-gate-versus-advisor split
over time, per-stage cost, project lifetime spend, month-to-date. Every figure in the research doc's
headline table was produced by a hand-written SQL query, and that is precisely the report the owner
keeps asking for.

**Done when.** `conductor money [--run|--project|--since]` ships the headline table's exact columns —
sessions, tokens, cache-read share, cost, checkpoints, tokens/checkpoint, $/checkpoint — plus the
before/after windows that answer "what did the cap buy me", and the Report tab carries the same section.
Cross-check one row against a hand-written query and put both in the evidence.

### K4.4 — Live token headroom sits beside live money

**Today.** Money caps are wired end to end — `CostCap`, budget window, approvals, headroom on Home
(SC2.3 / SF2.3). The token side is not: `MaxSessionTokensThisRun` reaches the wire, but there is no live
"session at 6.2M of 12M · nudge in 2.2M" gauge, no burn rate, no projection to the run cap.

**Done when.** The Face shows live session tokens against the cap with the distance to the nudge, a burn
rate, and a projection; the same numbers are on the wire so a remote surface can render them; and the
gauge is honest when the cap is unset (say "no cap", never imply one). This is also the first surface a
lane-aware Face will have to multiply, so keep the widget composable.

---

## K5 — The result contract and the channels

**Order matters here and it is deliberate.** The contract lands first, because everything downstream is
currently improvising its own mutilation of the same paragraph. Then the cheap Telegram relief. Then
evidence becomes a real thing. Then the composition layer, which is the only one that can carry a
screenshot.

### K5.1 — The session result has one format that conductor owns, and every consumer reads it the same way

**Today.** `SessionRunner.cs:482` `ExtractSessionResult` takes whatever prose follows
`SESSION-RESULT:`; 600+ words of unbroken narrative has been observed. Then every consumer improvises:
Telegram cuts at 700 chars (`RunLoop.Plumbing.cs:244`), the advisor at 1200
(`VerdictEngine.Advisor.cs:24`), `LessonsBattery` pastes a truncated copy into the next prompt, and the
digest and `REPORT.md` take their own slices. The same paragraph is stored, re-sent and re-cut four
ways.

**Done when.** Conductor owns the format: a headline of at most fifteen words, at most three outcome
bullets, changed artefacts as links, evidence paths, and explicit gaps. Prose goes to the handover,
where prose belongs. ~~**`FollowupParser` and the verdict parse read this text, so their parsers move in
the same checkpoint**~~ ⚠ **the verdict parse reads this text and moved; `FollowupParser` does not and
did not** — and so does the session template that teaches the format, which lives in
`plans/karvan/templates/session.md` and is read fresh every session, so this run can adopt its own
change mid-flight. A malformed or legacy result must degrade to today's behaviour rather than throwing:
the engine cannot make an agent obey a format, only prefer one.

> ⚠ **Corrected by K5.1 (session 18), measured before the edit.** `FollowupParser.cs:25-30` reads
> pipe-table rows out of `.conductor/followups.md`; it never sees a session result. Its only callers
> are `LaneCoordinator.cs:208/235` and `VerdictEngine.Advisor.cs:124`. So the checkpoint moved the
> verdict parse and the session template, and left the followup parser alone — correctly.

### K5.2 — The five Telegram defects that make the feed unreadable are gone

**Today**, from the owner's own transcribed client-site run (15 sessions, $97.46):

1. The session number is printed **twice**, from two different sources —
   `TelegramService.cs:391` `IdentityLine` stamps plan + live `_state.SessionCounter` onto every
   outgoing message, and the body from `Messages.cs:19` opens with the record's own number, so a late
   push can disagree with itself.
2. The stage is a bare letter (`— G`), because `stage` is passed as an id and the title is never
   looked up.
3. `result:` is 700 characters of raw prose cut blind mid-word (`RunLoop.Plumbing.cs:244`) — fixed
   upstream by K5.1, and this checkpoint stops re-cutting what is already structured.
4. Rollovers push `gates: (not recorded)` and no result line at all — K1.1's fix rendered on a phone.
5. No progress, ever: no checkpoint count, no stage progress, no ETA, in fifteen messages.

**Done when.** One identity block instead of two and one source for the session number; the stage title
beside the id; the structured result rendered rather than truncated; a rollover that reports what it
landed; and a progress line in every push. A handful of lines each — this is the cheap relief, and it
should be visible in the owner's chat the day it lands.

### K5.3 — Evidence is a first-class artifact with an event, a registry and a surface

**Today.** `conductor task --done <id> --evidence <string>` stores a free-text field, and
`AuditCommand` scans `docs/evidence/<stage>` and `.conductor/evidence/<stage>` for `*.txt` at replay
time. That is the whole of it. There is no evidence event, no watcher, no artifact registry, and no
notification when an agent produces a screenshot. The owner's real case — conductor builds a website,
the agent screenshots it, and a *second agent* has to be hired to notice and forward the images — is
unsupported.

**Done when.** An evidence artifact has a model (path, kind, checkpoint, session, sha, created-at)
written as an **event** when an agent registers one or when a watched directory gains a file; the Face
has an evidence surface; and the notification path can carry one (which K5.4 then delivers). Non-text
kinds are first-class — a PNG is the case that motivated the item. Keep the free-text `--evidence`
working; an artifact registry that breaks every existing claim is not an improvement.

### K5.4 — The message-composition layer: identity once, links, progress, money with headroom, and a screenshot that arrives

**Today.** `TelegramService.cs:420` is a single `sendMessage` with `parse_mode=HTML`. No `sendPhoto`,
no `sendDocument`, no threads, no albums, no `disable_notification`, no editing, and no chunking against
Telegram's 4096-character limit. Nothing is a link, though `Reporter.cs:443` already knows how to build
remote URLs from a commit sha. Money renders as four decimal places with no cap, headroom or burn rate,
all of which the engine already computes for the Face. The completion push names the plan twice and
gives the engine build string more room than the result.

**Done when.** Per-event templates the owner can edit (`TemplatesDir` already exists for prompts);
repo, branch, stage title and checkpoint in every push; commits and PRs as links; money with headroom
from K4.4; `sendPhoto`/`sendDocument` so K5.3's evidence actually arrives; a thread per run; severity
mapped to notify-versus-silent; and 4096-character chunking. The completion push leads with the
outcome, the cost, the checkpoint count, the duration and a link to the report.

**This is also the answer to remote observability (E3), and the decision is push-only.** The control
plane is a loopback HTTP server with no auth story; exposing it is a security surface this project has
deliberately avoided. A richer push plus a shareable report covers "can I see it from my phone" without
opening an inbound port. Record that decision in an ADR rather than leaving it implicit.

---

## K6 — The surfaces read

### K6.1 — An ADR fixes the TUI conventions, after reading glow and its peers

**Today.** The Face's conventions are implicit and inconsistent: one bespoke scroll integer per surface,
a hand-rolled editor, hand-maintained help, and markdown rendered in exactly one pane.

**Done when.** `docs/dev/adr/` gains a short ADR of adopted conventions — pager-shaped keys, the focus
model, help via `bubbles/key` + `help`, one scroll idiom, when to use `viewport` versus `list` versus
`table` — informed by an actual read of **glow** (the canonical answer to long markdown in a terminal:
`viewport` + `glamour` with pager keys), **soft-serve** (multi-pane app structure), **gh-dash** (dense
tabular dashboards, closest to our Kanban and Report) and **lazygit** (not bubbletea, but the reference
for a many-panel git TUI). Short and decisive; K6.2 and K6.3 implement it.

### K6.2 — Long text scrolls in a viewport, and the long-text glitch is gone

**Today.** `face-go/go.mod` requires exactly three things: `charm.land/bubbletea/v2`,
`charm.land/lipgloss/v2`, `x/term`. **`bubbles` is not a dependency at all.** So the Face hand-rolls
scrolling — `ScrollOffset` in `TranscriptModel`, plus `consoleScroll`, `reportScroll`,
`knowledgeScroll`, `ownerQueueScroll`, `processSelected` — as well as a text area
(`widgets/editor.go`), tables, lists and pagination. Bubbles ships **viewport, textarea, table, list,
paginator, help, key, spinner, progress, filepicker, timer**; every one of those is re-implemented here
with its own bugs. "You cannot read long text" is precisely the failure a hand-rolled scroll region has
and `viewport` does not.

**Done when.** `bubbles` (v2, matching bubbletea v2) is a declared dependency and Report and Knowledge
scroll through a `viewport` — the smallest diff and the biggest relief. The golden tests
(`golden_test.go`, `frame_invariant_test.go`, `glitch_sweep_test.go`) are the regression net: keep them
green, and regenerate any baseline in a **separate rebaseline commit** so the diff that changes
behaviour is readable. Evidence is a captured frame of a long document scrolled to its end — the exact
case that failed.

### K6.3 — Each tab owns its own state, and the root `Update` delegates

**Today.** `model.go:125-322` is one flat struct of **116 fields** — `paletteQuery`, `planEnumCustom`,
`timelineHistorySet`, `reportScoresErr`, `knowledgeMode` all siblings — and `update.go` is **826 lines
with 80 `case` arms**. The bubbletea idiom is composed models: each tab is its own `tea.Model` with its
own state, `Update` and `View`, and the root delegates to the focused one.

**Done when.** Per-tab models are extracted — the `tab_*.go` files are *already* the right partition, the
state simply never followed the code — and the root `Update` becomes a dispatch. Do this **with** K6.2,
not after: you cannot drop a `viewport` into a tab whose scroll state is a shared integer on a struct
four hundred lines away. `plan.go` at 1,000 lines is the next candidate and may follow in the same
checkpoint if the goldens stay green. Keep `tabKey` in `model.go` the single source for mnemonics and
update the hand-maintained legend in `cmdbar.go` in the same commit, or the help lies.

### K6.4 — Markdown renders in the theme, everywhere it should

**Today.** `renderMarkdown` sets `glamour.WithStandardStyle("dark")` unconditionally while
`widgets/theme.go` already has a theme system, and ~~Report, Knowledge and handover panes render markdown
as plain text~~ ⚠ **the Knowledge pane renders markdown unthemed; Report is a dashboard, not prose, and
there is no handover pane at all**.

> ⚠ **Corrected by K6.4 (session 24), measured before the edit.** `tab_report.go` renders a
> stage/session/gate dashboard whose only free text is a one-line attention reason; no pane named
> "handover" exists in `face-go`. The checkpoint delivered what the *first* clause asked for — one
> theme-aware renderer — and named the rest rather than inventing panes to satisfy the sentence.

**Done when.** One markdown renderer honours the active theme, and the panes that should render markdown
do. Finish the remaining primitive swaps the ADR calls for (textarea for the Knowledge and Templates
inputs; `list`/`table` for the list-shaped tabs) as far as the goldens allow, and name in the handoff
anything deliberately left for later.

---

## K7 — Ship the plan

### K7.1 — The docs match the engine, and this era's own token measurements are written back

**Done when.** `docs/dev/TOKEN-BUDGET-TUNING.md` carries the re-measured conductor numbers — the 8M
cap's real score (~~26.5M → 14.0M tokens per checkpoint, 33% rollover, twenty of thirty-three sessions
closing nothing~~ ⚠ **26.5M → 17.0M, 30% rollover, nineteen of thirty-three closing nothing**), the
corrected rule that the **nudge** and not only the cap must clear the ~~floor~~ ⚠ **median closing
session**, and this run's own figures produced by `conductor budget` rather than by hand. `docs/dev/NEXT-FEATURES.md`
is refreshed. Every claim this plan made that turned out wrong is corrected in place, not quietly
dropped. The era CHANGELOG section is written. And the closure ledger names every open bug and followup
row with the stage that closed it or the living owner that holds it.

**Dogfooding note, and it is the point:** K4.2 shipped `conductor budget`; this checkpoint is the first
real use of it. If its prescription disagrees with the numbers in this spec, the prescription wins and
the spec gets corrected.

> ⚠ **It disagreed, and it won (K7.1, 2026-08-05).** Four of this section's own figures were wrong and
> are struck through above. The corrected rule is stronger than the one this spec asked for: sarban-face's
> nudge *did* clear the floor, at 1.30×, and still converted zero of ten kills — because it sat at 0.84×
> the median closing session. `TOKEN-BUDGET-TUNING.md` §7 step 4 now says *median closer*, not *floor*.
> The wrong claims found by this plan's other stages are corrected in place too — see the ⚠ blocks at
> K2.3, K5.1 and K6.4. The closure ledger that names an owner for every bug and followup left open is
> the `## K7.1 closure ledger` section of `.conductor/followups.md`, where SF7.1 put its own.

### K7.2 — The branch is merged by the owner, tagged, released and installed

**Done when.** `feat/karvan` is merged to master by the owner (this stage is owner-gated), the release is
tagged through the existing pipeline, the workflow publishes its platform assets, and the installed
`conductor version --short` matches the releases page. The install is the **first** `install.ps1` of
this run and happens only after the owner confirms no other conductor run is live on this machine.

---

# Part II — L: the lanes plan (`plans/karvan/lanes.plan.json`)

**What the owner asked for, in his words:** *"Sometimes two of my tasks are two different clients'
websites, or two different pages of the studio site. So it would be either two repositories or two
worktrees."* That settles the design: **the unit of parallelism is a repository or a worktree**, not two
agents in one tree. Repos first, same-repo stage parallelism second and behind a flag.

**What the ecosystem already settled** (surveyed 2026-08-04; see the research doc for sources):
the git worktree is the consensus isolation primitive, and conductor already has it. Ports, databases
and dev servers are what worktrees fail to isolate, and exactly one of nine surveyed tools solves it.
Merging back is where every tool gives up; the mature answer is a Bors-style batch-then-bisect queue.
Multi-repo is the ecosystem's open gap — of roughly 140 indexed tools, five claim it. **That last fact
is why this plan is worth running: it is the part conductor can be genuinely better at, and it is the
part the owner needs.**

**Prerequisite.** Karvan core must be complete, in particular K3.1 — a lane worktree contains no
`.conductor/` at all, so a lane cannot hold state until the catalogue lives outside the tree.

## L1 — Git safety is code

- **L1.1** `requireCleanTree` exists and is enforced: a post-session clean-tree assertion, a
  branch-pattern violation that can **refuse** rather than only warn (`RunLoop.Control.cs`'s
  `WarnOnBranchPattern` is today the only enforcement — a session on the wrong branch is told so and
  proceeds), and a refusal on a detached HEAD or an unexpected remote. Configurable, with the strict
  setting the default for new plans and the lenient one available for existing ones.
- **L1.2** Push and force-push guards, and a per-checkpoint commit-convention check. A run may not
  force-push; a checkpoint whose commit does not carry the repo's trailer convention is reported, not
  silently accepted.
- **L1.3** Worktrees are counted, capped and reaped — and **an unmerged scratch branch is never
  force-deleted.** Today `MutatingLaneRunner.cs:164` integrates with `git merge --ff-only`, and the
  `finally` at line 209 calls `Git.DeleteBranch`, which is `git branch -D` (`Git.cs:96`, force). With
  one lane the fast-forward always succeeds; with two, the second lane's base has moved, the merge
  fails, and a full session of gate-green work is force-deleted with only the reflog holding it. Fix:
  `-d` and keep the branch on failure with its name in the event and the log; delete the directory then
  `git worktree prune` (measurably faster than `worktree remove` on a large tree, and
  `Git.WorktreeRemove` currently swallows the `IOException` that a locked build output produces on
  Windows, leaving an orphan and no record); count live worktrees against the concurrency limit; a
  `conductor worktrees` verb lists and reaps; and a startup sweep collects orphans left by a killed run.

## L2 — Repos are first class

- **L2.1** `repos: [{id, path, role, branchPattern, gates}]` replaces the bare
  `satelliteRepos` string list (`PlanConfig.cs:24`), with `satelliteRepos` kept as a back-compat alias
  that keeps every existing plan working. `doctor` reports each repo, its role, and whether it resolved
  — the one line that today is an operator's only confirmation the paths were right.
- **L2.2** Per-repo branch assertion, clean-tree assertion and gates. Today `satelliteRepos` does
  exactly two things — `SatelliteRepos.Heads`/`CommitsSince` for the verdict (`SessionRunner.cs:216`,
  `VerdictEngine.cs:69`) and a `doctor` check (`DoctorCommand.cs:284`) — and it does not branch,
  checkout, gate, commit, push or assert cleanliness anywhere. Everything else is prose in
  `promptExtra`; the owner's own nine-repo CI plan carried eight numbered traps doing by hand what this
  checkpoint moves into the engine.
- **L2.3** Coordinated commit and push, and per-repo verdict attribution: which repo did the work land
  in, shown per session and per checkpoint. The anchor-commit rule (at least one commit in the anchor
  repo per session, as a dated proof note) becomes an engine-side expectation instead of a paragraph in
  a template.
- **L2.4** The Face shows every repo in the run — per-repo chip with branch, dirty state, ahead/behind
  and the commits this session landed there.

## L3 — A lane is a real session

- **L3.1** A lane resolves state through a redirect to the run's canonical store, and one writer owns
  it. `StateDir` is `Repo/.conductor` and `.conductor/.gitignore` is `*`, so a fresh worktree has no
  state directory: without this, a lane session either finds nothing or creates a second `run.db` and
  splits the ledger. `SqliteRunStore` opens `Data Source={path}` with `journal_mode=WAL` and **no
  `busy_timeout`** (`SqliteRunStore.cs:24-29`), which is exactly the configuration that starts
  returning `SQLITE_BUSY` under a second writer — set the timeout, keep one writer, and prove it under
  contention.
- **L3.2** A lane runs a **real** session. Today `MutatingLaneRunner.RunAsync` spawns the agent with
  `ProcessRunner.RunAsync(...)` directly (line 78): no `SessionRunner`, and therefore no cost rows, no
  token budget or soft break, no rollover handling, no resume rail, no stall watchdog, no verdict pass,
  no tracker claim, no MCP task tools and no transcript — `LaneCoordinator` infers success from
  `result.Merged`. Everything the engine knows how to do to a session has to reach into a worktree.
  **This is the real distance to parallel stages, and it is why the scheduler is the stage after this
  one.**
- **L3.3** Per-lane environment: a lane index, a base port plus offset, its own state dir and scratch
  database name, injected into the lane's environment; and per-lane `setup`/`teardown` commands that
  run inside the worktree, which is where a dependency install, an env-file copy and a port claim
  belong. Conductor has `setup`/`teardown` at plan level only, and no per-lane env at all. This is the
  gap that matters most for the owner's actual work: two lanes each running a dev server collide on
  port 3000 long before they collide in git. Log the assignment and put it in the lane's event so a
  failure is attributable.
- **L3.4** Conflicts are **detected**, not merely declared. `PathClaimTracker.TryClaim` arbitrates only
  the `pathClaims` a plan declared (`LaneCoordinator.cs:52-58`), and most plans declare none, so two
  lanes can be dispatched onto the same files and the collision surfaces as a merge-gate failure after
  both have been paid for. Add real detection with `git merge-tree $(git merge-base A B) A B` — one git
  call, no working tree — before dispatch and again before queueing a merge, with declared claims kept
  as the cheap fast path.

## L4 — The scheduler and the queue

- **L4.1** A DAG scheduler behind `limits.maxConcurrentStages`, default **1**, so no existing plan
  changes behaviour. `RunLoop.cs:104` is one sequential `while`, and `RunLoop.Plumbing.cs:34` picks
  `FirstOrDefault(IsReady)`; `StageConfig.DependsOn` is a real DAG, cycle-validated in
  `PlanConfig.cs:419-427`, whose own doc comment admits "execution stays sequential". The scheduler
  walks it and runs independent ready stages concurrently, honouring dependencies — worktree isolation
  cannot resolve a task dependency, and pretending otherwise is the ecosystem's most common failure.
- **L4.2** The merge queue: serialize, rebase onto the current base, re-run the merge gate on the
  rebased tree, then integrate. Batch-then-bisect (rebase the pending stack, test the tip,
  fast-forward all on green, binary-bisect on red) is the mature form and the stretch goal;
  **serialize-and-rebase is correct on day one** and it is what makes L1.3's branch-loss fix
  unnecessary rather than merely safe.
- **L4.3** Budget and tokens are accounted per lane and capped globally: the cost cap, the token cap
  and the approval flow all still mean what they say when three sessions are spending at once, and
  every cost row names its lane.
- **L4.4** The rehearsal, and it is a deliverable, not a formality: a scratch two-repo plan run end to
  end with two lanes, proving that both land, that the verdict attributes each correctly, that the
  ledger is one ledger, and that neither lane's ports, database or build output touched the other's.
  Never rehearse against a real client repo.

## L5 — The Face renders a fleet

- **L5.1** N live sessions render at once, with the focused lane selectable — today every surface
  assumes one.
- **L5.2** Per-lane cost, tokens, headroom and gate state, with the run-wide totals still correct.
- **L5.3** The kanban and the history surface are lane-aware: which lane owns a card, which lane a
  commit came from.

## L6 — Autonomy

- **L6.1** An in-process supervisor. `conductor watch` is already the right shape — it blocks on a wake
  set and its own doc comment prices the polling alternative correctly (a babysitter spends ~95% of its
  budget on "still running" ticks that every later tick pays for again). What is still outside is the
  hook: it shells out to a model the engine does not own, does not budget, does not record in `costs`
  and cannot show in the Face. Bring it in-process using the configured provider, with its own cost
  line (the `advisor` category already exists), its briefs in the store, and a Face surface — and keep
  `--hook` for people who want their own. **Worth most once lanes exist**, because N concurrent sessions
  is exactly when a human stops being able to watch.
- **L6.2** The advisor becomes event-sourced. `VerdictEngine.Advisor.cs` prices itself at a flat
  `0.0005 × seconds` — an estimate, not a measurement — and re-reads a freshly composed context when the
  folded event projection (`events`, 4,199 rows on this repo; `RunStateProjection`, `TaskGraph`,
  `HealthMetrics` already fold it) is a better and far cheaper input. Record its real token and cost
  rows.
- **L6.3** One-way GitHub sync, off by default: `conductor task --done` comments on or closes a mapped
  Issue, and the run report can post as a PR comment. `Core/Update/ReleaseClient.cs` is currently the
  only GitHub caller in the tree. **Nothing inbound** — the tracker is the verified contract, and
  making it an eventually-consistent mirror of someone else's board is the failure this project's own
  anti-pattern A16 names. Needs a token, therefore `SecretsStore`. Optional: an honest skip with a
  reason is an acceptable completion.

## L7 — Ship the lanes

- **L7.1** The owner's own two-repo job is the acceptance: a real plan across two of his repositories
  or two worktrees, run to completion, with the evidence being the run record — per-repo commits,
  per-lane cost, one ledger, no cross-talk. Docs and an ADR carry the concurrency model, the merge
  policy and the limits; `ARCHITECTURE.md` gains the scheduler and the queue.
- **L7.2** Owner-gated merge, tag, release, reinstall.

---

# Appendix A — owner ask to stage traceability

| owner's note | stage |
|---|---|
| evidence seeing and knowing | K5.3 |
| code and architecture hygiene (go + backend) | K2.1–K2.4, K6.3 |
| code should be browsable and readable | K2.3, K2.4 |
| caveman / token saving | **scrubbed** — K5.1 carries the safe part |
| review previous sessions for token efficiency | K1.2, K4.2, K7.1 |
| telegram messages | K5.2, K5.4 |
| multi repo worktree merge and support | L2, L4.2 |
| conductor auto mode for future | parked, named in the research doc |
| internal runner and planner | L6.1 |
| **parallel work (crucial)** | L3, L4 |
| GitHub syncing | L6.3 |
| UI reflects budget updates | K4.4 |
| solution for long text | K6.2 |
| embed ai / retrieval | parked behind K4.1 |
| review token consumption | K4.1, K4.3 |
| conductor money report | K4.3 |
| stateful, history with consumption and money | K3.1, K3.2, K4.3 |
| remove .conductor / general vs specific | K3.1 |
| git worry | L1.1, L1.2 |
| better token understanding | K4.1 |
| event sourced advisor | L6.2 |
| less need for the human | L6.1 |
| history aware | K3.1, K3.2 |
| monitor sits inside / remote friendly | L6.1, K5.4 |
| glow and bubbletea best practice | K6.1 |
| refactoring go and .net | K2, K6.3 |

# Appendix B — what this era deliberately does not do

- **Auto mode / self-planning (D3).** A mode where conductor plans its own next stage from a goal
  collides with the project's own principle that the checkpoint table is the verified contract. Named,
  scoped, not started.
- **Retrieval or embeddings over history (A4).** The prompt prefix is 2–5% of a session. It waits for
  K4.1's instrument to say it is worth attacking.
- **Output-brevity prompting.** Scrubbed on the owner's instruction. Output is ~15% of the bill, much of
  it code and tool arguments that such a rule would not touch, and adopting a third-party harness plugin
  mutates something conductor cannot assert. K5.1 takes the same win in a format we own.
- **An authenticated tunnel to the control plane (E3).** Push-only, decided in K5.4.
- **Two-way GitHub sync.** L6.3 is one-way by design.

# Appendix C — launch drill

Follow this in order. Steps 1–3 are the owner's; the rest is pre-flight and costs nothing.

1. **Reinstall the engine from a clean tree, and check what you got.** The engine on PATH as of
   2026-08-04 is `0.3.1-alpha.0.6+98a426af63d6.dirty` — built from commit `98a426a`, which lives on
   `chore/ci-health` and is **not an ancestor of master**. Confirm no conductor run is live anywhere on
   this machine (`conductor ps`, and check `Get-CimInstance Win32_Process` for `conductor.exe` and
   `conductor-face.exe`), then run `tools/install.ps1` from a clean checkout and confirm
   `conductor version --short` reports what you expect with no `.dirty` suffix. A face holding the DLLs
   makes the publish fail with a file-lock error that names the pid.

   > **What actually happened, 2026-08-04: this step was DEFERRED, on purpose.** A second conductor run
   > was live in `C:/Code/sk-studio` (engine pid 25612, session #1 started 22:35:58), driving from the
   > same published `conductor.exe`. Publishing over a running engine fails on the file lock at best and
   > breaks someone else's run at worst, so the era launched on the dirty
   > `0.3.1-alpha.0.6+98a426af63d6` build — the same one that drove the Sarban face era. It was checked
   > for the one capability this plan depends on: `hook-budget` is present, so the cooperative
   > soft-break rail reaches the agent rather than firing into a void. **FU-OWNER-14 therefore remains
   > open**, and K7.2 pays it — that checkpoint's reinstall is the first install of this run, which is
   > what its own text already requires.
2. **Create the branch.** `git switch -c feat/karvan` from master, and commit this spec, the plans, the
   trackers and the templates on it. The plan's `branchPattern` is `^feat/karvan$`.
3. **Telegram.** The bot token lives in the user environment (`CONDUCTOR_TELEGRAM_TOKEN`). A shell
   started before it was set does not see it — pass it inline in the same command when launching, or the
   run's pushes silently do nothing. Send one test push before launching.
4. **Pre-flight, three commands, no spend.**
   `conductor doctor -p plans/karvan/core.plan.json` — must be 0 fail, and the line to read is `work`:
   the only acceptable form is `25 work item(s) cover all 7 stage(s)`. Anything else means the tracker
   table is not being parsed.
   `conductor journey -p plans/karvan/core.plan.json` — the Model column must show the model you chose,
   not `(default)`.
   `conductor run -p plans/karvan/core.plan.json --dry-run` — read the composed prompt in full. It must
   not contain the phrase about QA-ing the previous session; if it does, `templatesDir` did not resolve
   and the built-in template is being used.
5. **Launch supervised, then detached.** `--once` first. Then detached with **stderr redirected** — a
   prompt the engine refuses to build is written to stderr only, and without the redirect the engine
   simply vanishes with `stage → K1` as the last log line.
6. **Arm one log-tail monitor, and re-derive its filter from this engine's source** rather than copying
   an old one. The vocabulary the log actually prints is not the vocabulary of the status enums.

# Appendix D — traps this repo has already paid for

These are in the plan's `promptExtra` as well; they are here so a human reading the spec sees them too.

1. **This repo's `.conductor/` is the live state of the run driving you.** The claim and note verbs and
   the read-only verbs are yours; never aim a run-control verb at this repo. Any live-run proof spawns
   your build against a **scratch** repo with its own plan and its own state dir.
2. **Never run `tools/install.ps1` mid-run.** The engine driving you runs from the published copy. The
   owner reinstalls at K7.2, not before.
3. **The `conductor` on PATH is not your working tree.** Exercise your changes through
   `dotnet run --project src/Conductor -- <verb>` (or `go run ./cmd/...` in `face-go`). Testing a new
   verb through the PATH shim only proves the published engine lacks it. The exception by design is a
   task claim, which must target the driving run.
4. **A second conductor run may share this machine.** Never kill a `conductor.exe`, `conductor-face.exe`
   or stray `dotnet` process by pid without checking its command line first; give every scratch rig its
   own port and its own state dir, and read the port back from the rig's own discovery file.
5. **A literal brace token in a file under `templatesDir` kills the engine** — it is validated for
   unresolved placeholders, the refusal goes to stderr only, and it fires when a stage first renders
   that template. Sweep the templates after any edit; use a doubled brace to emit a literal one.
6. **Keep the escalation token out of the tracker's handoff block** unless you are escalating right now.
   The match is a plain substring, so prose *describing* the convention parks the run as hard as raising
   one — and the park then hot-loops and floods the notification channel. Write "escalation" in prose;
   the literal form is the word HUMAN followed by a colon.
7. **`face-go`:** pad plain text then style, never width-format an ANSI string; clamp with `MaxWidth`
   and `MaxHeight`; goldens pin UTC and live in `face-go/internal/tui/testdata/golden` — regenerate in a
   separate rebaseline commit, and read `face-go/STYLE.md` before any face change.
8. **PowerShell tooling targets Windows PowerShell 5.1 and stays ASCII-only** — em-dashes have torn
   string literals here before. The ratchet gate is `tools/gates/ratchet.ps1`, and the wrong path prints
   an error but exits 0, a silent false green. Never raise the pragma ceiling; fix the cause.
9. **Orphaned `dotnet test` hosts flake the full suite.** Stopping one is sanctioned only after trap 4's
   command-line check proves it belongs to this repo.
10. **The conductor MCP task tools may arrive deferred in your harness.** Search for `task_update`
    first, or use the CLI: `conductor task --done ID --evidence PATH`. That command is the only claim;
    prose moves nothing.
