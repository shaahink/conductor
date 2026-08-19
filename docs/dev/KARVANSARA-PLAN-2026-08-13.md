# Karvansara — the era of the open door

**2026-08-13 · ITERATION 2 — compiled.** Iteration 1 was findings + era plan for owner review.
The owner's second brief (same day) accepted the direction and added three asks, all folded in
below: **GitHub sync is committed, not cut-first** (now core stage KS9, decision ND-9); **long
text must be readable in every Face section** (now KS2.7/KS2.8, decision ND-10 — the draft had
no readability work at all, an audit gap); and **compile it into a conductor plan now**. Plan I
is compiled at `plans/karvansara/core.plan.json` + `CORE-TRACKER.md` + `templates/`; Plan II
compiles at the era boundary after the budget re-measure (D-1 discipline). Iteration 2 also
fixed a craft omission: the draft's core plan had no ship stage — Karvan's K7 pattern
(reconcile, owner-signed merge, tagged release, reinstall) returns as KS10.

**Name.** *Karvansara* (کاروانسرا) — the caravanserai. Sarban drove the caravan; Karvan was the
caravan; the karvansara is where caravans gather between journeys — where every run that ever
passed is in the ledger, where the next journey is planned, and where you walk in through one
door. That is literally this era's centerpiece feature. Naming was parked by owner decision on
2026-08-07; this is the proposal, decision ND-1 below. (Alternates considered: *Rahdar* — the
road-warden, fits the verification-hardening half; *Manzel* — the day's halting place. The
*public* product name question is separate, still parked, and now more urgent — see §3.4.)

**Inputs.** Four parallel audits run 2026-08-13: (1) a very-thorough capability inventory of the
repo at `master`/`304fc5b` against installed engine `0.4.0+1a554372eb74`; (2) a mining pass over
the full operational history — machine catalogue, bug ledgers, followups, field logs, git history;
(3) a market/OSS landscape audit (August 2026 sources, URLs inline where they survive into this
doc); (4) a Claude Code / Agent SDK feature catch-up (directionally useful, **flag-level
unverified** — every CLI flag it named is treated as "verify against the installed binary first").
Plus the two prior planning artifacts, absorbed rather than re-litigated:
`docs/dev/NEXT-ERA-FINDINGS-2026-08-04.md` (decisions D-1…D-7) and
`docs/dev/NEXT-ERA-VERIFIED-PLAN-2026-08-07.md` (the truthful-read-side epics — placed into the
repo today from the scratchpad where it had been stranded since 2026-08-07).

**The owner's ask, as interpreted** (from a voice-transcribed brief; interpretations flagged):
plan the next iteration in full depth; audit the tool against agent-driven-programming concepts
("this is my harness"); audit the open-source market for what it has that conductor doesn't —
explicitly *not* web chrome; catch up with recent Claude Code features and find integration
points; mine the history of runs, bugs, and findings; and one concrete product wish — **typing
`conductor`, with or without a plan present, opens the app: see previous runs, plan a new run,
switch between runs** — with **no hand-written JSON** (interpreted from "there is no adjacent" ≈
"there is no JSON"; flag if wrong).

---

## 1 · The harness audit — conductor against agent-driven-programming concepts

Twelve concepts, each graded honestly against the code (file evidence in the inventory sweep) and
against the August-2026 state of the art (§3). **Strong** = at or beyond market;
**partial** = present with real gaps; **weak** = market has it, conductor doesn't.

