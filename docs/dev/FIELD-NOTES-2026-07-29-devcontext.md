# Field notes — driving a real plan with conductor (2026-07-29)

Findings from setting up and running `DevContext graph-v2 — autonomous remainder` on
`C:/code/DevContext2` with master `5cf77f1`, engine + Face freshly installed from source. Written by
the operator agent as the run proceeded. Every entry has evidence; anything unverified says so.

Ordered by how much they cost, not by severity label.

---

## 1. Live cost/token counters read zero for the whole session, then jump at the verdict

**Severity:** low-medium — a display/latency issue, NOT a data-loss bug.

> **Corrected entry.** This was first written as "aggregates never accumulate", which is **wrong**. The
> aggregates work. I had sampled `/state` mid-session and again seconds after the session exit line,
> both of which are legitimately before the verdict persists. Recording the correction because the
> original claim would have sent someone hunting a bug that does not exist.

**What is actually true.** During a session, and through gate verification, `GET /state` reports:
```
"totalCostUsd":0  "tokensInput":0  "tokensOutput":0
```
even at `status:"VerifyingGates"`, seconds after the engine logged
`session #1 exited (code 0, 56m, $12.67)`. The totals land once the verdict completes, and they are
correct and durable — after a restart the engine logged
`restored budget: $29.17 agent / $0.05 overhead / 562k tokens (from prior process)` and `/state` then
read `totalCostUsd: 29.17`, `tokensInput: 402746`, `tokensOutput: 159252`.

**Impact.** A session here runs ~55 minutes. For that entire window every live surface — the Face's
Home, the cost line, any dashboard polling `/state` — shows `$0.00` and `0` tokens, then jumps by ~$15
at once. Someone watching a long run cannot see spend accruing, which is exactly when they would want
to. It also makes a genuine "cost is not being recorded" bug indistinguishable from normal operation
(as this entry demonstrates).

**Suggested fix.** Fold the running session's in-flight usage into the `/state` aggregate as
`TokenDelta` events arrive, or expose it as a separate `currentSessionCostUsd` so live surfaces have
something truthful to show mid-session. `tokensReasoning` stayed `0` throughout and may be genuinely
unpopulated for this provider — not investigated.

---

## 2. `agent.model` is silently dropped unless `{model}` appears in the arg template

**Severity:** high — the run does the wrong thing, confidently, with no diagnostic.

**Evidence.** `AgentSession.ResolveArgs` substitutes `{model}` only where the token appears in
`args`/`resumeArgs`. A plan that sets `agent.model` but whose args have no `{model}` runs the agent CLI's
own default model. `conductor journey` then prints the configured model in its Model column, so the
itinerary *agrees with the plan file* while the spawned process does something else.

Caught here only because the session stream was inspected directly (`"model":"claude-fable-5"` while the
plan said Opus).

**Impact.** An expensive/cheap model choice can be silently inverted for a whole run. This is the single
most dangerous config trap found.

**Suggested fix.** `doctor`-level check: if `agent.model` (or any stage's `agent.model`) is non-empty and
no `{model}` token exists in the corresponding template, emit `fail` — not `warn`. Cheap to detect,
impossible to notice otherwise.

---

## 3. The default `advisor` configuration cannot work

**Severity:** high — it fails exactly when it is needed.

**Evidence.** `AdvisorConfig` defaults are `Command = "claude"` and `Args = new()` (empty).
`Advisor.cs:60` does `a.Args.Select(...)`, so the advisor launches bare `claude` with **no prompt and no
arguments** — the interactive REPL, with no TTY. It hangs until `TimeoutMinutes` (6) and returns null.
The failure is swallowed (`return null` on timeout), so nothing surfaces.

`docs/plan-config.md` describes the default as "cheap model: deepseek via opencode by default", which
does not match the code.

**Impact.** The advisor is consulted when a stage exhausts its attempt budget — i.e. unattended, at the
worst moment. Every consult costs a 6-minute stall and yields nothing. The loop then falls back to its
deterministic path, so it degrades safely, but the feature is dead by default.

**Suggested fix.** Ship working default args (`["-p","{prompt}","--output-format","json"]` with
`Output = "json"`), or refuse to enable the advisor when `Args` is empty and say so in `doctor`. Also
correct `plan-config.md`.

---

## 4. Unknown `RunIf` / `SkipIf` tokens evaluate to TRUE

**Severity:** medium — a typo inverts control flow silently.

**Evidence.** `WorkflowEngine.EvaluateCondition` ends with `_ => true, // unknown expression → treat as
true (permissive)`. So `runIf: "!gatesgreen"` (wrong case) or `runIf: "gates.green"` (wrong shape) means
"always run" rather than an error.

**Impact.** A custom workflow can silently run a step it should skip. Given workflows are hand-authored
JSON with no schema hints, this is easy to hit.

**Suggested fix.** Validate condition tokens at plan load against the known vocabulary and fail loudly.
Permissive-at-runtime is fine; permissive-at-authoring-time is not.

---

## 5. Sessions still block the foreground on long commands, and the stall message doesn't say so

**Severity:** low-medium — costs a kill + resume cycle.

**Evidence.** Session #1 produced a genuine 15m09s gap with zero parsed events (transcript timestamps
`00:38:39` → `00:53:48` UTC), which correctly triggered
`stall: all signals quiet for 15m — 3m soft-kill grace window started`.
**The detector was right** — this was investigated on the suspicion of a false positive and it was not
one. The agent had blocked the foreground on a long command instead of using `conductor bg`, despite the
injected tools block instructing it to.

**Impact.** A healthy session looks dead. With `stallPatternTermination`, two of these in a row park an
otherwise fine run.

**Suggested fix.** Two cheap improvements: (a) make the stall log line name the likely cause and the
remedy (`no output for 15m — if a long command is running, it should be under 'conductor bg'`), and
(b) consider treating a live child process of the agent as a liveness signal even when it emits nothing,
which is what `conductor bg` already does explicitly.

---

## 6. `conductor plan set` rewrites the plan file and strips `//` comments

**Severity:** low, but lossy and irreversible.

**Evidence.** The plan was authored with extensive `//` annotations documenting the traps in this
document. After a single `conductor plan set limits.maxRunCostUsd 100`, the file came back re-serialised
(`planVersion: 2`), fully expanded, and with **every comment gone**.

