# KS7.3 — cost/usage with the cache split, and an OTel emit a real collector renders

Session 8, stage KS7, 2026-08-18. Every number below was measured on this machine; nothing here is
read off a doc comment.

---

## 1 · Flags verified first (trap 16)

Installed CLI: **claude 2.1.235**. Live probe, one turn, captured at
`C:/Users/shahi/AppData/Local/Temp/ks73-probe/stream.jsonl`
(`claude -p "Reply with exactly: ok" --output-format stream-json --verbose`).

**Per-turn usage exists and it is four-way.** Verbatim from the assistant message:

```json
"usage":{"input_tokens":2,"cache_creation_input_tokens":9306,"cache_read_input_tokens":20673,
         "cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":9306},
         "output_tokens":4,"service_tier":"standard"}
```

Three findings, one of which changed nothing and two of which are handed forward:

| # | Measured | Consequence |
|---|---|---|
| 1 | `input_tokens` / `cache_creation_input_tokens` / `cache_read_input_tokens` / `output_tokens` are all present per turn | The checkpoint's premise holds. Nothing to work around. |
| 2 | `cache_creation` is now a nested object splitting `ephemeral_5m_input_tokens` from `ephemeral_1h_input_tokens` — all 9306 were **1h** here | The two TTLs bill at different multiples of base input. Conductor takes `total_cost_usd` from the CLI and models no rates, so nothing is wrong today; filed as **bug #53** for whoever adds a rate model. |
| 3 | The result envelope carries `modelUsage` per model: `claude-opus-5[1m]` with `contextWindow: 1000000`, `maxOutputTokens: 64000`, `canonicalModel: claude-opus-5` — and a **second** model, `claude-haiku-4-5`, billed in the same session (897 in / 8 out, $0.000937 of a $0.1044 turn) | This is exactly KS7.4's "re-measure the model lineup and context ceilings". It is already captured; KS7.4 should read that file, not re-probe. |

**The peer OTel surface the era doc hoped for is not a documented flag on this build.** `claude --help`
does not mention `CLAUDE_CODE_ENABLE_TELEMETRY`; what it does have is a `claude gateway` subcommand,
described as the enterprise auth/telemetry gateway. So there is no per-run OTel export to reconcile
conductor's numbers *against* — conductor emits its own, which is what this checkpoint builds.

---

## 2 · The cache split, kept rather than folded away

**What was wrong.** `ClaudeProvider` added `cache_creation_input_tokens` into `TokensInput` and the
name died there — its own doc comment said so: *"the state has four buckets and no cache-write
bucket"*. Every downstream consumer therefore saw a three-way split of a four-way wire.

**What was NOT done, deliberately.** The fold is not undone. `SessionRecord.TokensTotal` sums the
buckets to gate `limits.maxSessionTokens`, and 18 archived runs plus every cost row in the store were
written against that meaning; re-basing `Input` would silently restate history. So the split is
recovered by **naming the part**, not by moving it:

- `AgentStreamState.TokensCacheWrite`, `TokenDelta.CacheWrite` — a **subset of `Input`**, never a peer.
  Adding it to a total that already contains `Input` double-counts, and the doc comments say so at
  every point a reader could get it wrong.
- Every existing total is unchanged to the token. `KS7_3OtelExportTests.ClaudeStreamKeepsAllFourUsageBucketsAndTheTotalIsUnchanged`
  asserts exactly that, driving the real `ClaudeProvider` with the verbatim live line above:
  `CacheWrite == 9306`, `CacheRead == 20673`, `Output == 4`, `Input == 2 + 9306`, `CacheWrite <= Input`.
- A provider whose wire does not report it emits **0, not a guess** (`AProviderThatReportsNoCacheWriteEmitsZeroRatherThanGuessing`).
  0 therefore means *not reported*, never *no cache was written* — which is why the historical spans in
  §4 read 0 and that is correct.

Files: `src/Conductor.Core/Providers/AgentStreamState.cs`,
`src/Conductor.Core/Providers/ClaudeProvider.cs:153,168,177,205-212`,
`src/Conductor.Core/Events/Kinds/GateEvents.cs`, `src/Conductor.Core/AgentSession.cs:74`.

---

## 3 · The emit: `conductor otel`

`src/Conductor.Core/Telemetry/` — `OtelSpan`, `OtelIds`, `OtelBuildContext`, `OtelTrace`, `OtlpJson`,
`OtlpHttpExporter`; verb in `src/Conductor/Commands/OtelCommand.cs`. **No OpenTelemetry SDK
dependency**: the SDK exists to instrument a live process from ambient call context, and these spans
are reconstructed after the fact from an event log that may be days old and belong to another
machine's run — the one thing the SDK adds is the one thing that would be wrong. What remains is a
data shape and an HTTP POST.

The tree, and where the convention names apply:

```
conductor.run                    conductor's own scheduling — conductor.* attributes
  stage KS7                      conductor's own scheduling
    chat claude-opus-5           ONE model call unit -> gen_ai.* , span kind CLIENT
      execute_tool <name>        gen_ai.tool.name, name form "execute_tool {tool}"
    gate build                   conductor.gate.*, status ERROR when it failed
```

