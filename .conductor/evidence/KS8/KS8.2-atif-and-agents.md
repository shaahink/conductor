# KS8.2 — the run as a shareable artifact, and the AGENTS.md courtesy

**Checkpoint**: KS8.2 — *ATIF trajectory export from the fold (`history export --atif`), billed costs
included; AGENTS.md generated/honored via the CLAUDE.md-import pattern.*
**Falsifiable exit**: *An exported Karvan-core trajectory validates against the ATIF schema; 22 runs
become shareable artifacts.*

Both are measured below. The validator is **Harbor's own model**, not one written here.

---

## 1. What shipped

| | |
|---|---|
| Verb | `conductor history export [<run>] --atif [-o PATH] [--all]` — `src/Conductor/Commands/HistoryExportCommand.cs`, reached by the argv rewrite in `src/Conductor/VerbRewrites.cs` |
| Exporter | `AtifExport` — `src/Conductor.Core/Interop/AtifExport.cs` |
| AGENTS.md | `AgentsFile` — `src/Conductor.Core/Interop/AgentsFile.cs`, wired into `conductor init` |
| Docs | `docs/cli.md` (`history export` row), `docs/operating.md` §2 |
| Tests | `KS8_2AtifExportTests` (7) + `KS8_2AgentsFileTests` (5) — 12, all green |

The mapping, stated once: **the agent is conductor, each step is one session it dispatched, and the
observation is what the engine fed back** — the gate battery's verdict, the checkpoints it confirmed,
the commits it found. System steps frame it: the brief, each stage entered, how it ended.

Two sources, deliberately. The **session rows are the spine**, because every catalogued run has them
including the v1..v10 databases whose logs predate half the event kinds; the **event log enriches**
(stage titles, the per-gate breakdown). `extra.event_log_steps` says which a reader is holding, so a
thin trajectory off an old database reads as thin rather than as a run that ran no gates.

**Billed dollars only.** Conductor has no price table by design, so ATIF's own
`cost = non_cached × rate + cached × rate + completion × rate` derivation is *not* applied —
`cost_usd` is what the provider charged. `prompt_tokens` does include `cached_tokens`, because that
is what the same formula requires and conductor stores the two side by side.

---

## 2. Exit half A — it validates, and not against a validator we wrote

Full run: `KS8.2-atif-validation.txt`. Excerpt of the document itself:
`KS8.2-karvan-core.atif.excerpt.json`.

The validator is **Harbor** — the Laude Institute / Terminal-Bench evaluation harness that defines
ATIF (`pip install harbor`, PyPI name `harbor`, requires-python ≥3.12) — installed into a throwaway
venv. Nothing was vendored and nothing was written here:

```
Trajectory.model_config extra = forbid
Step.model_config       extra = forbid
-> an unknown or misspelled field is a HARD REJECTION, not a dropped key.
```

That `extra="forbid"` is what makes the result worth anything: had a single field name been wrong,
validation would have failed rather than silently discarding it.

**The named run.** `conductor history export df9c4af8 --atif`:

```
schema_version    ATIF-v1.7
session_id        df9c4af8… (Karvan core - the engine knows what it did and what it cost)
steps             41  ( 32 agent / 9 system )
final_metrics     total_prompt_tokens=401,623,975  total_completion_tokens=2,289,567
                  total_cached_tokens=396,958,427  total_cost_usd=317.84  total_steps=41
first agent step  step_id=3  llm_call_count=184
  metrics         prompt=24,872,817 completion=95,366 cached=24,653,507 cost_usd=18.068611
  observation     3 results; first: "outcome: Advanced; gates: engine-fast:OK · face-fast:OK;
                                     checkpoints closed: K1.1, K1.2; commits: 3"
```

Nothing was dropped in the round trip: metrics, observations, per-step `extra` (session number,
stage, kind, attempt, resume count, commits, context high-water, closed checkpoints) and root `extra`
all parse into Harbor's models.

