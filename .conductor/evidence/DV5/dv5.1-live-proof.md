# DV5.1 — the live proof, and the defect it caught

Driven 2026-08-26 through the FRESH build (a scratch console in `%TEMP%\dv5rig\driver`
referencing `src/Conductor.Core/Conductor.Core.csproj`), as a tracked `conductor bg` child,
against scratch git repos under the temp directory. Full transcript:
[`dv5.1-live-proof.log`](dv5.1-live-proof.log).

Nothing here ran against this repo except one **read-only** `git ls-remote`.

## 1. `CloudPreflight.Probe` against real git — all six verdicts, real output

| case | verdict | what the owner is told |
| --- | --- | --- |
| repo initialised, no commits | `NothingToClone` | "there is no commit there for a cloud session to clone — the path is not a git checkout, or it has no commits yet." |
| committed, never pushed | `NoUpstream` | "main has no upstream — it has never been pushed, so the remote has no copy of it at all." |
| pushed and level | `Ok` | "main at aa3b4d50, clean, and origin/main has the same commit." |
| one staged file | `DirtyTree` | "main has 1 uncommitted change:\n A  note.txt" |
| one local commit not pushed | `RemoteDiffersFromHead` | "main is at bff6d5d1 but origin/main is at aa3b4d50 (locally 1 ahead at last fetch) — a cloud session would clone the remote's commit, not yours." |

The first case is why the verdict is named `NothingToClone` and not `NotARepo`: `git init` with no
commit is a real repo that a cloud session still cannot clone, and the live run is what showed the
first wording was wrong about it.

## 2. `ls-remote` against a real GitHub remote

```
repo=C:\code\conductor
  branch=feat/divan upstream=origin/feat/divan
  local HEAD  = 32a48687c563eca112a0f8cecaf75a9445b6f324
  ls-remote   = 115020b094a9c0130f245dfad62d28edf926c45c
  same commit = False
```

This is the check the tracking counters cannot make. At that moment `git status` had nothing to say —
the DV5.1 commit had not been pushed — and `ls-remote` correctly reported that the remote, which is
what a cloud session clones, was still one commit behind. Read-only; no write of any kind.

## 3. The verb's own paths, end to end

*Create, dirty:* refused with the git state, and the command withheld —

> /cloud refused for dv5 rig: main is at bff6d5d1 but origin/main is at aa3b4d50 (locally 1 ahead at
> last fetch) — a cloud session would clone the remote's commit, not yours.

*Create, clean and level:* the platform's refusal, verbatim, plus the exact command —

> /cloud cannot start a new cloud session for dv5 rig. This engine drives claude 2.1.246, and
> starting one is interactive-only there: … Run this on a terminal … `claude --cloud "sweep the docs"`

*Bare `/cloud`:* usage, the live git state, and the cost line that is always a word.

## 4. The real `claude` binary, through the real seam — and the defect

`ClaudeCloudCli` invoked the installed binary. The argv is the one the CLI's own message spells:

```
argv: claude -p "say ok" --cloud 00000000-0000-4000-8000-000000000000
exit=1 timedOut=False
stderr: Error: --cloud "00000000-0000-4000-8000-000000000000" is not a cloud session ID or URL.
        With --print, --cloud sends the prompt to an existing cloud session: pass its ID
        (session_... or cse_...) or its claude.ai/code URL. To start a new cloud session from a
        description instead, drop --print.
```

**This is the defect the live run caught.** `CloudSessionRef` had been written to accept a bare UUID
or a `sess_` prefix — both guessed. The real shapes are `session_…` and `cse_…`, and a bare UUID is
a *local* session id the cloud surface refuses. Under the guessed shape, a real cloud session id
would have been read as a task description and the owner would have been handed a create refusal for
a session that already existed. No unit test over synthetic data could have found it; only driving
the real binary did. `CloudSessionRef.TryParse` now matches `session_…`, `cse_…` and the
`claude.ai/code` URL, and the test theory pins the corrected shape with the old wrong ones as
negative cases.

A second run with a correctly *shaped* but unreal id proved the routing reaches the CLI:

> Cloud session session_notarealsession (find it at claude.ai/code) failed (exit 1):
> Error: failed to send message to cloud session session_notarealsession: invalid session ID: must
> be a cse_… or session_… tagged ID
>
> Cost: unknown — a cloud session reports no per-turn spend to this engine.

Two things that reply proves. The parser is a **router**, not a validator — the CLI judges whether a
session-shaped id is a real one, and conductor deliberately does not re-implement a tagged-id format
it has never observed. And the cost line survives the failure path: it is the word, on every path.

## 5. What was NOT proved, and why

No real cloud session was created. Creating one needs a TTY, and the two things it would have cost
are named in the findings doc itself: §2.4 item 5 says cloud sessions drain the same Max pool the
local run needs — and the local run here is this session — and §2.4 item 8 says sharing defaults on
Max are Private-or-Public with repo-access verification off. The create direction is refused by the
engine, so there is no conductor code path that a real cloud session would have exercised.

The one avenue not explored, recorded so the owner can decide rather than a session deciding for
them: `claude --debug-file <path>` plus a child process given a real console (`CREATE_NEW_CONSOLE`)
would satisfy the TTY check and might let the session id be recovered from the debug log. That is
Windows console P/Invoke plus log scraping, aimed at defeating a refusal a research-preview surface
makes deliberately, and it is a checkpoint of its own — not something to smuggle into this one.

## 6. Suite

`DV5_1CloudVerbTests` 26/26. With the surface and architecture neighbours —
`KS11*`, `DV1_2`, `DV3_4`, `DV4_4`, `ArchitectureTests`, `SF7_1`, event/timeline — 350/350 green.
The type-ceiling ratchet went red at 4-against-3 on `OwnerControlEvents.cs` and was fixed by moving
`OwnerCloudAction` to its own file. No bar was touched.
