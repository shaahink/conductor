# KS7.2 — hooks as ground truth

*Session 7, 2026-08-18. Everything below was measured against the installed `claude 2.1.235` or
produced by the fresh build (`src/Conductor/bin/Debug/net10.0/conductor.exe`) driving a scratch rig.
Nothing here is read off a doc comment.*

**Landed:** `5b8d56e` (channel + promotion + bug #19 class + tests + replay corpus), and this
artifact's commit (ARCHITECTURE.md §3c + evidence).

---

## 0 · Flags verified against the installed CLI (trap 16)

`claude --version` → **2.1.235 (Claude Code)**.

| Surface | Verdict | Where checked |
|---|---|---|
| `--include-hook-events` | **exists**, requires `--output-format=stream-json` | `claude --help` |
| hook events that fire from a `--settings` file | `SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `Stop`, `SessionEnd` | live probe, 16 captured payloads |
| `PermissionRequest` | registers without error, **did not fire** (nothing needed permission under `acceptEdits`) | same probe |
| `--plugin-dir <path>` | **exists**, session-scoped, repeatable | `claude --help` + live probe |
| `--disable-slash-commands` | exists — "Disable all skills" | `claude --help` |

**`--include-hook-events` is not the tool-event channel, and this had to be checked rather than
assumed.** It emits the hook *lifecycle* only:

```
{"type":"system","subtype":"hook_started","hook_id":"87c8…","hook_name":"SessionStart:startup","hook_event":"SessionStart",…}
{"type":"system","subtype":"hook_response","hook_id":"87c8…","output":"","stdout":"","stderr":"","exit_code":0,"outcome":"success",…}
```

No `tool_input` anywhere in it. The tool payload only reaches the hook **command**, on stdin — which
is why this checkpoint extends the `hook-budget` verb rather than parsing a richer stream.

The PostToolUse stdin payload, keys as captured:

```
cwd, duration_ms, effort, hook_event_name, permission_mode, prompt_id,
session_id, tool_input, tool_name, tool_response, tool_use_id, transcript_path
```

`tool_input` is the **full, unclipped argument object** — the same shape as stream-json's
`tool_use.input`. PreToolUse is the same minus `duration_ms`/`tool_response`. That identity is the
reason the two digests can be made to match rather than merely to agree approximately: both go through
`ToolEventExtractor.Extract`, so a drift can only ever be a drift in delivery.

---

## 1 · The measurement that changed the design

> **PostToolUse does not fire for a tool call that was refused or failed.**

Measured twice, perfect correlation both times, in both directions.

**Probe** (`%TEMP%/ks72-probe`, one `claude -p` run, both channels captured side by side):

| | count |
|---|---|
| `tool_use` blocks in the stream | 7 |
| `PreToolUse` hooks | 7 |
| `PostToolUse` hooks | **5** |

The two missing are exactly the two Bash calls print mode refused. The session's own closing message
names them: *"Step 6: PowerShell command requires approval / Step 7: `./conductor task` command
requires approval"*.

**Rig run 1** (`%TEMP%/ks72-rig`, run `c5fe473d`, session #1, 12 tool calls): 10 PostToolUse. Reading
the raw stream's `tool_result` blocks, **every `ok` result had a PostToolUse and neither `is_error`
result did**:

```
ERR  Bash  conductor task --done R1.1 --evidence hello.txt  -> "Exit code 127 … conductor: command not found"
ERR  Bash  where conductor                                  -> "This command requires approval"
```

Note the first one: it *executed* (bash returned 127) and still produced no PostToolUse. The rule is
about the tool result, not about whether a process ran.

**Consequence, and the design it forced.** A PostToolUse-only channel counts SUCCESSES; the transcript
it replaces counts ATTEMPTS. They can never agree — and a session whose forty test commands all failed
would have reported none. So:

- **PreToolUse writes the call.** It fires for every call the model makes, including the ones the
  posture then refuses, so it is the transcript's own population.
- **PostToolUse appends an outcome line**, merged back by `tool_use_id` — the same string in the
  stream and in both hook events (verified 5/5 in the probe, and pinned by a test).
- What that buys beyond parity is `SessionDigest.FailedCalls`, the one number the transcript could
  never supply.

Cost of the decision, stated plainly: **two hook processes per tool call instead of one.**

---

## 2 · The acceptance — hook-derived matches transcript-derived

### 2a · On the replay corpus (`tests/Conductor.Tests/testdata/ks72/`)

The corpus is **both channels of one live run**, raw and unedited: `probe-stream.jsonl` (the whole
`--output-format stream-json` output) and `probe-hooks.jsonl` (the raw stdin of every hook that
fired). A hand-written corpus would only prove that two functions in the same file agree with each
other.

`KS7_2HookGroundTruthTests.HookDerivedDigest_MatchesTranscriptDerived_OnTheReplayCorpus` builds one
digest from the stream's `tool_use` blocks and the other by pushing every hook line through the **real**
append and read, then asserts equality on `ToolCalls`, `Mix`, `FilesTouched`, `Claims`,
`BackgroundJobs` and `Commands`. The corpus is itself pinned (7 calls over Bash/Edit/Grep/Read/Write)
so a later edit cannot shrink it into something the test passes trivially.

### 2b · Live, on a real session — which is the stronger proof

Rig run 2 (`%TEMP%/ks72-rig`, session #2, fresh build):

```
[23:16:35] session #2 digest: 8 tool calls · 6 tools · 1 file (1 write) · 1 claim · 1 failed/refused · via hook
```

Transcript tool events recorded for that same session: **8**. Hook call lines: **8**. They agree, so
`PromoteHookDigest`'s divergence line stayed silent — which is what agreement looks like from the log.
The hook file is attached as [`ks7-2-rig-hook-tools.jsonl`](ks7-2-rig-hook-tools.jsonl): 8 call lines,
7 outcome lines, and the one call with no outcome is the failure.

```
  ok   Bash   {"command": "echo probe-one", …}
  ok   Write  {"path": "…/hello.txt", "bytes": "5", "lines": "1"}
  ok   Read   {"path": "…/hello.txt"}
  ok   Grep   {"pattern": "hooks", "in": "…", "output_mode": "files_with_matches"}
  ok   Bash   {"command": "powershell -NoProfile -Command \"dotnet --version\"", …}
  FAIL Bash   {"command": "conductor task --done R1.1 --evidence hello.txt", …}
  ok   ToolSearch                {"query": "select:mcp__conductor-tasks__task_update", …}
  ok   mcp__conductor-tasks__task_update  {"taskId": "R1.1", "status": "done"}
```

---

## 3 · Fallback — a hook-less agent still works

`PromoteHookDigest` promotes **only** when the file exists and has lines. Absent and empty are
deliberately the same answer: an opencode session, a `--bare` claude and a session that made no calls
all mean "the transcript is the only source there is", and promoting an empty digest over it would
report a session that did nothing. Four tests cover it: no file, empty file, a torn tail (costs that
line and nothing else), and an outcome line arriving *before* its call (parallel tool calls interleave,
so the merge is by id, not by file order).

The digest now records `source` (`hook` / `transcript`) and it round-trips through `run.db`. It is
stored rather than inferred on purpose — "hooks are the primary source" is a claim a reader must be
able to **check on any given session**, because a run where the hook silently never fired would
otherwise look exactly like one where it did.

---

## 4 · The bug #19 class dies

`SessionDigest.Claims` read exactly one shape: the MCP `task_update` call's `taskId`/`status` pair.
Sessions claim with `conductor task --done <id>` through the shell — because that is what their own
prompt tells them to do, and the MCP tools arrive deferred in some harnesses. So the digest said
**0 claims** for sessions that had claimed, and that number was read as evidence they delivered
nothing.

`SessionDigest.TryReadCliClaim` now reads a board move out of a shell command line, narrowly on
purpose: the `task` token must be present, the flag must be one that moves a card
(`--done`/`--in-progress`/`--todo`/`--blocked`/`--skipped`, mapped onto the same status words the MCP
path writes), and the id must be the very next token and not itself a flag. `--amend` is excluded —
it attaches a note and moves nothing. A fabricated claim is worse than a missed one, so
`grep -n "task --done" prompt.md` and friends are rejected by leading verb; six positive and six
negative cases are pinned as theories.

Live, in the rig: **`1 claim`** in a session that claimed through the shell.

**One thing this made visible and did not fix — [bug #52](../../..).** The rig's CLI claim *failed*
(exit 127) and the session then claimed successfully via MCP; the digest showed 1 claim only because
the two dedupe to one entry. A session whose only claim attempt failed would show a claim it never
made. Filtering `Claims` by outcome is now possible — but it would make the hook-derived digest differ
from the transcript-derived one on the corpus, and that equivalence is this checkpoint's acceptance.
Filed rather than smuggled in.

---

## 5 · Skills vs `promptExtra` — DECIDED

Measured first:

| | measured |
|---|---|
| project `.claude/skills/…` in print mode with `--setting-sources project` | resolves; agent listed `karvan-traps` |
| `--plugin-dir <dir>` with `--setting-sources` **empty** | resolves as `karvan:karvan-traps`; **no writes into the target repo** |
| what is resident | the one-line description only (~25 tokens) — the probe quoted the description and confirmed it could **not** see the body |
| this plan's `promptExtra` | 6,651 chars ≈ **1,662 tokens**, of a 17,871-char composed prompt — **37%** |

**Decision: `promptExtra` stays. Its rails are not converted to skills.**

The `promptExtra` content is a trap list, and its whole value is being read *before* the first edit by
a session that does not yet know it needs it. A body that loads only when the agent chooses to invoke
it is a rail conditional on curiosity, which is not a rail. The saving is small against that risk: the
1.66k resident tokens sit in the **cached** prefix, and this era pays cache-read rates for them —
comfortably under a dollar a session, against the cost of one session that skips a trap.

**Decision, second half: skills via `--plugin-dir` are the right vehicle for the *reference* half.**
Material a session reads only once it has decided to do a specific thing — the golden rebaseline
recipe, the face style rules, the plan-editor caveats. The criterion, written down so it is not
re-argued:

> **If not reading it causes harm the session cannot detect, it is a rail and stays resident. If it
> only helps once the decision is already made, it is a reference and can move to a skill.**

The seam is named and the objection that would have blocked it is retired: `--plugin-dir` pointed at
the run's state dir needs no `.claude/` in the target repo and no widening of the
`--setting-sources` surface KS7.1 narrowed. Spending it belongs to **KS7.5**.

---

## 6 · Gates

Scoped battery, `dotnet test Conductor.slnx --filter` over the classes touched
(`KS7_2`, `Architecture`, `SF7_1Docs`, `BudgetRail`, `SessionDigest`, `Transcript`, `Ratchet`):

```
Passed!  -  Failed: 0, Passed: 96, Skipped: 0, Total: 96
```

Two things that had to be fixed rather than suppressed along the way:

- **`MA0045` is an error in `Conductor.Core` and the pragma ceiling only goes down** (`maxPragmas: 38`,
  `tools/gates/ratchet-baseline.json`). The cross-process append was made genuinely async — and
  `HookBudgetCommand` became an `AsyncCommand` — instead of buying a suppression.
- **The soft-break notice is now guarded to PostToolUse.** The same verb runs on PreToolUse now, and
  emitting a block whose `hookEventName` does not match the event that ran it is how a hook goes from
  silent-and-working to rejected-and-silent — the exact failure B13.3 was written to end. No payload
  at all still counts as PostToolUse, which is the shape every pre-KS7.2 invocation has.

## 7 · One trap this cost a rig run

`dotnet run --project src/Conductor` **while** a `dotnet build Conductor.slnx` is in flight fails with
a wall of analyzer errors from `Conductor.Planning` that reproduce in neither build alone — two
MSBuild instances over the same `obj/`. Drive a live rig with the built exe directly
(`src/Conductor/bin/Debug/net10.0/conductor.exe`), which is still your build and not the PATH engine,
and cannot race a rebuild.