**Impact.** The loader explicitly tolerates comments and `conductor init` writes them, so users are
invited to annotate a file that the first live edit silently strips. Annotation is exactly where
per-project knowledge lives.

**Suggested fix.** Either preserve comments through the round-trip, or warn on the first `plan set`
("this rewrite will drop N comment lines"). At minimum, document it next to the "comments are allowed"
line in `plan-config.md`.

---

## 7. `/telegram/status` reports `configured` for a configuration that cannot deliver

**Severity:** low — `doctor` already handles this well; the HTTP surface does not.

**Evidence.** `TelegramService` returns early whenever `AllowedChatIds.Count == 0` (lines 173, 181, 308),
so with an empty list every push is dropped and two-way is off. `doctor` gets this exactly right —
`"token present but no allowedChatIds — bot is push-only to nobody"` — but `GET /telegram/status`
exposes `configured`/`hasToken`/`allowedChatIds` as independent booleans with no derived verdict, and the
Face reads that.

Also worth documenting: conductor never learns the chat id from an incoming message, so the owner must
fetch it via `getUpdates` out-of-band. That is a reasonable security posture but it is undocumented, and
it is the step everyone will get stuck on.

**Suggested fix.** Add a derived `willDeliver` (or `verdict`) field to `/telegram/status` carrying
doctor's sentence. Document the `getUpdates` step in the Telegram setup flow — or offer a "learn my chat
id from the next message" button in the Face's Telegram tab, gated on an explicit click.

---

## 8. The claim path is easy for a capable agent to miss

**Severity:** medium — it is the difference between a confirmed checkpoint and a wasted session.

**Evidence.** Session #1 did the work, wrote a full evidence artifact, and wrote `G1.1 CLAIMED` into the
tracker handoff at 02:13 — but did not call `task_update` until 02:21:54. For eight minutes the run
looked finished and the board correctly showed `TODO`. Conductor's design caught it exactly as intended;
the point is how natural the mistake is.

Contributing factor: the conductor MCP tools arrive **deferred** in the Claude Code harness — the agent
had to `ToolSearch` for `mcp__conductor-tasks__task_update` before it could call it, adding a step to the
one mandatory channel.

**Suggested fix.** In the injected tools block, state that the tool may need loading first in harnesses
with deferred tools, and give the CLI fallback (`conductor task --done`) on the same line. Consider a
session-end nudge: if a session exits having produced commits and evidence but zero claims, log a
specific hint rather than a generic `Progress`.

---

## 9. Nothing instructs the agent to mark work in progress

**Severity:** low — pure observability.

**Evidence.** The built-in session template mentions `conductor task --in-progress <id>` only inside the
tools block, not as a step. Result: the Kanban board sat entirely in TODO for a 56-minute session that
was actively delivering, and the owner's first question on opening the Face was "why is everything in
todo and nothing in progress?"

**Suggested fix.** Make it step 1 of the built-in session template, before any editing. One line, and it
is the difference between a legible board and a wall of TODO.

---

## 10. Agent events are stored as truncated strings, so nothing downstream can render them well

**Severity:** high for usability — this is the difference between a log you read and a log you skim past.

**Evidence.** Every agent event reaches `.conductor/transcript.jsonl` as a **truncated concatenation of
tool name + raw JSON args**, capped around 150 characters, cut mid-string:
```
Edit {"file_path":"...\\DevContext.Core\\\\Rendering\\\\LibrarySurfaceRenderer.cs","old_string":"            if (gro<CUT>
```
`len == 156`, ending mid-token. The Face, the timeline and the report all read this, so they can only
ever show a wall of escaped JSON fragments. A `Write` shows the file's *contents* rather than its path;
a `Bash` shows the command buried behind `{"command":"`; backslashes are double-escaped; quotes arrive
as `\u0022`.

**The data is lossy at capture, not at render.** I tried to build a readable digest from
`transcript.jsonl` and could not — `file_path` and `command` are unrecoverable because the JSON is cut.
The full structured call survives only in `logs/session-NNN.jsonl`.

**Impact.** The primary human-facing surface of a run is unreadable, and no consumer can fix it, because
the structure needed to summarise was discarded before storage.

**Suggested fix, in two parts.**

1. *Capture structure, not a string.* Store the tool name and a small set of extracted fields per call
   (`path`, `command`, `taskId`, `purpose`, byte counts) instead of a truncated blob. Truncate the
   *values*, never the JSON.
2. *Render a one-liner per call.* With structure available, the same events read as:
   `Edit LibrarySurfaceRenderer.cs (+12/-3)` · `Bash dotnet build src/DevContext.Mcp` ·
   `conductor:task_update G1.1 -> done` · `bg_start "G1.1 full solution build"`.

**And then the extra value — a per-session digest.** Built from the raw stream for session #1 of this
run, which is what the Face's Sessions tab and `REPORT.md` should show instead of (or above) the event
list:

```
TOOL CALLS: 154  ·  distinct tools: 13
MIX: Bash 97, Edit 16, Read 11, bg_start 7, conductor_note 5, ToolSearch 4, bg_status 4, bg_stop 3

FILES TOUCHED (17 edits over 10 files):
    tests/DevContext.Core.Tests/LibrarySurfaceRendererTests.cs   3x
    docs/product/mcp-reference.md                                3x
    src/DevContext.Core/Rendering/LibrarySurfaceRenderer.cs      1x
    src/DevContext.Mcp/DevContextTools.cs                        1x
    ...

CLAIMS: [('G1.1', 'done')]

BACKGROUND JOBS (7):
   - G1.1 full solution build (0w/0e gate)
   - G1.1 evidence: real MCP map() call on FluentValidation (library pole)
   - G1.1 evidence: real MCP map() on GitVersion (multi-solution pole)
   - MCP QA harness re-run (warm snapshot) to test the cold-snapshot hypothesis

BUILD / TEST / EVIDENCE COMMANDS (12 of 97 shell calls):
    dotnet build src/DevContext.Mcp -clp:ErrorsOnly
    dotnet test tests/DevContext.Core.Tests --no-build --filter "FullyQualifiedName~MapRenderer..."
    UPDATE_GOLDENS=1 dotnet test ...
    powershell -File eval/contract-sweep.ps1
    powershell -File scripts/loom-guards.ps1
```