**The corpus.** `conductor history export --all --atif -o <dir>` wrote **30 trajectories**, and every
one validates:

```
VALIDATED 30/30; invalid: 0
corpus totals: 734 steps, $4598.75 billed
```

The checkpoint asked for 22; the catalogue has grown to 35 rows, 30 of which hold a readable run row.
The five skipped are the catalogued stores whose database is gone — reported as skipped by name, not
silently dropped.

## 3. Exit half B — AGENTS.md generated, and honoured

Transcript: `KS8.2-agents-md-init.txt`, driven through the fresh build against scratch repos under
the temp directory.

Claude Code still does not read `AGENTS.md` natively; it reads `CLAUDE.md`. So `conductor init` now
writes both — an `AGENTS.md` with what an agent cannot work out from the source (that this repo is
conductor-driven, what the session's own verbs are, that a claim goes through
`conductor task --done`), and a `CLAUDE.md` whose body is the `@AGENTS.md` import. One source of
truth, honoured by both families of agent, no second copy to drift.

**The only way this feature could be harmful is by clobbering the file that steers somebody's agent**,
so most of what is pinned is what it does *not* write:

| case | behaviour | proved by |
|---|---|---|
| neither file | both created, `CLAUDE.md` is the import | transcript §A |
| `AGENTS.md` already exists | *Kept* — byte-identical, not one line added | transcript §B, `MY OWN AGENT RULES` intact |
| `CLAUDE.md` already exists | **appended**, every original byte kept | transcript §B |
| already imports | *Kept*, untouched | transcript §C — identical md5 across a re-init |

## 4. Gates run

```
dotnet build Conductor.slnx -clp:ErrorsOnly -nodeReuse:false   → 0 errors, 0 warnings
dotnet test --filter KS8_|K7_2DocsVerbCoverage|SF7_1DocsMatchReality|B11_2|Init|KS3_1PlanNew
                                                               → 111 passed, 0 failed
```

Harbor validation is **evidence, not a gate**: it needs a python ≥3.12 environment with `harbor`
installed, which the battery does not have and should not grow a dependency on. What the C# tests pin
is everything validation cannot see — that the dollars are the billed ones, that `prompt_tokens`
includes the cache reads, that a gate lands under the session that ran it and not its neighbour, that
the reconciled status rides the artifact beside the stored one, and that a run with no event log
still exports its spine.

## 5. Two things this checkpoint found

**A ratchet, not a bug.** Adding a third argv rewrite to `Program.cs` pushed it under CA1505's
maintainability bar (MI 19 against 20). The rewrite moved to `src/Conductor/VerbRewrites.cs`; top-level
statements compile into one `Program` class, so that is where any further ones go.

**A trap worth the ledger entry.** Editing a `.cs` file with a text-mode script converts the working
tree copy from CRLF to LF. With `core.autocrlf=true` the blobs are LF either way, so **nothing shows
in `git diff`**, the build is clean, and exactly one test goes red:
`KS3_1PlanNewTests.InitStillWritesExactlyWhatItWroteBefore`, whose assertion separates the live
`advisor` block from the commented one *by the line ending of a raw string literal*. A C# raw string
literal takes its newlines from the source file's bytes, so a line-ending flip changes program output.
The working tree was re-normalised before the suite was re-run; the committed tree never carried it.

## 6. What KS8.2 does NOT claim

- No ATIF **import**. This is an export; reading someone else's trajectory into conductor is not in
  the checkpoint and would need its own decision about what a foreign run means here.
- No `subagent_trajectories`. A conductor session's own subagents are not recorded per-agent in the
  store, so the field is omitted rather than faked.
- **This repo's own `AGENTS.md` is still not imported.** `C:/code/conductor` has a 28KB `AGENTS.md`
  and no `CLAUDE.md`, so every session in this run reads none of it. Adding the import is one file and
  would put ~7k tokens into every remaining session's prompt prefix — a spend decision about a live
  run, which is why it is raised in the handoff rather than taken here.
