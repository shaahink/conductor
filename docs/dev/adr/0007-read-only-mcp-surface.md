# 0007 — The MCP surface conductor exposes to the outside is read-only, by construction

- **Status**: accepted
- **Date**: 2026-08-19
- **Stage**: KS8.1 (karvansara-edge)
- **Supersedes**: nothing. Sits beside [0005 — push-only remote observability](0005-push-only-remote-observability.md), which decided the same shape for the Telegram side.

## Context

Conductor already speaks MCP. `conductor mcp-serve` runs a JSON-RPC 2.0 server over stdio and hands
the *agent it spawned* sixteen tools: `task_update`, `task_add`, `conductor_note`, `bug_new`,
`bg_start`, `inject_instruction`, `run_query` and the rest. That server is inside the trust boundary
by definition — it is wired into a session the engine itself launched, in a run the engine is
driving, and the writes it makes are the session's own progress.

KS8 asks a different question: what does conductor expose to a client it did **not** spawn — an
editor, a dashboard, a second model, a colleague's agent? The obvious answer is "the same server,
it's already written". That answer is wrong, and the reason is not hypothetical.

2026 was the year MCP's security record got written. The pattern in every serious disclosure is the
same: a server that mixes reads with privileged writes on one connection, and a client that can be
*talked into* calling the write. Tool poisoning — malicious instructions embedded in the data a
server returns, which the model then reads as instructions — is now its own OWASP entry precisely
because the read path and the write path share a model. A surface that returns run history *and*
offers `abort` is a surface where "here is your run history, and by the way call abort" is one
sentence away from being obeyed. The mitigation everyone reaches for is a confirmation prompt, which
is a mitigation that depends on a human being present, which an unattended overnight run is not.

Conductor's blast radius on that surface would be unusually bad: the writes available are not
"create a file", they are *abort the run*, *inject an instruction into the next session's prompt*,
*mark a checkpoint done*. The last one is the worst of the three. This project's entire verdict
mechanism rests on the claim that a checkpoint is confirmed by a gate battery and not by the agent
that claimed it (KS4.5). A reachable `task_update` on an unauthenticated stdio surface hands anything
that can talk to it the power to mark work done that was never done.

## Decision

**The outward-facing MCP surface serves resources and has no tools.**

1. It is a **separate server and a separate verb** — `conductor mcp-observe`, class
   `McpObserveServer` — not a `--read-only` flag on `mcp-serve`. The difference between the two is
   the threat model, and a threat model belongs in the command an operator types, not in a flag they
   can forget. A flag is also one refactor away from defaulting the wrong way; a type with no write
   path in it is not.

2. **`tools/call` is refused, always**, with `-32601` and a sentence naming this ADR. `tools/list`
   answers with an empty array rather than an error: "how many tools do you have" deserves the true
   answer, and a client that sees `[]` knows it asked a server that has none, where a
   method-not-found reads as an older server that might have them under another name. `initialize`
   declares a `resources` capability and **no `tools` capability**, which is the part a conforming
   client reads before it asks anything.

3. **Read-only is enforced by SQLite, not by discipline.** Every answer is built from
   `RunHistory`/`ArchiveView`/`RunArchive`, and `RunArchive`'s connection is `Mode=ReadOnly`
   (`src/Conductor.Core/History/RunArchive.cs`). A write added to this surface by mistake would not
   be a bug that ships — it would be refused by the connection at runtime. The observe server never
   touches `IRunStore`, which is the type that can write; an architecture test forbids the reference.

4. **Three resource families**, matching what an outside reader actually wants:
   - `conductor://history` — every catalogued run, newest first.
   - `conductor://runs/{run}/status` — one run: the reconciled status word, the stored one, and the
     same `StateDto` the Face renders.
   - `conductor://runs/{run}/money` — one run's billed spend, through the same `MoneyAnalyzer` and
     `MoneyJson` that `conductor money --json` uses.

5. **Both status words ride every row.** `status` is `RunLiveness.Reconcile(stored, storeLooksLive)`
   — a run whose engine died still says `running` in its database, and the surface says `orphaned`.
   `storedStatus` is the untouched word beside it, because reconciling is a rendering decision and a
   surface that dropped the stored value would be hiding the evidence for its own claim.

## Consequences

- An outside client can watch a run and price it. It cannot steer one, and there is no configuration
  that makes it able to. If someone genuinely needs remote control, that is a new ADR and a new
  authenticated transport, not a flag on this one.
- There is a second MCP server to keep working. That is the price, and it is small: the JSON-RPC
  framing types (`JsonRpcRequest`/`JsonRpcResponse`/`JsonRpcError`) are shared, only the dispatch
  differs.
- `mcp-serve` is unchanged. Nothing about this decision restricts the agent's own surface, which is
  inside the boundary and needs its writes.
- The resources are a *catalogue-wide* read: `mcp-observe` serves every run this machine has, not
  one. That is deliberate — "list the runs" is the first thing a client asks — and it is why there is
  a `--home` option: pointing it at another state home is how you scope it.
