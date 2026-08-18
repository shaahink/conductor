# KS7.4 — fork instead of cold start, and what the CLI actually reports about its own lineup

Session 8, stage KS7, 2026-08-19. Rig: `C:/Users/shahi/AppData/Local/Temp/ks74-probe`, claude 2.1.235.

---

## 1 · Flags verified first (trap 16)

From `claude --help` on the installed CLI, verbatim:

```
  -c, --continue          Continue the most recent conversation in the current directory
  -r, --resume [value]    Resume a conversation by session ID, or open interactive picker
      --fork-session      When resuming, create a new session ID instead of reusing the original
                          (use with --resume or --continue)
      --session-id <uuid> Use a specific session ID for the conversation (must be a valid UUID)
```

So the checkpoint's premise holds: forking exists and is a *modifier on resume*, not a separate verb.

---

## 2 · The measurement that decided the design

One 30k-token base conversation (`aa53aebc`), asked the same question three ways. The base was seeded
with a word the model could only know from carried context, so "did the context survive" is answered
by the reply and not by inference.

| mode | session id afterwards | fresh in | cache **write** | cache **read** | out | cost |
|---|---|---|---|---|---|---|
| `--resume` | **same** `aa53aebc` | 2 | 58 | 29,995 | 8 | $0.0157875 |
| `--resume --fork-session` | **new**, chosen by claude (`ca8d51df`) | 2 | 45 | 30,053 | 8 | $0.0156865 |
| `--resume --fork-session --session-id <ours>` | **new, and the one we asked for** (`1535ff92`) | 2 | 0 | 30,098 | 8 | — |

All three answered correctly from carried context.

**The delta the checkpoint asks for: +45 prompt tokens on 30k — 0.15% — and $0.000101 *cheaper*.**
A fork is not a more expensive resume. The carried prefix arrives as a cache **read** rather than a
cache write, and write bills above read, which is why the fork came out marginally ahead.

**The design-deciding line is the third row.** `--fork-session` composes with `--session-id`, and the
CLI honoured the id we asked for. Conductor therefore does **not** surrender id control to fork —
crash recovery (`CrashRecovery.ClaudeSessionId`), transcript correlation and the session record all
keep working unchanged. Without that we would have had to scrape a new id out of the stream and
reconcile it after the fact.

Transcripts on disk under `~/.claude/projects/<cwd>/`: the fork wrote **its own** `.jsonl`; the base
file was not extended by it. That is the property that makes this a fork and not a resume — see §3.

---

## 3 · What was built

`SessionFork.BaseFor(history, stageId, kind, agent)` — a pure function, `src/Conductor.Core/Orchestration/SessionFork.cs`.
One line wires it into `SessionRunner` at the `AgentSession.Start` call; `AgentSession` grew a third
arg template (`AgentConfig.ForkArgs`) chosen ahead of `ResumeArgs` and `Args`.

**Fork, not resume, and the difference is not cosmetic.** Resuming would append the fix onto the
delivery session's own transcript: a second fix attempt would inherit the first attempt's failure, and
the delivery session's record would no longer be what it was when the checkpoint was confirmed. A fork
branches — the base is left exactly as it ended and every attempt starts from the same clean point.

**Opt-in twice over.** Nothing forks unless the plan supplies `agent.forkArgs` *and* names the kind in
`agent.forkKinds` (`["fix","audit"]` is the pairing the checkpoint asks for). A kinds list without a
template does not silently fall back to a resume. `AnExistingPlanIsUnchangedByUpgrading` asserts that
every session kind still starts cold under a plan that sets neither.

**The known failure mode, stated rather than discovered later:** a fork resumes a transcript *on disk*.
If the agent CLI has pruned the base session, `--resume` fails and the session dies. That is the
second reason forking is opt-in, and it is why `BaseFor` refuses a session that has not finished
(`ASessionStillRunningIsNotForked`) — a mid-write transcript is the one most likely to disagree with
what the record says about it.

Tests: `tests/Conductor.Tests/KS7_4SessionForkTests.cs`, 10 facts covering the policy (which session,
which stage, which kinds), the arg resolution (`{claudeSessionId}` is what we resume FROM,
`{sessionId}` is the id we keep), and the merge path.

**Scope line, drawn honestly:** a forked session still receives the full composed prompt. Trimming the
prompt *because* context is already carried is KS7.5's subject (context economics), not this
checkpoint's, and doing it here would have made the fork's token delta unmeasurable against the
resume baseline this checkpoint is claimed on.

---

## 4 · The model lineup and context ceilings, re-measured

Landed in `docs/dev/TOKEN-BUDGET-TUNING.md` §11 with the derivation. In brief, from a real `result`
envelope's `modelUsage` — provider-reported, not a spec sheet:

| model id reported | canonical | context window | max output |
|---|---|---|---|
| `claude-opus-5[1m]` | `claude-opus-5` | 1,000,000 | 64,000 |
| `claude-haiku-4-5-20251001` | `claude-haiku-4-5` | 200,000 | 32,000 |

Two findings worth carrying forward:

- **This era's sessions run the 1M variant**, and peak at **298k of it — 30%**. What ends a session
  here is conductor's 32M cumulative ceiling, never the model's window. The 32M / 0.85 pair does not
  move; the reason it works is now measured instead of assumed.
- **A session bills more than one model.** The probe billed opus *and* `claude-haiku-4-5` (897 in / 8
  out, $0.000937 of a $0.1044 turn) inside one request. Conductor takes `total_cost_usd` from the CLI
  so the ledger is right today — but any future per-model attribution must not assume the session's
  declared model is the only one billed.

---

## 5 · Gates

`dotnet build Conductor.slnx -clp:ErrorsOnly -nodeReuse:false -p:UseSharedCompilation=false` —
**0 errors, 0 warnings**.
`dotnet test Conductor.slnx --filter "KS7_4|KS7_3|AgentSession|SessionRunner|Architecture|AgentProvider"` —
**97 passed, 0 failed**.

Those two MSBuild switches are not decoration. This session found the **root cause** of the
`Conductor.Planning` analyzer wall the previous session recorded as "don't `dotnet run` during a
build": **MSBuild node reuse plus the shared Roslyn compiler server serve a stale analyzer config**.
The tell is that MA0006 is reported as an *error* while `.editorconfig:51` sets it to *suggestion* —
the severities were not applied to that compilation at all. Deleting `obj/` does not clear it and
plain rebuilds do not clear it (reproduced three times); disabling node reuse and shared compilation
does, first try. Filed as **bug #54**. The flagged `Conductor.Planning` files are not defective and
must not be "fixed".