Three things a reader learns in five seconds that the raw log never tells them: **63% of the session was
shell calls**, the `bg_start` *purposes* are already a written narrative of the session's reasoning (they
are the best text in the whole stream and are currently buried), and the session regenerated goldens
(`UPDATE_GOLDENS=1`) — which a reviewer would want to know without hunting.

`bg_start.purpose` deserves special mention: agents write genuinely descriptive purposes there, and
surfacing just that field as a session storyline would be nearly free.

---

## 11. An agent can write outside the repo, where verification cannot see it

**Severity:** medium — a correctness hole in the trust model, not a bug.

**Evidence.** Session #1's digest shows two edits outside `repo`:
```
C:/Users/shahi/.claude/projects/C--code-DevContext2/memory/project-ui-feature-redesign.md   1x
C:/Users/shahi/.claude/projects/C--code-DevContext2/memory/MEMORY.md                        1x
```
These were legitimate — the project's own session-close protocol asks for a memory update, and the agent
read that instruction from the repo's `AGENTS.md`. But conductor's entire verification is git-scoped
(`Git.IsDirty`, new-commit diff, tracker diff against `repo`), so **any write outside `plan.repo` is
invisible to the verdict**: not counted as progress, not flagged as an unexpected mutation, not
reviewable afterwards.

**Impact.** Today it is benign. It is also the blind spot where a misbehaving session would be least
visible — and a run that is meant to be left alone overnight is exactly where you want to know about
out-of-tree writes.

**Suggested fix.** Cheap version: extract `file_path` from tool calls (which fix 10 enables anyway) and
report any path outside `plan.repo` in the session verdict — `note: 2 file(s) written outside the repo`.
No policy needed, just visibility. A stricter mode could make it a `warn` or require an allow-list.

---

## 12. The gate battery starts ~1s after the agent exits, and can fail on the session's own teardown

**Severity:** high — a false RED costs a whole paid fix session.

**Evidence.** Session #2 exited at `03:24:59`; conductor started the battery at `03:25:00`.
```
[03:24:59] session #2 exited (code 0, 55m, $16.50)
[03:25:00] gate fast-engine: powershell ... eval\gates.ps1 -SkipEval -Scope engine
[03:25:59] gate fast-engine: FAIL (exit 1) in 59s
[03:27:43] verdict inputs: gates RED · commits 3 · newly DONE [G1.2] · dirty YES
[03:27:43] session #2 GatesRed — queuing fix session (attempt 1/6)
```
Re-running **the identical command** minutes later, with no code change, gives `GATE: PASS` (exit 0).

The durations are a tell, but a softer one than it first appeared: the failing run died at **59s** while
session #1's passing run of the same scope took **249s**. It failed early — in the build/test phase —
not at a real assertion.

**Correction from a later sample.** Session #4's `fast-engine` **passed in 98s**. So the healthy range for
this gate is at least 98–249s, not "about 250s", and a bare duration threshold would have to sit near 60s
to avoid false accusations — uncomfortably close to the 59s failure itself. Passing runs vary this much
because the build is incremental: how much a session touched decides how much gets rebuilt. Do not treat
"faster than last time" as proof of an infra failure on its own; pair it with **where** it died (build/
restore phase vs. an assertion) which is the part that actually discriminates. Session #2 had spawned seven `conductor bg` children (servers, test hosts, an MCP evidence
driver); at least one was still releasing file locks when the battery's build started one second later.
This repo's own battery has a `Step 0: Clear orphaned build-locking processes` precisely because that
class of failure is known here, but Step 0 cannot help with a process that is still mid-shutdown.

**Impact.** The run scored `GatesRed`, queued a **fix session for a defect that does not exist**, and
would have spent ~$15 and an attempt against the stage budget "fixing" a green tree. This is the most
expensive failure mode observed so far, and it is invisible unless someone re-runs the gate by hand.

**Suggested fix, cheapest first.**
1. **Settle before verifying.** Wait for the agent's process tree — including `conductor bg` children,
   which conductor already tracks by pid — to actually exit before starting the battery. Conductor knows
   about these processes; it should not race them.
2. **Retry once, unconditionally, before declaring `GatesRed`.** This was originally written as "retry
   when the gate fails materially faster than its recent passing runs", but the 98s pass above kills that
   trigger — 59s is not *materially* faster than 98s, so the heuristic would not have fired on the very
   case it was designed for. Retrying any required-gate failure once is simpler and strictly better: it
   costs one gate duration on a genuine red (which is about to cost a whole fix session anyway) and saves
   a session on a false one. Declare RED only if the second run agrees.
3. Failing both, surface it: when a gate fails, log its duration next to the previous passing duration
   so a human reading the log sees `FAIL in 59s (last PASS 249s)` and knows to suspect the environment.

**Related, and it makes this worse:** the run had `pauseAfterSession` queued, so it parked immediately
after the false RED. Without that pause it would have gone straight into the pointless fix session
unattended.

---

## 13. Telegram reports healthy, passes its own test, and still delivers nothing

**Severity:** high — every status surface says "working"; the feature is entirely dead.

**Evidence.** After a restart with the token saved and a chat id configured:
```
GET /telegram/status
{"configured":true,"started":false,"hasToken":true,"allowedChatIds":["99205495"],"enableTwoWay":true}

POST /telegram/test
{"ok":true,"botUsername":"conductor_app_bot"}      <- a real push arrived on the phone
```
So the config is complete and the send path provably works. But `started` is `false`, and
`grep -ci telegram .conductor/conductor.log` returns **0** — `StartAsync`'s
`"Telegram bot started (poll interval {Interval}s)"` never logged, in either the text or structured log.
`StartAsync` never ran, or returned at its `if (!IsConfigured)` guard.

**Why this is worse than it looks.** `_started` gates *both* background loops, and every real
notification path checks it:
```csharp
// TelegramService.cs:173
if (!_started || _cfg?.AllowedChatIds is not { Count: > 0 } ids) return;
```
With `_started == false`: the poll loop never runs, so **two-way control from the phone is dead**
(`/status`, `/pause`, `/resume`, `/approve`), and the send loop never runs, so **every queued
notification is silently dropped** — including the NeedsHuman park and run-completion pushes, which are
the entire point of the feature for someone who is away.

