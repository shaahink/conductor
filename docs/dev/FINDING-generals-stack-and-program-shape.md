# FINDING — Generals rebuild: stack decision and program shape

**Companion to** [`FINDING-generals-zero-hour-modernization.md`](FINDING-generals-zero-hour-modernization.md),
which surveys the landscape and the prior art. That document asked *which path*. This one answers
*which stack, what does v1 actually contain, and what shape does the Conductor plan take*.

Written 2026-07-31. Still assessment — no code, no commitment.

---

## 1. What changed, and what drops out

Four constraints landed that materially simplify the program:

| Constraint | Consequence |
|---|---|
| **No C++ in the product** (design preference, firmly held) | Path D (strangler-fig, C++ shrinking over 2 years) is out. The C++ engine becomes a **CI-only oracle**, never a shipped artifact. |
| **Agent-driven for certain; you read code, don't write it** | Technology chosen for *mechanical verifiability* and *port fidelity*, not for author familiarity. This removes C#/TS-because-you-know-them from the argument entirely. |
| **v1 is offline vs. the AI. Multiplayer, matchmaking, gamification later** | **No server, no netcode, no lobby, no accounts in v1.** Ship a static bundle. |
| **One or two technologies. Don't overcomplicate** | Single-language where possible; no separate asset-conversion toolchain. |

Three things fall out of the plan immediately, and that's most of the simplification:

- **No netcode.** No sequencer, no relay, no signalling, no desync resync. Determinism stays — but
  now it earns its keep purely as a *testing* property. Multiplayer later becomes nearly free
  precisely because the sim was deterministic from day one for testing reasons.
- **No asset pipeline.** The engine reads BIG archives, INI, W3D models and maps *at runtime*. Port
  those loaders and there is nothing to convert, no build-time tooling, no Python, no C#. This alone
  removes a whole technology from the stack.
- **No backend.** Browser file picker → OPFS, exactly as Project New Shoes already does. Users bring
  their own retail install. Static hosting on anything. Zero running cost, zero ops.

---

## 2. Stack decision: Rust, single language

**Sim + engine + renderer + wasm + native, all Rust. Bevy for the presentation shell only. Nothing
else.**

### The honest counter-argument first