| # | Concept | Grade | One-line verdict |
|---|---------|-------|-----------------|
| 1 | Verification & trust | **strong** | The unoccupied quadrant — nobody else ships evidence-based verdicts + deterministic gates as run progression. Gaps: visible gates are gameable (holdout/regression/mutation classes missing). |
| 2 | Durability & state | **strong (write) / partial (read)** | Event fold as truth is Temporal-grade discipline; but the read side still lies (FU-F1-06, three eras old) and the catalogue corrupts itself on import. |
| 3 | Budget & economics | **strong** | Billed-money-only is unique in the field. Gaps: lane/advisor spend uncounted, no machine-wide ledger, `approve` semantics surprise the operator. |
| 4 | Context engineering | **partial** | Handovers + batteries + fresh-context sessions are the right shape; B7 ("attack the 66%" — cache reads are two-thirds of every bill) is designed and unbuilt. |
| 5 | Memory & learning loops | **partial** | lessons/bugs/followups feed forward, but "two ledgers, one purpose, two formats" is unresolved, retrieval is parked, and lessons.md just shipped a duplicate-append regression (K7-32 twice). |
| 6 | Human-in-the-loop | **partial** | Owner gates, parks, Telegram push are real; park hygiene is not (one park → ~200 phone notifications; a transient DNS cut → a 14-hour park). |
| 7 | Observability | **partial** | Face + history + control plane are deep for a live run; there is no machine-level surface, no attach-to-past-run, no standard export (OTel/ATIF). |
| 8 | Ergonomics & entry | **weak** | Bare `conductor` prints 41 verbs; `status` in a multi-plan repo errors out; plan authoring is hand-JSON with silently fatal traps — "the roughest UX in the audit" held up against the market. |
| 9 | Parallelism & isolation | **weak (by decision)** | A cycle-validated DAG, a worker pool, and worktree machinery all exist and none of it schedules anything; `mutatingLanes[]` is config nothing reads. The lanes plan is authored, unlaunched. |
| 10 | Safety & containment | **weak** | Gates constrain *progression*, not *blast radius*: sessions run `--dangerously-skip-permissions`, no sandbox. (Anthropic's own sandbox is WSL2-only; Codex proved native-Windows containment is possible.) |
| 11 | Interop & standards | **partial** | Three agent providers (claude/opencode/generic) is more agent-agnostic than most; one MCP server (tasks) exists; no MCP read surface, no AGENTS.md, no trajectory export, GitHub sync designed-not-built. |
| 12 | Planning & decomposition | **partial** | Stages→checkpoints with falsifiable exits is ahead of spec-driven tools' execution story; but plan *generation* (PRD→plan) — table stakes at Task Master/spec-kit/Kiro — doesn't exist (`init --from-idea` is a scaffold, `plan import` needs an existing plan to diff). |

The assessment in one paragraph: **the loop is world-class and lonely; everything around the loop
is where the operator's time goes.** The corpus (§4) proves the core thesis — 22 runs, 408
sessions, ~$3.7k, 96.5% checkpoint completion, multi-day unattended self-hosting with zero
rollovers in the last era — and it equally proves that the surfaces, the catalogue, the authoring
path, and the entry point are the tax. The market audit confirms the same from outside: the
differentiators are all in the loop (verdicts, billed-only money, event fold, endurance); the
gaps are all around it (entry, authoring, isolation, containment, export).

---

## 2 · What the history says (evidence base)

Corpus at 2026-08-13, via `conductor history --json --limit 0`: **22 runs · 408 sessions ·
386/400 checkpoints (96.5%) · ≈$3,736 · ≈4.84B tokens · 98.3% cache reads · blended ~$0.74/M**.
Karvan core closed at 24 checkpoints × 16.8M tokens × $13.24, zero rollovers in 30 costed
sessions — the measured basis for this era's budget (§7).

Ranked recurring pains (each with a worked trail in the mining report; top evidence only here):

1. **Read-side truthfulness.** The largest bug class of all three eras. FU-F1-06 (a run that ends
   NeedsHuman/Paused reads `status='running'` forever — `UpdateRunStatus` exists nowhere) is open
   since 2026-07-10 and four phantom "running" rows exist today. The Karvan run itself had to be
   **closed by hand-edited SQL in two databases** because no CLI verb closes a run record
   (`.conductor/WATCH-HANDOFF.md`).
2. **Catalogue self-corruption — demonstrated live during this audit.** Legacy-db import keys on
   plan-slug, not run-id: three read-only `conductor bug list` invocations grew the catalogue
   22 → 31 rows, listing the Karvan core run four times (twice disagreeing on its own checkpoint
   count). Every plan interaction in an old repo mints duplicates; payesh's harvest already
   refused to run over one such duplicate on 2026-08-13.
3. **Plan authoring is hand-JSON riddled with silent traps** — brace landmines killing runs 13
   hours in via stderr nobody reads, letters-only stage ids silently owning zero checkpoints,
   `plan set --create` minting dead keys, the editor rewriting whole files and twice adding
   phantom stages, a merely-*mentioned* `HUMAN:` token hot-looping ~200 phone notifications under
   `--dry-run`.
4. **Windows sharp edges**: ~8191-char argv ceiling silently truncating the agent while the run
   reports success (bug #15, three eras old, #21 same root); the gate battery rebuilding the
   *running* `conductor.exe` (bug #16 — "no stage has ever owned tools/gates, which is why this
   survived three eras"); `CONDUCTOR_PLAN` beating CWD so scratch rigs target the driving run's
   plan (bug #20, "the sharpest of the seven").
5. **Budget semantics that surprise**: `approve` on a budget park resets the counter rather than
   raising the ceiling; the cap check runs before the queued plan reload; `--once` loses session
   cost from the persisted window.
6. **Monitoring fragility**: monitors armed on grepped log strings; parks indistinguishable from
   healthy silence; no monitor-listing verb; a transient DNS cut escalated to a 14-hour park.
7. **Multi-repo blindness**: `satelliteRepos` is verdict-only; the nine-satellite ci-health run
   worked because a human hand-wrote eight numbered traps into `promptExtra`. (This is the lanes
   plan's reason to exist — authored, 0/23, unlaunched.)
8. **Ledger duality**: bugs vs followups — "quietly become one ledger with two file formats";
   era-end reconciliation is still hand-written.
9. **Self-hosting install confusion**: engine-on-PATH vs source-being-edited burned sessions in
   every era; provenance (K3.3) fixed the *record*, not the ritual.
10. **No front door** — measured, not aesthetic: bare `conductor` prints help; `conductor status`
    in a multi-plan repo **errors** ("Multiple plan files found and output is not interactive to
    prompt") — the first command this audit ran, failing.

Open ledger feeding this plan: bugs #15 #16 #18 #19 #20 #21 #23 #24 #27 #31 #35; FU-F1-06,
FU-B2-3, FU-B11-2/3; NEXT-FEATURES rows 1–10 (notably **B7** context economics, **G4** git safety
"blocks the lanes plan", `mutatingLanes[]` inert-key row, lane-spend-outside-cap row); the
GitHub-sync design (built nothing yet, its own sequencing recommendation adopted here); the
verified plan's T/S/Q epics.

---

## 3 · The market audit (August 2026) — what it means for conductor

Full sourced survey in the audit transcript; what survives into the plan:

### 3.1 Verified differentiators — the moat is the combination

- **Evidence-based verdict engine + deterministic gates as run progression.** Nothing shipped
  does this. Spec-kit (127k★) stops at markdown and its `/speckit.implement` famously doesn't
  load its own constitution; BMAD's QA gates are LLM-judging-LLM; claude-flow's verification is
  documented theater ("99% Theater, 1% Real" audit); Kiro's EARS→property-tests is the lone
  partial exception. The 2026 reward-hacking literature (Anthropic arXiv 2511.18397: agents that
  game visible tests generalize to sabotage; SpecBench: every frontier agent saturates visible
  suites while failing holdouts) just made conductor's "never trust the agent" posture the
  *provably correct* one — and also names its gap (§3.2 item 2).
- **Billed-money-only accounting.** Unique. Every cost tool found (ccusage, /cost, OTel gen_ai
  cost attrs) estimates from token counts × price tables.
- **Days-long unattended *local* runs** with session recycling + handovers. Ralph (21.5k★ +
  first-party Anthropic plugin) has the mindshare with weak verification; Warp Oz and Devin do
  long runs in *their* cloud; Prime Agent caps autonomy at minutes by design.
- **Event-fold as source of truth** — enables "replay a run under new verdict logic" almost for
  free (the Temporal trick; nobody in the coding-agent space does it).
- **Windows-first** — underserved: Claude Code's sandbox is WSL2-only, Sculptor has no Windows
  build. Sustained by the graveyard evidence (Bloop/vibe-kanban dead, Terragon dead, Crystal
  dead): local-first with no revenue need is itself a survival moat.

### 3.2 Market-present gaps, ranked (adopted into §6)

1. **Worktree isolation even single-agent** — the most-adopted pattern of 2025-26; a worktree per
   *stage attempt* = free rollback + a clean attempt diff as verdict evidence. → KS4.4
2. **Anti-gaming gate classes** — holdout gates (commands the agent never sees), PASS_TO_PASS
   regression gates (SWE-bench's second half), mutation-score gates (the only deterministic
   counter to the-agent-wrote-both-code-and-tests). → KS4.1–4.3
3. **File-state rollback** — Gemini's shadow-git snapshots, `/rewind`, Conductor.build
   checkpoints; conductor records history but can't mechanically undo a failed stage. Largely
   subsumed by worktree-per-attempt. → KS4.4
4. **Plan generation/authoring** — Task Master parses a PRD into a dependency-ordered task tree;
   spec-kit and Kiro author specs→tasks; conductor hand-authors JSON. → KS3
5. **Containment** — OS-level blast-radius limits; Codex CLI shipped a *native Windows* sandbox
   (restricted tokens/AppContainer/ACLs), proving stock Windows primitives suffice. → KS7.1 now
   (permission posture), full sandbox parked (§8)
6. **Machine-readable run exports** — Factory's public `stream-jsonrpc`, ATIF trajectory format
   (Terminal-Bench's Harbor; includes token usage *and costs* — fits the billed-only rule). → KS8.2
7. **MCP exposure of the run** — read-only only; MCP's 2026 security record (OWASP tool-poisoning
   entry, CVSS 9+ disclosures) argues control ops stay off it. → KS8.1
8. **Advisory judge as evidence source** — Amp's oracle, Anthropic's own harness-design guidance
   (separate evaluator, "self-evaluation produces confident praise of mediocre work") — as
   *evidence into* the deterministic verdict, never the verdict. → KS4.5
9. Agent-agnostic backends — conductor already has three providers; keep, don't chase parity.
10. Recipes/scheduling/mobile two-way — peripheral; skip.

### 3.3 Standards bets

**Adopt:** MCP read-only surface; OTel gen_ai *names* mirrored in the event log (schema still
"Development" status — don't hard-commit); ATIF export. **Courtesy:** AGENTS.md (note: Claude
Code *still* doesn't read it natively; the `@AGENTS.md`-import-from-CLAUDE.md pattern is the
working play). **Watch:** ACP (Zed 1.0/JetBrains momentum, but it standardizes editor↔agent and
conductor sits *above* the agent). **Skip:** A2A; swarm frameworks.

### 3.4 The name

"Conductor" is one of the most collided names in dev tooling: **Conductor.build** (YC, *exactly*
this space — parallel Claude Codes on Mac), **Netflix Conductor/Orkes** ($60M raise, April 2026,
marketed for AI-agent orchestration), and **Google's own "Conductor"** for Gemini CLI. Public
discoverability under the bare name is zero. The Payesh precedent (site renamed) is the right
instinct; the public-name decision stays parked but should not survive another era unmade. Era
names (Sarban/Karvan/Karvansara) are unaffected.

---

## 4 · Claude Code catch-up — integration candidates (all verify-first)

The catch-up audit was directionally useful but conflated some of conductor's own roadmap with
platform features and named flags that must be verified against the installed CLI before any
checkpoint depends on them. What matters to an external orchestrator, ranked by fit:

1. **Hooks as ground-truth telemetry.** The hook surface has grown (PreToolUse/PostToolUse/Stop/
   SubagentStop/SessionStart/PermissionRequest, JSON-on-stdout protocol, non-command hook types).
   Conductor already owns the pattern — the hidden `hook-budget` verb delivers the soft break as
   a PostToolUse hook. Extending that channel to structured tool events would replace transcript
   parsing as the primary source (and fix the bug #19 class: digests counting MCP claims while
   sessions claim via CLI). → KS7.2
2. **Permission posture.** Permission modes + allow/deny rules in settings have matured to where
   an unattended run may no longer need `--dangerously-skip-permissions` — an allowlist profile
   with explicit deny rules is both safer and what the market ships. → KS7.1
3. **Per-turn usage with cache split** off stream-json, and **OTel export** from Claude Code
   itself — feeds K4.1's context-per-turn with provider-reported numbers and gives the era's
   observability work a peer to reconcile against. → KS7.3
4. **Session forking** (fork-a-session rather than cold resume) for fix/audit sessions; re-verify
   resume flag behavior; re-measure the model lineup/context ceilings for the budget table.
   → KS7.4
5. **Skills as per-repo knowledge injection** — potentially lighter than growing `promptExtra`;
   evaluate, don't assume. → folded into KS7.2's scope decision.

Explicitly not chased: Agent Teams (within-session collaboration — orthogonal to conductor's
between-session model), managed/cloud agents (against the local thesis), native task queues.

---

## 5 · Decisions for the owner — ND-1 … ND-8

House rule from NEXT-ERA-FINDINGS: a recommendation is given for every one, the plan as written
implements the recommendation, so **silence is a valid answer**.

| # | Question | Recommendation (implemented below) |
|---|----------|-----------------------------------|
| **ND-1** | Era name? | **Karvansara.** Stage prefix `KS` (satisfies `[A-Za-z]+\d+`). Public product name stays a parked-but-aging item. |
| **ND-2** | What does bare `conductor` do? | **The hub** (§6 KS2): resolve machine state; live run → attach; otherwise the caravanserai — recent runs, plans found, start/plan/switch. `--help` and every existing verb unchanged; non-TTY prints a status board instead. |
| **ND-3** | Plan authoring contract? | **JSON stays the compiled machine contract; a human never writes it by hand.** Authoring is interview/import/editor (KS3). Explicitly rejected: a parallel Markdown plan format — two sources of truth is the disease the work graph cured. |
| **ND-4** | One plan or two (or fold into lanes)? | **Two plans, then lanes** — same D-1 discipline: `karvansara-core` (KS0–KS3, KS5) then `karvansara-edge` (KS4, KS6–KS8), budget re-measured between; the lanes plan launches after edge, amended per ND-8. |
| **ND-5** | Drop `--dangerously-skip-permissions`? | **Yes, gated on verification** that an allowlist/deny profile sustains a karvan-class run (KS7.1). If the CLI can't yet, keep the flag and file the finding. |
| **ND-6** | Standards adoption? | MCP **read-only** yes; ATIF export yes; AGENTS.md courtesy yes; OTel **names only**; ACP no; A2A no. (§3.3) |
| **ND-7** | Catalogue repair now or in-era? | **In-era but first** — KS0.1 opens the era (self-hosting discipline: the era fixes itself). Until it lands, the standing workaround: check `imported.json` before diagnosing duplicates; payesh harvest reads the deduped view only. |
| **ND-8** | Does KS4.4 (worktree-per-attempt) pre-empt lanes L1? | **Yes, deliberately.** It builds the single-lane base of L1/G4 (branch-safety code, the `-D`-loses-work fix, Windows lock handling). The lanes plan gets a small amendment: L1.3's correctness fix lands here; L1 rescopes to multi-lane generalization. G4 stops blocking lanes. |
| **ND-9** | Where does GitHub sync live? *(new, owner ask 2026-08-13: "with github sync")* | **Promoted from cut-first KS8.3 to core stage KS9** — phases 1–2 of the committed design (backfill verb, then the live reconciler mirror) are committed work; Projects v2 (KS9.3) is the stage's own cut line because the owner's `gh` token lacks `project` scope (one-time `gh auth refresh -s project` is a launch-drill item, not a checkpoint). One-way push, off by default, nothing inbound — L6.3/D-7 stand unrelitigated. The self-hosting payoff: KS10.3 backfills **this very run's** board to GitHub as the era's closing act. |
| **ND-10** | Long-text readability in the Face? *(new, owner ask 2026-08-13: "cannot read long texts in different sections")* | **Committed as KS2.7 + KS2.8.** Karvan's K6.2 adopted the ADR-0006 pane viewport for Report/Knowledge/Home/Owner/Templates and stopped; a fresh audit (2026-08-13) shows the Agent tab still hand-rolls `consoleScroll` — the exact unbounded-scroll pattern the ADR was written to kill (bug #30) — and Kanban detail, History, Telegram and Processes truncate long cells with no way to open them. KS2.7 finishes viewport adoption everywhere; KS2.8 adds a full-screen reader overlay so any truncated cell can be opened and read to its last line. |
| **ND-11** | Era-close craft? | **KS10 "Ship core" (ownerGate) added** — the draft omitted the K7 pattern entirely. KS10.1 reconciles the internal record (ARCHITECTURE + `docs/dev/`) + closure ledger and re-measures the budget via `conductor budget` (the number edge compiles against); KS10.2 reconciles the published surface (ND-12); KS10.3 is the owner-signed merge, tagged release, reinstall, and the first real GitHub backfill of this run. |
| **ND-12** | What guarantees README/docs/GitHub actually match the shipped engine? *(new, owner ask 2026-08-13: "make sure docs readme github and so on will be synced after this plan")* | **A dedicated checkpoint, KS10.2, and a test that outlives it.** "Docs reconcile" as a clause inside a ship checkpoint is how doc drift survives an era: the ledger and the budget re-measure are concrete and crowd it out. KS10.2 names the published files — README, the `docs/` user set and its index, `.github/` templates, and the `[Unreleased]` CHANGELOG section, which `release.yml` uses verbatim as the release body, so **the CHANGELOG is the GitHub sync for humans** while KS9 is the sync for machines. The durable half is extending `SF7_1DocsMatchRealityTests`: a verb this era adds that never reaches `cli.md` must fail the battery, in this era and every era after it. The field guide (payesh) is included because KS0.1 changes the store its harvest reads — a green harvest on the deduped catalogue is the end-to-end proof that the era's first fix did not break its most public consumer — but it lands as a PR, because publishing to a live site is the owner's signature, not a session's. |

---

## 6 · The era plan

### Thesis

> **The era of the open door.** The engine's write side never believed the agent; the last plan
> made the read side equally skeptical. This era finishes that debt — then opens the door: one
> command, every run this machine ever ran, truthfully listed, attachable, and a new journey
> plannable without a human ever hand-writing JSON. Then it hardens the gates against the one
> adversary the literature now names — the agent that learns to pass them — and catches the
> harness up to its platform. **Credibility before capability, and the door is only opened onto
> surfaces that cannot lie.**

The ordering dependency is real, not rhetorical: the hub (KS2) lists runs — it must not list four
phantom "running" rows and a quadruplicated Karvan. Truth (KS0/KS1) therefore precedes the door.

### Plan I — `karvansara-core` (31 checkpoints, 7 stages)

Compiled: `plans/karvansara/core.plan.json` + `plans/karvansara/CORE-TRACKER.md` +
`plans/karvansara/templates/`. Branch: `feat/karvansara`.

**KS0 — Leftovers: the catalogue stops corrupting itself** *(3 checkpoints)*

| cp | Work | Falsifiable exit |
|----|------|------------------|
| KS0.1 | Import dedups by **run id**, not plan slug; `imported.json` consulted before import; a repair pass collapses existing duplicates (with backup) | `bug list` under three different plans in an old repo adds **zero** rows; catalogue back to one row per real run; payesh `npm run evidence` green on the deduped store |
| KS0.2 | `conductor run close\|adopt <id>` — a verb closes/annotates a run record with provenance; non-terminal parks get an honest status writer (the engine half of FU-F1-06) | The four phantom rows closed **via the verb**; WATCH-HANDOFF's hand-SQL procedure reproducible by CLI on a copy; no `runs` UPDATE outside the store |
| KS0.3 | Sharp-small batch: gate battery must never rebuild the running engine (bug #16 — build to shadow path); plan resolution prefers CWD over `CONDUCTOR_PLAN` with a warning on override (bug #20); first-write FK error on fresh run.db (bug #27); lessons.md duplicate-append regression (K7-32 twice) + pinned test | Each bug's reproduction script goes red→green; bug ledger rows closed with commits |

**KS1 — Truth: every read surface reconciles** *(6 checkpoints — the verified plan's Epic 1,
scopes as corrected there)*

| cp | Work | Falsifiable exit |
|----|------|------------------|
| KS1.1 | Plan reload updates the run row; limits provenance labeled "at launch / now" | Mid-run limits edit → `history` shows both at the same boundary; test asserts the UPDATE |
| KS1.2 | Stage rows derived from the event fold; `stages` side-table reads retired (template: `RunArchive.Checkpoints()`) | Derived stage status matches the status surface for **all** archived runs; architecture test forbids readers of `stages.session_count` |
| KS1.3 | Render-time liveness reconciliation in `history`, the fleet list, and `--json` (closes FU-F1-06/D11 read half; KS0.2 was the write half) | A killed engine's run never lists as `running`; `--json` carries reconciled status (the CV evidence pipeline quotes it) |
| KS1.4 | Doctor plan-semantics lints: gate-command path probe, checkpoint-id-vs-tracker cross-check, hook dry-run, plan-drift, composed-prompt **argv-length** check (authors' half of #15/#21), brace sweep, `HUMAN:`-token sweep over notes/templates | Doctor red on each of seven seeded trap plans; the sarban-face launch-drill checklist items all owned by a check |
| KS1.5 | ARCHITECTURE.md rollback paragraph rewritten to match `ControlDispatcher` (`reset --hard`, `--force` semantics) | Docs-match-reality test covers the claim |
| KS1.6 | The invariant as an architecture test: readers outside the engine may not consume mutable snapshot columns that have a fold-derived equivalent | New rule in `ArchitectureBoundaryTests`, green on the tree, red on a seeded violation |

**KS2 — The open door: bare `conductor` is the app, and every section of it reads** *(8 checkpoints)*

| cp | Work | Falsifiable exit |
|----|------|------------------|
| KS2.1 | Default command: `conductor` (no args, TTY) resolves the machine state home and opens the hub — recent runs (live + past, reconciled), plans discovered here, actions (attach / start / plan new / history). Non-TTY prints a status board, exit 0. `--help`/verbs unchanged | On a machine with **no plan anywhere**, `conductor` shows the caravanserai, not 41 verbs; scripts calling verbs see no behavior change |
| KS2.2 | The archive serves: a read-only control-plane mode over the state home so **the Face attaches to finished runs** (today the picker admits it cannot) | Pick a completed run → sessions, money, timeline, report render; no engine process for that run exists |
| KS2.3 | Start a run from the hub: choose plan → journey preview → launch **detached** engine (stderr redirected — the drill's hard-won launch shape becomes the code path) → attach | A run started from the Face appears in `ps`; killing the Face leaves the engine alive; the FIELD-LOG detached-launch incantation retired |
| KS2.4 | Switch runs: one picker merging fleet probe + catalogue — live runs attach, past runs open read-only, across repos | Two live runs + archive in one list; switching preserves theme/session; write tokens never cross runs |
| KS2.5 | `conductor status` with no resolvable plan = machine-level board (`ps` + catalogue summary), never an error | The "Multiple plan files found…" error is unreachable; multi-plan repo answers usefully |
| KS2.6 | Park hygiene: a park emits once; notifier rate-limited with a max-per-incident; `--dry-run` never notifies; NEEDS HUMAN holds silently as long as it takes; a monitor/watch listing verb | Replay of the 2026-08-02 `HUMAN:` incident → **1** notification; DNS-blip replay → backoff, no 14-hour park without a push saying so |
| KS2.7 | **Long text scrolls everywhere** — finish ADR-0006: every long-text surface owns a pane viewport (Agent console + transcript, Kanban detail, History, Telegram feed, Processes); the last hand-rolled scroll integers (`consoleScroll` and kin) deleted, `applyPaneScroll` the only key path | Glitch-sweep renders a 500-line body in **every** tab scrolled to its end; a module-intent test forbids new bespoke scroll fields outside the viewport wrapper; goldens rebaselined in a separate commit |
| KS2.8 | **The reader** — one full-screen overlay opens any truncated cell or row (checkpoint note, evidence text, session result, gate output, telegram message): soft-wrapped prose, the ADR's pager keys, percent readout, themed markdown where the body is markdown, `esc` back | A 2,000-line report and a 300-char kanban note both readable to the last line at 80×24; frame-invariant tests cover the overlay; no surface answers "long text" with silent truncation |

**KS3 — Authoring: no human writes JSON** *(5 checkpoints)*

| cp | Work | Falsifiable exit |
|----|------|------------------|
| KS3.1 | `conductor plan new` — agent-assisted interview from an idea / PRD / existing tracker → plan JSON + tracker + templates, doctor-clean by construction; no diff-against-existing needed (closes NEXT-FEATURES #5) | From an empty repo: one command → doctor 0-fail plan + tracker; the JSON is never opened in an editor during the drill |
| KS3.2 | The editor stops destroying: comment header preserved across `plan set`/`add-stage`/import (parse-preserving edit or an owned `header` field); no more silent progress-kind/gate-timeout rewrites | Replay of the plan-editor trap (memory: adding a stage changed three unrelated things) → diff shows only the stage |
| KS3.3 | Schema honesty: the eight undocumented keys documented (`supervisor`, `packs`, `pipeline`, `verifyEachDelivery`, `limits.maxSessions`, `stallGraceMinutes`, `authPreflight`, `sameFailureCircuitBreaker`, `verifierThreshold`); `mutatingLanes[]` **removed or wired** (NEXT-FEATURES #7 — the GitHub-sync design already cites it as the anti-pattern); doctor warns on inert keys | plan-config.md matches `PlanConfig` under the docs-match-reality pin; no settable key is read by nothing |
| KS3.4 | `conductor preflight` — the launch drill as a verb: doctor + journey + dry-run compose + version-vs-release + rebuild check + escalation-block check, one command, one verdict | Each seeded drill failure caught; the conductor-drive skill's manual checklist section marked superseded |
| KS3.5 | Import bridges (cut-first): spec-kit `tasks.md` / Task-Master `tasks.json` / plain markdown checklist → plan; "the SDD execution layer" pitch | A spec-kit sample converts and drives `conductor demo` to completion |

**KS5 — Spend: every dollar the tool can spend is governed** *(4 checkpoints — verified plan's
Epic 2 + the uncounted paths)*

| cp | Work | Falsifiable exit |
|----|------|------------------|
| KS5.1 | Machine-wide ledger verb: "what did this machine spend this week/month," billed-only, across the catalogue | Totals cross-check against per-run `money`; no price table appears anywhere in the diff |
| KS5.2 | Lane/advisor/supervisor spend counted: cost rows for every spawned model process; caps see them (NEXT-FEATURES lane-spend row; advisor's flat `0.0005×seconds` replaced by billed rows where the provider reports) | No spend path uncounted — architecture test: any process-spawning path that takes a model must write a `costs` row |
| KS5.3 | BudgetAnalyzer prescriptions surfaced at plan-reload | A reload whose ceiling contradicts the measured floor/prescription logs the disagreement at the boundary |
| KS5.4 | Approve/cap semantics: `approve` on a budget park **raises the ceiling explicitly** (amount stated) instead of resetting the counter; the cap check runs after the queued reload applies | Replay of FIELD-LOG 2026-07-29 19:03 → no silent double-spend; a boundary cap-raise saves the run it was raised for |

**KS9 — The far door: GitHub is the remotest view** *(3 checkpoints — the committed design
executed; one-way push, off by default, nothing inbound; promoted per ND-9)*

| cp | Work | Falsifiable exit |
|----|------|------------------|
| KS9.1 | Token + client + backfill: SecretsStore gains the GitHub field (+ `CONDUCTOR_GITHUB_TOKEN` override); raw HttpClient client on the ReleaseClient pattern (no Octokit); `conductor github sync --backfill <run>` posts a **finished** run's board (one issue per TaskItem, upsert-never-clobber, retire-don't-delete) and its diary (`run:` issue, one comment per SessionFinished) to a scratch repo | The design doc's own acceptance: a finished Karvan run's board appears in a scratch repo; re-running the backfill mints **zero** duplicates; nothing inbound; off by default |
| KS9.2 | Live mirror: a reconciler over `IRunStore.ReadEventsAfter` (never a hot IEventSink) converges the board while a run is live — batched, network-failure-proof, resumable from its cursor | Kill the network mid-run: the run is unharmed and the board converges on reconnect; replay from cursor zero produces zero duplicate issues; the tracker remains the verified contract (nothing reads GitHub) |
| KS9.3 | *(cut-first)* Projects v2 board via GraphQL — columns mirror stage status; needs the one-time `gh auth refresh -s project` (launch-drill item, ND-9) | The board's columns match `conductor status` for a live run; without the scope the checkpoint reports the precise refusal and stays SKIPPED, not half-done |

**KS10 — Ship core** *(3 checkpoints, ownerGate — the K7 pattern, restored per ND-11, with the published surface split out per ND-12)*

| cp | Work | Falsifiable exit |
|----|------|------------------|
| KS10.1 | Reconcile the **internal** record: ARCHITECTURE.md and `docs/dev/` vs the engine for everything this plan changed; closure ledger naming every bug/followup row closed here or its living owner; `conductor budget` re-measured and written into TOKEN-BUDGET-TUNING — the number edge compiles against | Budget doc carries this run's figures produced by the verb, not by hand; every wrong claim in this plan corrected in place, not dropped |
| KS10.2 | Reconcile the **published** surface, and pin it: README.md (the hub is now the app — KS2.1 changes the first thing a reader runs), `docs/` user set (`cli.md`, `operating.md`, `quickstart.md`, `troubleshooting.md`, `tracker.md`, `plan-config.md`, the `docs/README.md` index), `.github/` templates where a verb changed, and the `[Unreleased]` CHANGELOG section written as the **release body the world reads** (`release.yml` refuses a tag whose section is missing). Extend `SF7_1DocsMatchRealityTests` so every verb this era added and every command README quotes is pinned by a test, not by good intentions. Then the field guide: re-run payesh's harvest against the **deduped** catalogue, on a branch with a PR — never publish to the live site from a session | `conductor --help` lists no verb absent from `cli.md`; every command block in README executes as written; the docs-match-reality suite goes **red** on a seeded stale doc; CHANGELOG's section names this era's user-visible changes; payesh harvest green on the deduped store with its PR open — or, if it cannot run for a reason outside this plan, the precise refusal in the closure ledger and that half SKIPPED |
| KS10.3 | Owner-signed: merge `feat/karvansara` to master, tagged release through the pipeline (the CHANGELOG section from KS10.2 *is* the release body), reinstall with reported version matching the releases page — then the era's closing act: `conductor github sync --backfill` **this run**, the first real use of KS9 on the run that built it; and merge the payesh PR so the field guide matches the shipped engine | Installed engine reports the new version; the releases page body is KS10.2's section; the karvansara-core run's board is live on GitHub; owner confirms no other conductor run is live before the reinstall |

### Plan II — `karvansara-edge` (16 checkpoints, 4 stages)

Compiles at the era boundary, after KS10.1's re-measure (D-1 discipline).

**KS4 — Verification that can't be gamed** *(5 checkpoints)*

| cp | Work | Falsifiable exit |
|----|------|------------------|
| KS4.1 | **Holdout gates**: `visibility: holdout` gate class — excluded from prompts, tool contract, logs the agent reads; run only by the engine at verdict time | Grep of composed prompt + transcript proves absence; a seeded gaming fake-agent passes visible gates, fails holdout, verdict red |
| KS4.2 | **Regression gate class** (SWE-bench PASS_TO_PASS semantics): "nothing that worked broke" as a named class with distinct reporting | A seeded regression flips the verdict with the class named in evidence |
| KS4.3 | **Mutation gate kind**: `mutation-score >= X`, diff-scoped (Stryker.NET first) — the deterministic counter to agent-writes-code-and-tests (absorbs verified-plan Q5) | A checkpoint adding tests must clear the score on changed files; era-boundary run on conductor's own suite recorded |
| KS4.4 | **Worktree-per-stage-attempt**: each attempt in a worktree; failed attempt = drop the tree (mechanical rollback); verdict receives the clean attempt diff; merge ff-only on green; **never `branch -D` an unmerged branch** (the L1.3 fix lands here per ND-8); Windows lock/removal path proven | A failed attempt leaves the main tree untouched; attempt diff in the evidence set; orphan sweep at startup; lanes-plan L1 amendment committed |
| KS4.5 | **Judge as evidence, never verdict**: second-model review whose structured output joins the evidence taxonomy (a new advisory row), with the deterministic signals still deciding (Anthropic's evaluator-separation guidance, Amp's oracle — adopted on conductor's terms) | Judge disagreement recorded as evidence; no code path lets a judge score flip a gate verdict |

**KS6 — Quality lane: hygiene that buys design** *(4 checkpoints — verified plan's Epic 3, rule
adopted verbatim: every hygiene checkpoint buys one permanent design asset)*

| cp | Work |
|----|------|
| KS6.1 | Curated Roslynator set (~25 design-shaped rules) as errors; everything else off |
| KS6.2 | Analyzer-debt count ratchet extending `ratchet.ps1` semantics (referee not editable by the agent) |
| KS6.3 | Complexity budgets (CA1502/1505/1506) with ratchets; first targets: the largest partial surfaces (VerdictEngine 8 files, ControlPlaneServer 11) |
| KS6.4 | Extract the pure "evidence → verdict" function from VerdictEngine — the era's one funded deep refactor; makes the taxonomy testable without the loop and gives KS4.5 a clean seam |

**KS7 — Platform catch-up (every checkpoint opens by verifying flags against the installed CLI)**
*(5 checkpoints)*

| cp | Work | Falsifiable exit |
|----|------|------------------|
| KS7.1 | Permission posture: allowlist/deny settings profile replaces `--dangerously-skip-permissions` for unattended runs, if the installed CLI sustains it; blast-radius story documented honestly (no native Windows sandbox from the vendor; posture stated in ARCHITECTURE.md) | A karvan-class stage runs green under the restricted profile, refusals telemetered; **or** a filed finding says precisely why not |
| KS7.2 | Hooks as ground truth: tool events delivered by hook (extending the `hook-budget` channel) become the primary source; transcript parsing demoted to fallback; digest claim-counting fixed (bug #19 class); skills-vs-promptExtra evaluated and decided here | Hook-derived digests match transcript-derived on a replay corpus; a hook-less agent still works (fallback proven) |
| KS7.3 | Cost/usage: per-turn usage with cache split parsed from the stream; OTel emit mirroring gen_ai *names* from the event log | Per-turn context curve reconciles with K4.1's derivation; an OTLP collector renders a run's spans |
| KS7.4 | Session lifecycle: fork-instead-of-cold-resume for fix/audit sessions where supported; resume flags re-verified; model lineup/context ceilings re-measured into TOKEN-BUDGET-TUNING | A fix session forks with measured token delta vs the resume baseline |
| KS7.5 | **Context economics (B7 — attack the 66%)**: gate output truncated in-prompt with full text as an evidence file; `RepoMapBattery` + definition-of-done recap battery (both fit the shipped `IPromptBattery` seam); templates teach search-delegation to subagents | Measured cache-read tokens per session drop vs the karvan baseline on a comparable stage, reported by `conductor budget` |

**KS8 — Interop (optional, cut-first)** *(2 checkpoints — KS8.3 GitHub sync promoted to core KS9, ND-9)*

| cp | Work | Falsifiable exit |
|----|------|------------------|
| KS8.1 | Read-only MCP surface: history/status/money as MCP resources; control ops excluded **by design** (ADR-0005 spirit; MCP's 2026 attack record cited in the ADR that documents this) | An MCP client lists runs and quotes reconciled status; no write tool exists on the surface |
| KS8.2 | ATIF trajectory export from the fold (`history export --atif`), billed costs included; AGENTS.md generated/honored via the CLAUDE.md-import pattern | An exported Karvan-core trajectory validates against the ATIF schema; 22 runs become shareable artifacts |

### Relationship to prior plans

- The **2026-08-07 verified plan** is fully absorbed: Epic 1 → KS1 (+KS0.2), Epic 2 → KS5,
  Epic 3 → KS6 (+KS4.3), Epic 4 → KS3.5. Its ordering rule and budget stand.
- The **lanes plan** (`plans/karvan/lanes.plan.json`, authored, 0/23) launches **after
  karvansara-edge**, amended once: L1.3's correctness fix and the single-lane worktree base land
  in KS4.4 (ND-8); its budget re-measured on karvansara's corpus per TOKEN-BUDGET-TUNING §7.
  G4 ("git safety is prose") stops blocking it. Nothing else in it is re-litigated.
- **D-1…D-7** all stand. The GitHub-sync design's sequencing recommendation (fold into this era,
  backfill first) is honored — and promoted — as core stage KS9 (ND-9): backfill proves against
  finished runs first, the live reconciler second, Projects v2 as the stage's own cut line.

### Budget prescription (measured, not guessed)

Basis: karvan-core at the tag — 24 checkpoints × **16.8M tokens** × **$13.24**, zero rollovers in
30 costed sessions at **32M / 0.70**; blended $0.74/M at 98.3% cache reads.

> ⚠ **Corrected in place at KS10.1 (2026-08-15): the ratio is 0.85, not 0.70.** The estimate above
> was written before this run had a corpus of its own. Re-measured on it — `dotnet run --project
> src/Conductor -- budget --json`, raw at `.conductor/evidence/KS10/ks10-1-budget-remeasure.json` —
> the prescription is *"the ceiling is right and the nudge is not: keep maxSessionTokens at 32M and
> move softBreakRatio 0.7 -> 0.85"*. The ceiling holds (zero rollovers in 12 costed sessions, three
> nudged and all three clean); the **nudge fires at 22.42M against a 22.08M median closer — 1.02×**,
> so the median session is told to wrap up at the point it would have closed anyway. Karvan
> prescribed the same 0.85 from headroom; this run prescribes it from the closers. **Where this
> paragraph and the prescription disagree, the prescription wins**, and it is `32M / 0.85` that
> karvansara-edge compiles against. Full working: `TOKEN-BUDGET-TUNING.md` §10.
>
> The per-checkpoint basis below survives the re-measure: karvan-core re-run today is 24 checkpoints
> × 16.8M × $13.24, unchanged. This run's own 22.2M / $16.56 per checkpoint is a **remainder**
> figure — KS3.4 took eight rounds, KS9 took two fix sessions — and is the variance edge should
> budget for, not the mean it should plan against (§10 shows the arithmetic and why `budget`'s own
> `tokensPerCheckpoint: 5.54M` for this run is a denominator artefact).

> ⚠ **Corrected again in place at KS12.1 (2026-08-19), and this time the ceiling moves — for the
> era *after* this one.** `32M / 0.85` is the pair edge actually ran under, and it held: realised
> `nudgeRatio` **0.8541**, **zero rollovers in 19 costed sessions**, **five sessions nudged and all
> five ended clean**, nudge 27.33M against a 20.19M median closer (**1.35×** — KS10.1's 1.02×
> complaint is answered). What the outturn exposes is the *ceiling*: the largest closer is **29.36M**
> against a 27.33M nudge, so the biggest sessions already run past the nudge and finish anyway.
> `budget`'s verdict, verbatim and with an empty `findings` array: *"set maxSessionTokens to 35M at
> softBreakRatio 0.9 - nudge 31.5M clears the 29.4M largest closer, headroom 3.5M is 2.0x the
> measured 1.74M wrap-up."* **Where this section and the prescription disagree, the prescription
> wins: the next era compiles against `35M / 0.9`.** Raw: `.conductor/evidence/KS12/ks12-1-budget-remeasure.json`.
> Full working: `TOKEN-BUDGET-TUNING.md` §12.

- **Keep 32M — and move the ratio to 0.85. Keep the model the pair was fitted to.** ~~32M / 0.70~~
  → **32M / 0.85** (nudge 27.2M), measured at KS10.1. The ceiling is only valid for
  `claude-opus-5`, the model whose run.db produced it (the sonnet→opus re-derivation was a
  measured **2.2×** scale factor, and doctor warns on neither direction). Any model change
  re-derives the pair from a fresh `conductor budget` before launch — KS7.4 re-measures the
  lineup for exactly this. Re-run `conductor budget` between plans (D-1 discipline) and let
  KS7.5's savings, if real, show up in edge's measure.
- **Plan I core, 31 checkpoints:** ≈ 520M tokens ≈ **$410** *(estimate at the measured
  16.8M / $13.24 per checkpoint)* — cap **$530** (~29% headroom, Karvan's ratio). Sessions
  planned 1:1 with checkpoints (Karvan's measured shape), `stageSlackFactor` 2.
- **Plan II edge, 16 checkpoints:** ≈ 270M tokens ≈ **$210** *(estimate)* — cap **$280**, minus
  whatever KS7.5 earns. **KS10.1's measure is in and it does not move this line:** the 16.8M / $13.24
  basis re-verified against a longer karvan run (30 costed sessions, 403.9M, still 16.8M/ckpt), so
  the estimate stands as written. What changes is the ceiling pair it runs under — `32M / 0.85`.
  ⚠ **Corrected in place at KS12.1 (2026-08-19) — this line was stale before the run started, and
  the outturn is now in.** Edge was *authored* at **24 checkpoints, cap $420**, not the 16 / $280
  drafted here; KS11 (the courier, 5cp) and KS4's fifth checkpoint did not exist when this was
  written. Measured at KS12.1 with 21 of those 24 closed: **366.8M tokens, $290.07, 17.47M /
  $13.81 per checkpoint** — **+4.0% tokens and +4.3% cost against the 16.8M / $13.24 basis**, which
  is the first per-checkpoint estimate in this project ever checked against a whole era's outturn
  and found to hold. Extrapolated to all 24 that is ~419M / **~$331**, inside the $420 cap.
- Cut lines, in order: KS8 (MCP/ATIF), then KS9.3 (Projects v2), then KS3.5 (import bridges).
  Core's KS0+KS1 are indivisible (the door doesn't open onto lying surfaces); KS9.1–9.2 are
  committed per ND-9 and not on the cut list.

### What this era proves (the CV sentence for each half)

1. *Core:* "One command opens every run the machine ever ran — reconciled against process
   liveness, deduplicated by identity, readable to the last line of the longest report,
   plannable without a human ever hand-writing config — on the same engine that never believed
   the agent. And the run that built it pushed its own board to GitHub."
2. *Edge:* "Gates the agent cannot game — holdout commands it never sees, regression classes it
   cannot trade away, mutation scores it cannot fake — enforced on the tool, by the tool, while
   it builds itself."

### Parked (explicitly not this era)

- **Public rename** — parked, aging, flagged (§3.4).
- **Full OS sandbox on Windows** (Codex-style restricted tokens) — KS7.1 documents the posture;
  building containment is an era of its own.
- **Same-repo parallel stages / scheduler / merge queue** — the lanes plan's L4, untouched.
- **Auto mode (D3), retrieval over history (A4)** — parked exactly as the findings doc parked
  them.
- **ACP, A2A, agent teams, cloud execution, mobile two-way** — surveyed, declined (§3.3, §4).
- **Bug/followup ledger unification** — real (pain #8) but unscoped; take a design note during
  KS0.2's ledger work and decide at the era boundary.

---

## 7 · Sources and provenance

Four audit transcripts (this session, 2026-08-13): capability inventory (repo @ `304fc5b`),
history mining (catalogue + ledgers + field logs; the import-duplication demonstration is
reproducible from `imported.json` timestamps), market landscape (URLs inline above; unverifiable
claims flagged in the transcript), Claude Code catch-up (**flag-level unverified** — treat every
CLI flag herein as a hypothesis until `claude --help` confirms it). Prior artifacts:
`NEXT-ERA-FINDINGS-2026-08-04.md`, `NEXT-ERA-VERIFIED-PLAN-2026-08-07.md` (both in `docs/dev/`),
`GITHUB-SYNC-DESIGN-2026-08-13.md`, `TOKEN-BUDGET-TUNING.md`, `.conductor/WATCH-HANDOFF.md`.

*Compiled 2026-08-13 (iteration 2): Plan I lives at `plans/karvansara/core.plan.json` +
`CORE-TRACKER.md` + `templates/`, carrying the twelve Karvan promptExtra traps plus the new ones
this audit filed (bug #20's plan resolution, the import-duplication workaround until KS0.1 lands,
the four phantom rows until KS0.2 closes them, and the F0–R0 phantom-stage scar in Karvan's own
tracker as the standing proof of trap #20). Launch drill before `conductor run`: (1) create
`feat/karvansara` from master; (2) confirm no other conductor run is live on this machine;
(3) `conductor doctor` + `journey` (Model column must read claude-opus-5, work line must read
31/7) + `--dry-run` with the composed prompt actually read; (4) `gh auth refresh -s project`
once if KS9.3 is to survive its cut line; (5) check `imported.json` before believing any
catalogue duplicate. Plan II compiles at the boundary from KS10.1's measure.*