`TestConnectionAsync` does *not* exercise this path — it calls `SendAsync` directly, bypassing the
queue. So **the built-in test passes while the feature is non-functional**, which is the most
misleading possible combination: a user configures Telegram, presses Test, gets a message on their
phone, walks away, and never hears from the run again.

**Impact.** Silent and total for the AFK use case that justifies the integration.

**Suggested fix.**
1. Find why the hosted service's `StartAsync` doesn't run (or exits early) on the `run --paused` path —
   registration is `AddSingleton<IHostedService>(sp => sp.GetRequiredService<TelegramService>())` in
   `ConductorHost`, so it should start with the host.
2. Make the guard's failure *loud*: log at warning when `StartAsync` returns early on `!IsConfigured`,
   naming which half was missing. Right now the single most important lifecycle event in this service
   produces no log line at all in either outcome.
3. Have `/telegram/status` derive a `willDeliver` from `started && hasToken && allowedChatIds.Count > 0`
   rather than exposing independent booleans, and have `POST /telegram/test` route through the real
   send queue (or report loudly that it bypassed it).

---

## 14. Status transitions each land their own commit, and the squash meant to clean them up never runs

**Severity:** medium — history fills with commits that carry no work, and conductor's own remedy for it
is silently disabled by a condition the run has already decided to tolerate.

**Evidence.** Three commits in eight minutes, all authored as the repo owner, all touching only
`.conductor/REPORT.md`, all with the same subject stem:
```
ccd8a41  03:27:44  chore(conductor): s2 G1 GatesRed — Idle      | REPORT.md | 48 +, 27 -
1a7a219  03:27:48  chore(conductor): s2 G1 GatesRed — Paused    | REPORT.md |  7 +,  6 -
e5bdd89  03:36:01  chore(conductor): s2 G1 GatesRed — Aborted   | REPORT.md |  3 +,  3 -
```
`ccd8a41` and `1a7a219` are **four seconds apart**. The second and third change three to seven lines of
a status block — the engine's own view of itself, not the project's work.

**Impact.** `git log --oneline` on a delivery branch is one of the first things a reviewer reads, and
here it is more than half orchestrator bookkeeping. Three near-identical subjects also make the real
work commit (`7c6eb5e G1.2: a library symbol gets a pack…`) harder to find, and a later `git bisect` or
`log --stat` sweep has to step over commits that provably cannot affect behaviour. It also inflates the
`commits N` count that feeds `verdict inputs` — the verdict for session #2 read `commits 3`, of which
one was the agent's work and the rest were conductor's own.

**The cleanup exists and it silently does not run.** This entry originally proposed "coalesce these
commits" as a fix. Conductor already implements exactly that — and it failed, once per stage, in one line:
```
[06:11:38] P4 squash: collapsing chore(conductor): commits for stage G1 since 65049a2
[06:11:40] P4 squash: git rebase returned non-zero for stage G1 — history unchanged
```
**Root cause, reproduced.** `git rebase` refuses to start when a *tracked* file has unstaged changes. At
06:11:40 two did — `GRAPH-V2-START.md` (the tracker conductor itself has the agent update) and
`eval-results/2026-07-29/mcp-qa.md`. Minimal repro, same rebase shape:
```
$ git status --porcelain
 M f.txt
$ git rebase --onto HEAD~2 HEAD~2 HEAD
error: cannot rebase: You have unstaged changes.
error: Please commit or stash them.        # exit 1
```
So stage G1 ended with **7 of its 19 commits** being `chore(conductor):` bookkeeping, two of them
(`b3ffa0c`, `3490ab0`) carrying byte-identical subjects.

**Two subsystems disagree about how much `dirty` matters.** The verdict treats a dirty tree as *advisory* —
it logs a note and scores the session green anyway. P4 squash treats it as *fatal*, because git does.
Nothing reconciles them, so a condition the run has decided to tolerate can disable the run's own history
hygiene.

**And the success message is not evidence the squash worked.** At the next stage boundary it reported the
happy path:
```
[07:23:03] P4 squash: collapsing chore(conductor): commits for stage G2 since b3ffa0c
[07:23:03] P4 squash: stage G2 complete — chore(conductor): commits squashed
```
The tree was **just as dirty** (`M GRAPH-V2-START.md`, `M eval-results/2026-07-29/mcp-qa.md`), so this was
not "dirt cleared, feature now works". The reflog shows **no rebase happened at all** — every entry across
the window is a plain `commit:`, with no `rebase (start)` / `rebase (finish)` anywhere:
```
046fe63 HEAD@{07:23:04} commit: chore(conductor): s6 G2 Advanced — Idle
04d14a7 HEAD@{07:07:35} commit: chore(conductor): s6 G2 Advanced — Idle
efe70fb HEAD@{07:03:00} commit: G2 handoff: stage complete, next session opens G3.1
```
At 07:23:03 HEAD was `04d14a7`, so the range `b3ffa0c..HEAD` held exactly **one** `chore(conductor):`
commit — the tip. There was nothing to collapse, and "commits squashed" was reported for a no-op.

**Then the punchline, one second later.** At `07:23:04` conductor committed `046fe63` —
`chore(conductor): s6 G2 Advanced — Idle`, a duplicate of the commit it had just finished "cleaning up".
**The squash runs before conductor's own final state write**, so even a squash that did collapse something
is immediately re-polluted by the next transition commit. Stage G2 therefore ended with two identical
chore commits despite the cleanup reporting success.

**Net:** across two stage boundaries the squash has never removed a single commit — once by failing on a
dirty tree, once by running too early against nothing and then being undone a second later. Both times the
log read as though history hygiene was handled.

**Suggested fix, cheapest first.**
1. **Move the squash after the final state write**, or have the stage-transition commit amend into the
   squashed commit instead of following it. Collapsing history one second before appending to it cannot
   work, and this ordering bug is independent of the dirty-tree one — fixing only the rebase would still
   leave a trailing duplicate every stage.
2. **Stash around the squash.** `git stash push --include-untracked` → rebase → `git stash pop`, or do the
   squash in a temporary worktree/index so the user's dirt is irrelevant.
3. **Report what actually happened.** "commits squashed" was logged for a no-op, and "returned non-zero —
   history unchanged" was logged for a failure; neither says how many commits were removed. Log the count
   (`squashed 4 commits into 1`, or `nothing to squash`) and the git stderr on failure. A success message
   that is emitted whether or not anything happened is worse than no message, because it stops anyone
   looking.