The best-documented large port of the AI era is Microsoft's TypeScript compiler → Go
([Project Corsa / tsgo](https://github.com/microsoft/typescript-go/discussions/411)). Their stated
**top** language-selection criterion was *"how easy it is to port the existing code structure
one-to-one"* — they transplanted the codebase file-by-file to preserve type-checking behaviour
exactly. And they explicitly rejected Rust for it: the compiler leans on garbage collection, so Rust
"would have required significant re-architecting," and TS→Go was judged "a more direct translation
than TypeScript to Rust would be."

That argument transfers to us and it is not weak. SAGE is deep-inheritance C++ with raw pointers,
cyclic object references (object ↔ team ↔ player ↔ drawable), and custom memory pools. A literal
1:1 Rust transliteration of *that object graph* fights the borrow checker on every line. On
correspondence grounds alone, **C# would be the better port target** — classes, virtual dispatch and
inheritance map nearly one-to-one, and GC handles the cycles for free. That is almost certainly why
OpenSAGE chose C#, and it's a real point against my recommendation.

### Why Rust anyway

1. **Browser + no server + 3D is the headline goal, and it decides this.** `wgpu` targets WebGPU and
   WebGL2 from the same source that targets Vulkan/Metal/DX12 natively — a trodden path with shipped
   games on it. .NET-on-wasm driving a skinned-mesh renderer through per-frame JS interop is a bet,
   not a path. Given "it must run in the browser" is the whole point, this dominates.
2. **Fewest technologies.** Rust covers sim, renderer, loaders, CLI tools, native build and wasm
   build. C# would need a JS/TS layer for the GPU boundary — so "one language" is achievable in Rust
   and isn't in C#. That is literally your "don't overcomplicate" criterion.
3. **Agent-driven changes the weighting.** When agents write ~all the code, the compiler is a free
   reviewer working 24/7. On a 300k-line codebase that no human fully reads, the difference between
   "won't compile" and "null at runtime in month nine" is large. Rust's static feedback is the
   tightest available.
4. **CI throughput, which is the binding constraint.** The verification ladder means thousands of
   trace-diff runs per commit. Native Rust does that 10–30× faster than Node and comfortably faster
   than a .NET equivalent. Feedback speed is what determines what an agent can attempt at all.
5. **Determinism is enforceable.** You can forbid `f32`/`f64`, `HashMap` iteration, clocks, threads
   and I/O at the sim crate boundary and fail the build on violation. That's a machine-checked
   invariant, not a code-review convention.
6. **Readability, since you'll be reading not writing:** Rust with a house style is at least as
   readable as 2003 C++, and considerably more honest about ownership and failure.

### The mitigation that makes the correspondence problem bounded

tsgo's concern is real, but it applies to the **object model**, not to the logic. Damage
calculations, locomotor updates, weapon state machines, AI behaviour trees all transliterate
cleanly. Only the entity graph fights.

So: **decide the object model once, in Phase 0, and write down the mapping rules.** One document,
applied mechanically everywhere after:

| SAGE C++ | Rust |
|---|---|
| `Object*`, `Drawable*` (raw, cyclic) | `ObjectId` / `DrawableId` — generational indices into an arena |
| Module class hierarchy + virtual dispatch | `enum` dispatch (deterministic, cache-friendly) or trait objects where the hierarchy is genuinely open |
| `AsciiString` / `UnicodeString` | interned `Symbol` for keys, `String` for text |
| `Real` (float) | `Fixed` (Q16.16), with all math on it |
| `MemoryPool` / custom allocators | arena per entity kind; no allocation in the tick |
| Inheritance for reuse | composition |

Pay that cost once, not per function. This is exactly the kind of uniform mechanical rule agents
apply well and humans apply inconsistently.

### Bevy: batteries where they help, firewalled where they hurt

You asked for the option with the most batteries included. That's Bevy — ECS, asset loading, input,
audio, skinned-mesh rendering, instancing, UI, and a working wasm target out of the box. But Bevy
schedules systems **in parallel by default**, which is a direct hazard to the one property the whole
project rests on, and its API churns every release — a real maintenance tax over a multi-year build.

So: **Bevy owns the shell (render, input, audio, UI, windowing). The sim is a plain Rust crate with
zero Bevy dependency that the shell calls exactly once per 30 Hz tick.** Batteries where they save
months, a hard firewall around determinism, and if Bevy churns or disappoints, only the shell breaks
— the expensive half is insulated. If Bevy's WebGPU performance disappoints under load, the fallback
is `winit` + `wgpu` directly, and the sim doesn't notice.

### On the "big Rust ports" you mentioned

Worth correcting the premise, because the lesson is different from the story: Claude Code's
TypeScript source (~512k lines) was exposed via a sourcemap published to npm, and *community* Rust
rewrites followed — one was submitted as a PR and closed by an Anthropic engineer, alongside a DMCA
complaint. It wasn't an Anthropic-run Rust port. The transferable data point survives anyway and
it's a good one: **multiple people produced working Rust ports of a half-million-line codebase in
days-to-weeks with AI assistance.** Agent-driven porting at this scale is real. The tsgo port is the
better-documented precedent and the one to actually copy.

---

## 3. The method: structural transliteration, oracle-verified

Name it so it stays honest, because it is *not* any of the three things people usually mean:

- Not clean-room (you read the source and mirror it — that's the point).
- Not strangler-fig (no C++ in the shipped artifact).
- Not "rewrite from the rules" (you are not redesigning the game; you are moving it).

**You port function by function, file by file, preserving names and structure, and a trace-diff
oracle proves each port didn't change behaviour.**

Four conventions carry the whole thing, and all four are Phase 0 deliverables:

1. **Mirror the directory structure.** `GameLogic/Object/Update/PhysicsUpdate.cpp` →
   `crates/sim/src/object/update/physics_update.rs`. Any agent can find the reference for whatever
   it's porting, and any human can diff the two trees side by side. tsgo did exactly this.
2. **Keep the original names.** Types, functions, fields, INI keys. `grep` works across both trees.
   Rename nothing until the port is complete — cosmetics are a separate, later, mechanical pass.
3. **The mapping-rules doc** (§2) is law, and it lives in the repo's `AGENTS.md`.
4. **The port ledger.** Auto-generated from the two trees: every C++ file marked `ported`, `stubbed`,
   `skipped` or `untouched`, with reasons. It is simultaneously the burndown chart, the agent's work
   queue, and the honest answer to "how far along are we". Generate it, never hand-maintain it.

And the harness that makes it verifiable, from the companion doc: instrument the C++ build to dump
per-frame per-entity state traces, then build a **divergence bisector** that localizes any mismatch
to *entity + field + frame*. That is the analogue of `objdiff` in the decompilation world, and
objdiff is precisely what made LLM-driven matching decompilation work at all. It is the single
highest-leverage artifact in the program.

---

## 4. What v1 actually is

**"Boot a browser tab, point it at your Zero Hour install, play a full skirmish against the AI."**
No account, no server, no download, no install.

One honest caution: offline-vs-AI is *not* a cheap subset. Netcode is maybe 10–15% of the work;
everything else — the full unit roster, economy, construction, upgrades, generals powers,
pathfinding, the skirmish AI, the command bar — is still in scope, because a skirmish uses all of
it. The real saving is that you skip an entire category of *hard* problems (desync, NAT, latency
compensation, cheat surface) and an entire category of *ops* problems (servers, accounts, matchmaking).
That's a good trade, but budget for v1 ≈ 85% of the total game, not 40%.

Two things make it much less daunting than it sounds:
- **The AI already exists in the source.** You port the skirmish AI; you don't design one.
- **The rules already exist in the INI files.** You write an INI→table compiler and never hand-port
  a balance number. Mod compatibility (Shockwave, Contra, Rise of the Reds) then comes nearly free,
  and every mod becomes extra test data.

**Explicitly not in v1:** multiplayer, matchmaking, ranking, accounts, campaign cutscenes/Bink video,
the map editor, mod tooling beyond "it loads mods". Multiplayer is cheap to add later *because* the
sim was deterministic from the start — a WebRTC P2P mesh plus a tiny signalling function, no game
server. Save that for when the game is worth playing.

---

## 5. Program shape — the phases and their gates

Ordered so that risk retires early and something is visible on screen by month ~5. Every gate is a
number a machine produces.

| Phase | Content | Gate |
|---|---|---|
| **P0 · Harness** | Fork GeneralsGameCode; add trace instrumentation + fine-grained hashing (**the only C++ ever written**); trace format; differ; **divergence bisector**; golden corpus; mapping-rules doc; port ledger generator; repo skeleton + CI | Bisector localizes a *deliberately injected* one-bit divergence to entity + field + frame |
| **P1 · Data spine** | BIG reader, INI parser + catalogue compiler, W3D model/animation loader, texture loaders, `.map` loader. Pure Rust, no rendering | Data parity: dumped tables byte-identical to the C++ build's, on stock ZH **and** one large mod |
| **P2 · First Blood** | Minimal renderer (terrain + skinned meshes) + sim skeleton: tick loop, fixed-point math, arena/entity model, one locomotor, one weapon, damage, armor, death | Two real units, from real INI data, on a real map, in a browser tab: one drives to the other, shoots it, it dies — **and the 300-frame trace matches the C++ build**. *Owner gate: you look at it.* |
| **P3 · Economy** | Supply lines, dozers, construction, power, build queues, unit production | Scripted build-order scenario matches trace |
| **P4 · Roster** | All units, weapons, armors, locomotors, upgrades, sciences, generals powers | Combinatorial micro-spec matrix (every unit × weapon × armor × terrain) ≥95% parity |
| **P5 · Pathfinding & AI** | Port the pathfinder; port the skirmish AI | AI plays a full match to a win condition; replay-corpus parity ≥99% |
| **P6 · Shell** | Command bar, selection, control groups, minimap, hotkeys, audio, menus, save/load | A human plays a full skirmish start to finish in a browser without touching a debugger. *Owner gate.* |
| **P7 · Ship** | Static hosting, asset onboarding (file picker → OPFS), perf pass, load-time budget | Cold tab → playing in <60 s on a mid laptop; 60 fps with 300+ units; bundle under budget |
| *Later* | Multiplayer (WebRTC P2P + signalling), matchmaking, gamification | — |

**P0–P2 is roughly 5–6 months and retires most of the risk.** If P2's trace doesn't match, the whole
approach is wrong and you've spent five months, not three years. Full v1 is a **2–3 year** program
at sustained agent-driven pace — the DevilutionX / Ship-of-Harkinian number rather than the
OpenSAGE number, because you start on the far side of the readable-source wall.

**Kill gates:** if P0's bisector can't localize an injected divergence, stop — nothing downstream
works. If P4 stalls below ~90% parity for a quarter, stop and reconsider; the ported data spine and
loaders still have standalone value to the community.

---

## 6. How this maps onto Conductor

It's an unusually good fit, for one specific reason: **every gate above is a CI exit code or a parity
percentage.** No human judgement in the loop, which is exactly what Conductor's
verify-independently ritual wants.

Concretely, when you're ready to plan:

- **The plan lives in the game repo, not this one.** `plans/generals.plan.json` + a tracker there.
- **Phases → `stages[]`**, wired with `dependsOn` so P2 can't start before P1 is green. Split the
  big ones (P4 especially) into per-subsystem stages so a session is one bounded port, not a month.
- **Gates are shell commands**: `cargo test`, the trace-differ returning non-zero below a parity
  threshold, the port-ledger tool asserting coverage didn't regress. Real exit codes, which is what
  Conductor verifies against anyway.
- **`ownerGate: true` on P2 and P6** — the two moments you'll actually want to look at the screen.
- **`promptExtra`** carries the transliteration law: mirror the tree, keep the names, obey the
  mapping table, never hand-port a balance number, always run the differ.
- **The port ledger is the session work-queue.** An agent asks it "what's the next unported file
  whose dependencies are all ported?" and gets a bounded, verifiable task. That is close to the ideal
  agent work item, and it's mechanically generated rather than hand-planned.
- **First plan should be P0 only** — six-ish stages, a few weeks. Prove the loop (agent ports a
  thing, differ verifies it, Conductor confirms and moves on) before committing to a multi-year plan
  file. If that loop works, the rest is repetition at scale, which is precisely what Conductor is for.

---

## 7. Open questions

1. **Bevy or bare `wgpu` for the shell?** I've recommended Bevy-with-a-firewall. If the API churn
   over a 3-year build worries you more than the months it saves, bare `winit` + `wgpu` is the
   conservative call and the sim doesn't care either way. Decidable at P2, not now.
2. **Does the P2 owner gate change your mind if it lands late?** Worth agreeing now what "too slow"
   means, while it's cheap to say.
3. **Upstream or solo?** Landing the trace instrumentation and bisector in GeneralsGameCode costs you
   nothing (you need them anyway), makes desyncs diagnosable for everyone, and is the cheapest way to
   discover whether collaborators exist. I'd do it regardless.
4. Still open from the companion doc: **"the bite step"** — build step, or byte step?