`gen_ai.*` is put only where it is true. A conductor session is the unit the convention describes —
one agent process, one model, one usage total — so it carries `gen_ai.system`, `gen_ai.operation.name`,
`gen_ai.request.model`, the four usage counters **including both halves of the cache**, and the span
name form `{operation} {model}`. Inventing gen_ai attributes for a run or a stage would tell a backend
something untrue.

Two wire details that silently corrupt an export if missed, both asserted in
`OtlpBodyQuotesEveryInt64AndUsesBase16Ids`: OTLP/JSON int64s are **strings** (nanosecond timestamps are
past 2^53 by a factor of a million), and ids are lowercase base16, not hyphenated UUIDs.

Ids are **derived from the run id**, so exporting the same run twice is the same trace rather than a
duplicate (`ExportingTheSameRunTwiceIsTheSameTraceNotTwo`).

---

## 4 · A real collector renders a real run

Collector: **official `otelcol` 0.159.0 windows_amd64**, downloaded from
`open-telemetry/opentelemetry-collector-releases` — a third-party binary, not a stub written for this
proof. OTLP receiver on `127.0.0.1:43181`, `debug` exporter at `verbosity: detailed`. Run as a tracked
background child (`conductor bg`, purpose `ks73-collector`).

Export, driven through the **fresh build**, never the published engine on PATH, against a `sqlite3
.backup` snapshot of the live store in a scratch rig:

```
> src\Conductor\bin\Debug\net10.0\conductor.exe otel --run 9491891fe700463ba0d876c06280cce2 \
      --endpoint http://127.0.0.1:43181
run 9491891fe700463ba0d876c06280cce2 -> 27 spans, 934 per-turn events, trace b898c408a64e2dffd34fe87d097d761c
exported 27 spans in 1 batches to http://127.0.0.1:43181
```

What the collector printed for the root — full capture in `ks7-3-collector-root.txt`:

```
Span #0
    Trace ID       : b898c408a64e2dffd34fe87d097d761c
    Name           : conductor.run
     -> conductor.run.id: Str(9491891fe700463ba0d876c06280cce2)
     -> conductor.run.sessions: Int(8)
     -> gen_ai.usage.input_tokens: Int(1627658)
     -> gen_ai.usage.output_tokens: Int(9372)
     -> gen_ai.usage.cache_read_input_tokens: Int(137391968)
     -> conductor.plan: Str(Karvansara edge - gates that can't be gamed, and the courier)
```

That is the era's whole argument in one span: **137.4M cache reads against 1.6M fresh input**, rendered
by somebody else's software. A session span with its `gen_ai.*` attributes and its first `gen_ai.turn`
events is in `ks7-3-collector-session.txt`.

Honest gaps in this render: `gen_ai.usage.cache_creation_input_tokens` is 0 on every span, because
`TokenDelta.CacheWrite` did not exist when these events were written (see §2 — 0 means *not reported*);
and there are **no `execute_tool` spans**, because this run has no `McpCallFinished` events at all —
its sessions claim through the CLI, which is the same fact bug #19 turned on. The mapping for both is
covered by tests; the live render will fill in from the next session forward.

---

## 5 · The reconciliation with K4.1 — exact, across two independent paths

The checkpoint's other exit. The comparison is deliberately *not* the exporter checked against itself:

- **the span**: `LiveMetrics.ContextForSession` folded over the event log, after the fact;
- **the `sessions` table** (`context_high_water`, `context_mean_turn`, `context_turns`): written LIVE by
  the provider's own `ContextWindowMeter` while each session was running.

| session | span (high / mean / turns) | sessions table (high / mean / turns) | match |
|---|---|---|---|
| 1 | 91236 / 64321 / 15 | 91236 / 64321 / 15 | YES |
| 2 | 277935 / 194741 / 104 | 277935 / 194741 / 104 | YES |
| 3 | 298272 / 166295 / 175 | 298272 / 166295 / 175 | YES |
| 4 | 212840 / 138686 / 111 | 212840 / 138686 / 111 | YES |
| 5 | 228474 / 148069 / 129 | 228474 / 148069 / 129 | YES |
| 6 | 250748 / 152471 / 172 | 250748 / 152471 / 172 | YES |
| 7 | 218161 / 134542 / 148 | 218161 / 134542 / 148 | YES |

All seven finished sessions of this run, identical on both sides. The curve a collector renders **is**
K4.1's derivation, and `PerTurnContextCurveOnTheSpanReconcilesWithK41sDerivation` pins it so it stays
that way: it also sums the per-turn span events back and asserts they reproduce the same mean and high
water, so the curve and its summary cannot drift apart.

---

## 6 · Gates

`dotnet build Conductor.slnx -clp:ErrorsOnly` — **0 errors, 0 warnings**.
`dotnet test Conductor.slnx --filter "KS7_3|K4_1|SC23|AgentProvider|Architecture"` — **104 passed,
0 failed**, including the architecture ratchet (no new file over 500 lines or 3 types).

One trap paid for and recorded in the ledger: building `tests/Conductor.Tests/Conductor.Tests.csproj`
**alone** produces 716 Meziantou analyzer errors in files nobody touched; the same tree is clean
through `dotnet build Conductor.slnx`. The solution build is the measurement that counts.