3. **Reconcile the two views of `dirty`.** If dirt is advisory for the verdict, it must not be fatal for
   the squash; if it is fatal anywhere, the verdict should say so rather than filing it under a note.
4. Independently, **don't commit `Idle` / `Paused` / `Aborted` at all** — process state already lives in
   `run.db` and `conductor.log`, and `REPORT.md` can be regenerated. That removes the need to squash most
   of these commits in the first place.
5. **Exclude conductor's own commits from the verdict's `commits N`**, so the count means "commits of
   work" — session #2's verdict read `commits 3`, of which one was the agent's.

---

## 15. An injected correction is rendered *after* the stale evidence it corrects

**Severity:** low-medium — it did not bite in the observed case, but the prompt ships a self-contradiction
and relies on model judgement to resolve it.

**Evidence.** After the false RED of entry 12, the watcher verified green by hand and injected a
correction before resuming. In the resulting `session-003.prompt.md` (132 lines):

- **line 9** — the fix template's own framing: *"Conductor ran the gate battery independently after
  session #2 and it came back RED:"*, followed by the captured `fast-engine` failure output and the
  standing instruction *"Do, in order: … 2. Reproduce each failure above."*
- **line 122** — `📋 QUEUED INSTRUCTIONS (human-injected, consume in order):` … *"There is NO defect to
  reproduce and NOTHING to fix. Do not spend a single tool call re-running or investigating that gate."*

So the prompt tells the agent to reproduce a failure 113 lines before it tells it the failure is not
real, and the template's instruction is the one framed as the session's purpose.

**What actually happened — the good case.** The agent read the whole prompt before acting and obeyed the
injection: `AGENTS.md` → ToolSearch for the conductor tools → `PLAN.md` → `ledger_list` → tracker → the
R4 spec → `task_update G1.3 in_progress` → straight into `DevContextTools.cs`. **Zero** tool calls went to
the phantom gate. Worth stating plainly, because it means this is a latent hazard, not an observed loss.

**Impact.** The outcome depends on the agent reading to the end of a 132-line prompt before its first
action, and on it weighing a trailing note above the stated purpose of its session. A cheaper or more
eager model, or a longer prompt, resolves this the other way and spends a session's opening minutes —
plus a ~250s gate re-run — disproving something the orchestrator already knew was false.

**Suggested fix, cheapest first.**
1. **Render injections at the top**, immediately after the role line, not at the bottom. They are the
   freshest and most authoritative input in the prompt; ordering should say so.
2. **Let an injection suppress the stale block.** If a correction is queued for a fix session, either
   drop the `{gateFailures}` block or stamp it *"SUPERSEDED — see queued instructions"* inline, so the
   two never stand as peers.
3. Longer term, give the watcher a first-class verb for this — `conductor gate --accept` or
   `clear-red` — so correcting a false verdict is a state change on the run rather than prose the next
   agent has to be trusted to obey.

---

## 16. `conductor run` has no detached mode, so a multi-hour run is hostage to its launching shell

**Severity:** high — an unrelated action in the launching terminal kills the engine *and* the paid agent
session mid-flight.

**Evidence.** The run was launched from an agent harness as a backgrounded foreground command. When that
harness task was later stopped — a routine cleanup, unrelated to conductor — the engine died with it:

```
04:04:17  session #3 start — Fix G1 attempt 2/6      (engine pid 32476)
04:09     session-003.jsonl stops growing
~04:10    harness task "Relaunch conductor paused with telegram token" killed
          → pid 32476 gone, GET /state on the control plane no longer answers,
            the spawned `claude` agent process is gone with it
```

Nothing in conductor's own model went wrong; the engine is simply a foreground process, and `run` offers
`--headless` (don't draw a TUI) but nothing that means *don't die with my parent*. A run budgeted at
`maxRunCostUsd = 300` — a many-hour, many-session job — inherits the lifetime of whatever terminal, SSH
session, or agent task happened to type the command.

**Impact here was small only by luck**: session #3 was still in its read phase, so no source edits were
lost. Had it died ten minutes later, mid-edit, the next session would have had to adopt a half-applied
change. The paid session time (~5 min of a $15-class session) was spent twice.

**Suggested fix, cheapest first.**
1. **`conductor run --detach`** — spawn the engine into its own process group / detached child, print the
   pid and control-plane URL, and return. The control plane already exists, so every verb keeps working;
   only the lifetime changes.
2. Failing that, **document the hazard loudly** in `operating.md` and in the run-conductor skill: launch
   long runs via `Start-Process -WindowStyle Hidden` / `nohup` / a service, never from a shell you might
   close. A watcher agent in particular must not hold the engine in one of its own background tasks.
3. Consider a **supervisor/`--restart-on-crash`** flag, since the recovery path below already works.

**What worked well — the recovery is genuinely excellent, and deserves protecting.** Restarting with the
same plan produced, with no flags and no coaxing:
```
[04:11:14] recovered: session #3 was interrupted — will resume its agent session
[04:11:16] restored budget: $29.17 agent / $0.05 overhead / 562k tokens (from prior process)
[04:12:15] session #4 start — Resume G1 attempt 2/6 (resume #1 of b9099344)
```
It detected the interruption, resumed the *agent's own* session rather than restarting it cold, and
restored the cost/token budget across a process boundary so the run-level cap still means something. The
resumed agent picked up mid-task and went straight back to its measured before-state evidence. Crash
*recovery* is a solved problem here; crash *avoidance* is the gap.

---

## 17. Documentation drift worth a sweep

- `plan-config.md` advisor defaults (entry 3) — wrong.
- `.claude/skills/run-conductor/SKILL.md` says a hand-edited tracker DONE flip is *discarded*. Current
  `VerdictEngine` accepts it via the W1.3 transition fallback with a `WARNING` and a ledger entry. The
  skill's description of the trust model is now stricter than the engine's behaviour.
- `docs/tracker.md` still documents `events.jsonl` as the append-only truth in its runtime-files table
  while `run-conductor`'s gotchas say assert on `run.db`. The `.conductor/` listing in `tracker.md` also
  does not match what a run actually produces (no `events.jsonl`, `state.json`, `queue/`, `lessons.md`
  in this run's state dir; it has `transcript.jsonl`, `bg-logs/`, `mcp-config*.json`).

---

## 18. The phase gate speaks a second grammar, so a log consumer needs two vocabularies

The stage-closing verdict — the one that decides whether a stage stands — is unmatchable by the same
pattern that matches the session verdict. Session verdicts say `gates green`; the phase gate says:

```
stage G6 checkpoints all DONE — scheduling full-battery phase gate
phase gate G6 finished in 803s — RED: fast-app:OK · guards:OK · battery:FAIL
phase G6 full battery RED — queuing fix session (attempt 1/2)
✓ phase G6 CONFIRMED (full battery green) — advancing
```

Nothing there contains `gates green` or `gates RED`. A watcher filtering on the session-verdict
spelling (which is what the documented watch filter did) sees `Advanced — G6.2 done`, then eleven
minutes of silence, then a Fix session appearing with no stated cause — and has to reverse-engineer
the RED from the log after the fact. That is exactly how it played out here at 17:17–17:30.

**Impact:** the single most consequential line in the run is the easiest one to miss.
**Suggested fix:** emit one canonical token in both places — `gates GREEN` / `gates RED` — on the
phase-gate result line as well as the session verdict, and keep the prose after it. Cheap, and it
makes one grammar sufficient for any log consumer.

---

## 19. A phase-gate RED consumes a stage attempt, and the two log lines disagree about which

```
[17:30:44] phase G6 full battery RED — queuing fix session (attempt 1/2)
[17:30:47] session #22 start — Fix G6 attempt 2/2
```

Same event, three seconds apart, two different attempt numbers. Beyond the cosmetic off-by-one, the
substance is worth documenting because it changes the intervention threshold: the phase-gate failure
itself burns an attempt, so on a `maxAttempts: 2` stage the FIRST battery RED leaves exactly one
repair session before the circuit breaks. Any watcher heuristic phrased as "worry at attempt ≥4"
never fires on such a stage — it parks first.

**Suggested fix:** agree on one numbering and print it once; and consider making a phase-gate RED's
repair session not consume the stage's delivery budget, since it is a distinct kind of failure from
"the agent could not deliver."

---

## 20. `P4 squash` fails at most stage closes and swallows git's reason

Reproduction across six stage closes on this run:

| Stage | Result | Duration |
|---|---|---|
| G1 | `git rebase returned non-zero — history unchanged` | 2s |
| G2 | `stage G2 complete — chore(conductor): commits squashed` | 0s |
| G3 | `git rebase returned non-zero — history unchanged` | 2s |
| G4 | `stage G4 complete — chore(conductor): commits squashed` | 0s |
| G5 | `git rebase returned non-zero — history unchanged` | 1s |
| G6 | `git rebase returned non-zero — history unchanged` | 2s |

4 of 6 fail. Consequence is cosmetic (the per-session `chore(conductor): sNN … — Idle` commits stay
uncollapsed — 11 of them in this branch so far), but it is the same defect as entry 14 and it is now
the majority case rather than an occasional one.

**Two plausible causes were tested against the log and BOTH are refuted** — recording them so nobody
re-derives them:
- *Dirty working tree.* No. G6 failed with only untracked files present; G2 succeeded with a modified
  tracked file (`M eval-results/2026-07-29/mcp-qa.md`) in the tree.
- *Chore commits interleaved with work commits (needing a reorder).* No. G4's range contains an
  interleaved `bd018af chore(conductor): s14` in the middle and squashed successfully anyway.

The 0s/2s split suggests the successes may be no-ops that never invoked git at all, but that cannot
be settled from the log — **because the engine logs that the rebase returned non-zero and never logs
what it said.**

**Suggested fix (the actionable one regardless of cause):** log git's stderr and exit code on the
failure path. Three entries and two dead hypotheses in, the reason is still unknown purely because
the error text is discarded. A one-line change would likely close this permanently.

---

## 21. A rolled-over session's commits and closed checkpoints are never recorded

**Severity:** medium — a ledger that reads as data loss on every rollover, whether or not any
occurred. Found 2026-08-02 while tuning `maxSessionTokens` for this run.

`SessionRunner.cs:411` sets `rec.Outcome = SessionOutcome.RolledOver` and returns **before** the
verdict pass that populates `NewCommits` (whence `commit_count`) and `newly_done`. The engine never
looks, so both are structurally 0 for every rollover:

| run | RolledOver sessions | `commit_count` > 0 | `newly_done` non-empty |
|---|---|---|---|
| sk-studio | 34 | 0 | 0 |
| conductor (sarban-face) | 11 | 0 | 0 |

**Git says otherwise.** Counting non-`chore(conductor):` commits inside each rolled-over session's
own `started_utc..ended_utc` window:

| run | left ≥1 agent commit | agent commits the ledger reports as zero |
|---|---|---|
| sk-studio | **19 of 34 (56%)** | 28 |
| conductor | **10 of 11 (91%)** | 20 |

**Impact.** Forty-eight agent commits are invisible in the ledger. Three consequences, in order of
cost: (a) an owner watching the board sees every rollover produce nothing and concludes the token
ceiling is destroying work — the exact wrong conclusion, and the one that gets a working cap turned
off; (b) any per-session efficiency read off `newly_done` mis-attributes a rollover's contribution to
its successor; (c) `RolledOver` cannot be distinguished from a genuinely empty session, so the one
case worth investigating — a rollover that really did land nothing — is unfindable.

**Suggested fix.** Run the commit/tracker half of the verdict on the rollover path before returning.
It is a read of git and the tracker; it spawns nothing and cannot fail the session. If that is
unwanted, the cheaper version is a `NULL`/`-1` sentinel so the surfaces render "not measured" rather
than a confident `0`.

---

## 22. `softBreakRatio` is a ratio, but the cost it has to cover is absolute

**Severity:** medium — it makes a correctly-set cap fail in a way that looks like the cap being too
low, so the fix people reach for (raise the cap) works by accident and for the wrong reason.

The cooperative nudge fires at `softBreakRatio × maxSessionTokens`, leaving
`headroom = (1 − ratio) × cap` for the agent to land its sub-task, commit, and write the handoff.
That wrap-up costs an **absolute** number of tokens — it scales with the session's context size, not
with the cap. Measured, as final tokens minus nudge threshold for sessions that ended clean:

- sk-studio stage H, 5 sessions: **1.03M · 1.63M · 1.91M · 1.97M · 2.01M**
- DevContext2 #27: **2.63M** (nudge at 12:23:12, clean exit 12:25:27, 3 commits, `G9.1` closed)

Because the reserve is a ratio, it shrinks with the cap exactly when it must stay constant:

| configuration | headroom | rollover rate |
|---|---|---|
| sk-studio 6M / 0.7 | 1.8M | **67%** (31 of 46, stages B/C/E/F) |
| conductor 8M / 0.75 | 2.0M | 11 over the run |
| sk-studio 9M / 0.7 | 2.7M | 22%; stage H **0 of 6** |
| DevContext2 20M / 0.7 | 6.0M | 0 of 1 |

sk-studio's 6M era cost **25–54M tokens per checkpoint against 20.0M uncapped** — stage F burned 9
sessions and 53.8M tokens for one checkpoint. Raising to 9M did not give the agent more room to
*work*; it gave it more room to *stop*, and stage H then rolled over zero times.

**Suggested fix.** An absolute `softBreakReserveTokens` that takes precedence over the ratio when
set, defaulted from the run's own observed wrap-up spend (the engine already has both numbers —
nudge threshold and final tokens — for every clean session). The ratio stays as the fallback for a
run with no history. Failing that, `doctor` could warn when `(1 − ratio) × cap` falls below ~2M.

**Context, since it is the same mechanism:** the full three-run analysis behind both entries — why a
cap must sit above the repo's *session floor*, why the tail is less productive per token rather than
more expensive, and how to set both numbers for a new repo — is in
`docs/dev/TOKEN-BUDGET-TUNING.md`.

---

## 23. `conductor inject` silently truncates an instruction to its first line

**Severity:** high — `inject` is the one channel a human has to steer a live run, and it fails
silently in the direction that looks like success.

Measured 2026-08-13 while watching the two DevContext2 pre-release runs. A 2,919-character gate
instruction, passed as a single PowerShell here-string, produced a **343-byte** queue file whose
`text` ended at the first newline. The CLI still printed
`queued 001-… — injected into the next session prompt`, the JSON was well-formed, and `status`
showed nothing amiss.

Probe, to find the cut point exactly:

```
$m = "PROBE-alpha line one`nPROBE-beta line two no blank before`n`nPROBE-gamma line four after a blank"
conductor inject $m
→ {"text":"PROBE-alpha line one", …}
```

The cut is at the **first newline**, not at the first blank line.

**It is the CLI, not the shell.** A second positional argument is rejected
(`error: Could not match 'PROBE-two-args-second' with an argument`), so PowerShell did not split the
string into three args that Spectre then dropped — the command received one argument containing
newlines and stored only its first line. The queue filename is slugged from that same first line
(`001-gateharness-chore-from-the-owner.json`), which is the likely mechanism: one first-line value
feeding both the slug and the payload.

**Why this is worse than losing text.** What survives is the *preamble* — the authority, the
urgency, the ordering ("do this FIRST, in its own commit, before resuming N2 work") — while every
instruction it introduces is gone. The agent receives a peremptory order it cannot satisfy but which
reads as complete, and nothing in the log, the queue file, or `status` says otherwise. An operator
who does not hand-read the queue file will believe the run was steered.

**Suggested fix.** Store the whole argument; derive the slug from the first line only. Failing that,
reject a multi-line instruction outright, or echo the stored character count in the success line —
`queued 001-… (2,919 chars)` would have exposed this on the first try.

**Workaround until then.** Pass the instruction as one physical line (use separators, not newlines)
and verify with `python -c "import json;print(len(json.load(open(PATH))['text']))"` against the
queue file before trusting it.

**Corrected 2026-08-14 (KS2.0), measured.** "It is the CLI, not the shell" above is wrong, and the
correction matters because it says where the text dies. The engine never truncated: run the probe
against `conductor.exe` directly and all 93 characters land in the queue file, from PowerShell and
from bash alike. `conductor` on this machine is not that exe — it is `~/scoop/shims/conductor.cmd`,
the one-line `@"…\conductor.exe" %*` batch file `tools/install.ps1` writes, and **cmd.exe ends a
command line at the first newline**, so the exe is handed one argument holding one line. Replaying
the probe through a `.cmd` shim of exactly that shape: `stored chars: 20` of 93; through the exe:
93. The second-positional test proved only that the *exe* saw one argument — by then the rest was
already gone. No batch file can fix this (the cut happens before the batch runs), so the shim itself
is the follow-up: a real shim binary, or put the install dir on PATH and drop the `.cmd`.

What KS2.0 does close: the queue stores the whole argument (pinned), the slug is derived from the
first line alone rather than five words wherever they fall, the prompt section renders every line
verbatim with a blank line between items, and the success line now reads
`queued 001-… (2,919 chars) — injected into the next session prompt`. The count is the check the
suggested fix asked for, and against a shim-cut instruction it prints `(20 chars)` — the failure
that used to look like success now says its own size out loud.

---

## What worked well (worth protecting in refactors)

- **The verdict line is excellent.** `verdict inputs: gates green · commits 2 · newly DONE [G1.1] ·
  dirty YES` is the single most useful line in the log — every input to the decision, in one place.
- **The two-tier gate design earned itself at G6.** Both checkpoints passed their per-session
  `-Scope app` gates; the stage-boundary full battery then caught a real regression the fast tier
  cannot see (`dotnet-podcasts → maui-present: 'MAUI' not found in output` — the "one vocabulary for
  service" change had narrowed what reaches the rendered surface). Cheap gates per session, the
  expensive truth at the boundary, and the boundary is empowered to overturn a green session: that is
  the whole design working, and the repair session fixed the cause without touching the expectation.
- **Claims-vs-confirmation held.** The agent's premature "CLAIMED" prose moved nothing; the board only
  changed when the real claim landed and the real battery agreed.
- **`journey` is the right pre-flight.** It surfaced the workflow chain and per-stage model in a form
  where a misconfiguration is visible before any spend.
- **Custom `workflows` + `templatesDir` made the tool fit the project** rather than the reverse. Being
  able to delete the Verify session and the QA-previous ceremony — because this project's real cadence
  has neither — is what made the run economical.
- **`conductor bg` + the process table** (`bg status` showing dead/exited children with runtimes) made
  cleanup after an abort verifiable rather than hopeful.

---

## Closure ledger (SF7.1, 2026-08-01)

What this era actually did about each finding above. The stage column is the checkpoint that owns
the fix; the commit is the one a reader can `git show`. Where two commits appear, the finding had
two halves and both are named. **Measured from the commits, not from the era spec's Appendix B
index** — the commit bodies cite these finding numbers themselves, and where the index disagreed
with the commits the commits won (two corrections, noted in the rows).

| # | Finding | Stage | Commit | What closed it |
|---|---|---|---|---|
| 1 | Cost counters read zero, then jump at the verdict | SC2.3 | `55da220` | `ClaudeProvider` emits a `TokenDelta` per new assistant message id; `LiveCostEstimator` prices them at the rate this run has actually been billed and labels the basis rather than shipping a price table |
| 2 | `agent.model` dropped without `{model}` | SC3.1 | `d4c9103` | `doctor` reads the MERGED agent per stage and FAILS — never warns — on a pinned model with no substitution point; `args` and `resumeArgs` are judged separately, because a placeholder missing only on resume is correct until the first resume |
| 3 | The default `advisor` block cannot work | SC3.4 | `abe0eb1` | `AdvisorConfig.Args` defaults to a working headless invocation, and a dead advisor key is fatal at load instead of falling back behind one grey line |
| 4 | Unknown `RunIf`/`SkipIf` tokens are TRUE | SC3.1 | `d4c9103` | `ConditionVocabulary` mirrors the evaluator's parse and plan load refuses an expression naming a token it does not know. **Corrects this note:** bare junk is stuck TRUE, but the observed `!gatesgreen` typo strips its `!` and is stuck FALSE — so a fix step written to catch red gates never ran at all |
| 5 | Foreground blocking; the stall line does not say so | SC5.2 + SF6.1 | `e6b15c7` + `8dd1aa3` | `SessionWatchdog.Remedy` names the likely cause and the exact `conductor bg start` command on both stall lines, and a live bg child counts as proof of life — suggestions (a) and (b) both. The built-in session template carries the same rule so the agent reads it before it stalls |
| 6 | `plan set` strips `//` comments | SC3.2 | `587eadd` | The dropped-comment count is reported before the write and the annotated original is kept alongside as a `.bak` |
| 7 | `/telegram/status` says `configured` for a config that cannot deliver | SC1.2 | `160f731` | `TelegramReadiness` is the single place that decides deliverability; doctor, `GET /telegram/status` and `StartAsync` all read it, and the Test button stops taking the path that works exactly when the feature does not |
| 8 | The claim path is easy to miss | SF6.1 | `8dd1aa3` | The built-in session template says the claim verb is the only claim, puts the deferred-MCP fallback on the same line as the CLI form, and orders the claim BEFORE the handoff |
| 9 | Nothing instructs the agent to mark work in progress | SF6.1 | `8dd1aa3` | Mark in-progress FIRST is the template's first instruction (the commit body cites this finding) |
| 10 | Agent events stored as truncated strings | SC7.1 + SC7.2 | `33d1f81` + `6d805e1` | Capture: `ToolEventExtractor` stores name plus canonical fields, each capped on its own, so what is stored is always complete JSON (transcript schema v2). Display: `ToolLine` renders one call as the line a human reads, and every session gets the digest this finding was actually asking for |
| 11 | An agent can write outside the repo, unseen | SC7.1 | `33d1f81` | `SessionRunner.NoteOutsideRepoWrite` records out-of-repo write paths on the session record, capped at `MaxOutsideRepoWrites`. It only became possible once the path survived capture — which is why no verdict had ever mentioned one. **Appendix B lists #11 under SC7.1 and #10 under bare SC7; the commit answers both** |
| 12 | The battery starts ~1s after the agent exits | SC4.1 | `ba9b523` | `BatterySettler` makes the battery wait for the session's own teardown, and a red gate is retried once before it costs a fix session |
| 13 | Telegram reports healthy and delivers nothing | SC1.1 | `b7d6eb4` | `ConductorHost`'s doc comment claimed the host ran no long-running `IHostedService`; because the claim was believed, nothing started the host. `StartRunServicesAsync`/`StopRunServicesAsync` start it on the run path and drain it on the way out |
| 14 | Status transitions each land their own commit | SC6.1 + SC4.2 | `04e092a` + `1ce4ba7` | `ReportSubstance.Of` decides the commit trigger and excludes everything the engine says about itself by name; separately, a bookkeeping commit stops buying a green verdict (`Git.ExcludeBookkeeping`). The squash half of this finding is #20 |
| 15 | An injected correction renders after the evidence it corrects | SC4.4 | `cfdb1ad` | The human correction is placed above the evidence it corrects |
| 16 | `conductor run` has no detached mode | SC5.2 | `e6b15c7` | A run outlives the shell that launched it |
| 17 | Documentation drift worth a sweep | SF7.1 | `1ebb536`, `36e406e`, `abe0eb1` | All three named drifts: `docs/tracker.md`'s runtime-files tree (`1ebb536` — five documented entries no run produces, fourteen real artifacts undocumented); the `run-conductor` SKILL.md trust model, which said the OPPOSITE of the engine (`36e406e` — a hand-edited tracker DONE is accepted via the W1.3 fallback, not discarded); and `plan-config.md`'s advisor defaults, already corrected by SC3.4 (`abe0eb1`) and now pinned by a test that reads them off `AdvisorConfig` |
| 18 | The phase gate speaks a second grammar | SC2.2 | `603fbbb` | `GateRunner.Token` is the only place a battery verdict is spelled, on the phase-gate line, the session verdict and the reuse path that carried no verdict at all |
| 19 | A phase-gate RED consumes a stage attempt | SC2.2 | `603fbbb` | `RunState.NextAttemptNumber` is the one source both lines read. **Remainder, stated:** the note's second suggestion — that a phase-gate RED's repair session not consume the stage's delivery budget — was considered and NOT adopted. The disagreeing numbers were the defect; the budget policy is a deliberate choice and stands |
| 20 | `P4 squash` fails at most stage closes | SC6.2 | `5c357b2` | Squash by rebuilding rather than rebasing, and it says what it did instead of swallowing git's reason. SC6.1 (`04e092a`) fixed the ordering that exposed the cause — the engine rewrote the tracker from `run.db` after the agent had committed it, so a tracked file always had unstaged changes when the squash ran |
