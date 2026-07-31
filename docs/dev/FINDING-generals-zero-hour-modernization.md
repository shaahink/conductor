# FINDING — Generals / Zero Hour: modernize the C++ source, or greenfield rewrite?

**Status:** assessment only. No code, no commitment. Written 2026-07-31.

**Question asked:** given EA's GPL source release, is it more doable to (a) plan a modernization of
the released C++ codebase and fix its long-standing defects, or (b) treat it as greenfield — rebuild
the game from the rules and raw materials, using the old code only as an observation source — with
the explicit goals of *browser playability*, *an agent being able to build and verify it*, and
*not C++*?

**Short answer:** neither. Both options are known failure shapes, and prior art tells us so with
unusual clarity — a dozen projects have run this exact experiment on other games and the results are
not ambiguous. One of your stated prizes (browser) is already claimed by someone else. The
recommendation below is a third shape that keeps the game playable and better *every single week*
instead of promising a payoff in year three.

---

## 0. The one-paragraph version

Every faithful game-rebuild project that succeeded — OpenRCT2, DevilutionX, Ship of Harkinian, and
Thyme for this very game — used the **same** method: keep the original running and replace it one
piece at a time, verifying each piece against the original as you go. Every project that tried to
rebuild from observation instead — OpenRA, OpenSAGE, OpenXcom — took 6 to 15 years, and the two C&C
ones either redefined the goal away from fidelity or are still not playable. You are being handed
the thing all of those projects spent their first years *earning*: readable source. The right move
is to start where they finished. **Strangle the C++ engine subsystem by subsystem, in the language
you want, behind C ABI seams, with a trace-diff oracle proving each swap didn't change behaviour.**
The game stays playable at every commit, you never write new C++, the browser build falls out for
free, and — crucially — each step is small enough and its pass/fail signal sharp enough that an
agent can actually do the work.

---

## 1. Landscape as of July 2026 — what already exists for *this* game

| Project | What it is | State |
|---|---|---|
| [`electronicarts/CnC_Generals_Zero_Hour`](https://github.com/electronicarts/CnC_Generals_Zero_Hour) | EA's official release, Feb 2025. GPL v3 **with additional terms**. `Generals/` + `GeneralsMD/`. | Archived, preservation only, no PRs. Needs DirectX SDK, STLport 4.5.3, 3DSMax 4 SDK, RAD Miles/Bink, GameSpy — several unobtainable. |
| [`TheSuperHackers/GeneralsGameCode`](https://github.com/TheSuperHackers/GeneralsGameCode) | The main community continuation. ~1.3k stars, 230 forks, 1600+ commits, weekly releases. | VS6/C++98 → **VS2022/C++20**, CMake + vcpkg, Linux via Docker, headless replay regression in CI. Deliberately pinned to retail 1.08/1.04 compat for now; roadmap says more opens up "once we can break retail compatibility". |
| [`fbraz3/GeneralsX`](https://github.com/fbraz3/GeneralsX) | Cross-platform fork of the above. | SDL3 + DXVK (D3D8→Vulkan) + OpenAL, **64-bit**, native Linux/macOS/Windows. Syncs upstream regularly (last seen 2026-07-20). |
| [Project New Shoes](https://newshoes.gg/) | **The original C++ engine compiled to WebAssembly, in the browser.** | WASM + WebGL2, OPFS storage, **WebRTC P2P multiplayer with relay discovery**, mod management (BIG/ZIP/7z/NSIS), saves + replays. GPLv3. Bring your own retail copy; assets never leave the machine. |
| [Thyme](https://github.com/TheAssemblyArmada/Thyme) | Clean-room bottom-up C++ reimplementation of Zero Hour, *pre-dating* the source release. | Uses **OpenRCT2's method**: a DLL injected into the running game, replacing original functions one at a time, calling back into the original binary for anything not yet written. |
| [OpenSAGE](https://github.com/OpenSAGE/OpenSAGE) | Clean-room **C#** reimplementation of SAGE. Started ~2017. | 3600+ commits, ~329 open issues, README still says *"nowhere near playable yet."* |
| iOS/ARM64 port | The 2003 engine compiled natively for iPhone/iPad; campaign, skirmish, Generals Challenge, touch controls. | Exists, open-sourced. |

Plus 400+ forks of the EA repo within days of release. The community's own reaction
([discussion #266](https://github.com/TheSuperHackers/GeneralsGameCode/discussions/266),
[OpenSAGE #1023](https://github.com/OpenSAGE/OpenSAGE/issues/1023),
[Thyme #1168](https://github.com/TheAssemblyArmada/Thyme/issues/1168)) was a call to *stop
fragmenting*: "if each fork works in isolation, we risk duplicating work and fragmenting the
community."

**Three consequences, and you should sit with all three before spending a year on anything:**

**1. "Run it in the browser and people go nuts" is done.** New Shoes runs the real engine in WASM
with WebRTC multiplayer today. Browser delivery is no longer a differentiator — it's table stakes,
and the incumbent gets the original engine's exact behaviour for free. *Do not use the browser goal
to justify a rewrite.* If browser-playable Generals is the actual want, go help New Shoes and you're
weeks from impact instead of years.

**2. The C++ modernization "plan" already exists and is executing weekly.** VS2022/C++20, CMake,
64-bit, Vulkan, cross-platform, headless replay CI — landed or landing. You would not be writing a
plan; you'd be boarding a moving train.

**3. Starting a ninth independent effort is the thing the community is explicitly asking people not
to do.** That's not a moral argument, it's a practical one: the scarce resource here is
contributor-hours and test coverage, and both are already spread thin across eight codebases.

---

## 2. The defect catalogue, sorted by how deep the root cause goes

Your list ("the byte/build step, thirty-two bit, the networking glitches, and so on") — ordered not
by annoyance but by *how much of the engine must change to fix it properly*. This ordering is the
real argument for or against a rewrite.

**Tier 1 — already fixed or trivially fixable. No rewrite justification here.**
32-bit / 4 GB limit (GeneralsX is 64-bit today) · VS6/C++98 and the unobtainable SDKs (done: CMake,
vcpkg, C++20) · DirectX 8 / modern GPUs / windowing / input (SDL3 + DXVK) · Windows 11 crashes,
widescreen, alt-tab (routine patch work, in flight) · GameSpy dead since 2014 (community servers,
and now WebRTC relay, already exist).

**Tier 2 — fixable in place, but painfully. Genuine friction.**
Custom allocators and `MemoryPool`, pervasive raw ownership · single-threaded logic tick coupled to
the render loop · pathfinding quality (bunching, stalling, absurd routes) entangled with
`GameLogic` object state · the INI/data layer: enormous, string-keyed, parsed at load, unvalidated —
and modders live there, so it can't be casually redesigned.

**Tier 3 — the one that actually motivates a rewrite: determinism.**

This is the "networking glitches" you named, and it is not a networking bug. Generals uses
**deterministic lockstep** — nobody sends game state; everybody sends commands and re-simulates —
and periodically CRCs the whole game state to compare. Any divergence (one float rounding, one
iteration order, one PRNG draw) ends the match with "mismatch". Community analysis
([discussion #369](https://github.com/TheSuperHackers/GeneralsGameCode/discussions/369)) attributes
divergence to data/version differences, memory manipulation, and **floating-point non-determinism
across FPU precision modes, compilers, and CPU architectures**. Two symptoms follow directly:

- With 4+ human players mismatches are near-guaranteed. 3v3 — exactly what you want — is worst case.
- The CRC compare is coarse (historically ~every 100 frames) and reports only "mismatched". A desync
  is therefore *undiagnosable*: you get a dead match and no bug report.

The community's own proposed fixes — softfloat wrappers, fixed-point math, quantized CRCs, periodic
state snapshots — are the right ones, and every one means **rewriting the entire math and
simulation layer**. Do that inside the C++ codebase and you have "rewritten the game in C++ with
extra steps", you've broken retail replay compatibility (only VC6-compiled builds reproduce
1.04/1.05 replays bit-exactly), and you still have a 2003 C++ codebase.

That is the honest case for greenfield. Not the browser. Not 32-bit. **Determinism, and the fact
that fixing it properly is a sim rewrite either way.**

> One item from your dictation I couldn't map: *"the bite step"*. Most likely "the **build** step"
> (it doesn't build) or "the **byte** step" (memory/alignment) — both covered above. Say which and
> I'll fold it in properly rather than guess.

---

## 3. Prior art — what a dozen other projects already proved

This is the section you asked for, and it turns out to be the most decisive part of the assessment.
Enough of these experiments have run to completion that the answer is close to empirical.

### 3.1 The projects

| Project | Method | Outcome |
|---|---|---|
| **[OpenRCT2](https://github.com/OpenRCT2/OpenRCT2)** (RollerCoaster Tycoon 2) | Incremental: `openrct2.dll` injected into a patched `rct2.exe`, sharing its memory model; each original procedure rewritten in C **one at a time**, calling back into the original for the rest. | **Success.** Playable from very early on; original binary eventually eliminated entirely. The reference method. |
| **[Thyme](https://github.com/TheAssemblyArmada/Thyme)** (**Zero Hour**) | Explicitly copied OpenRCT2: injected DLL, `Call_*` hooker utilities to call original functions with correct types, incremental replacement. | Working, and it did this **without** the source. Now partly obviated by the release — but the *method* is the transferable asset, and it was proven on this exact engine. |
| **[Devilution → DevilutionX](https://github.com/diasurgical/devilutionx)** (Diablo) | Decompile to readable C (IDA dump → 2 years of group cleanup, aided by leftover debug symbols), then port. | **Major success.** Modern OSes, Android, consoles, high-FPS, hardware cursor. Thriving. |
| **[Ship of Harkinian](https://en.wikipedia.org/wiki/Ship_of_Harkinian)** (Ocarina of Time) | ZRET produced a **byte-matching** decompilation in ~21 months; port work started as decomp completed. | **Major success.** Native Windows/Linux/macOS/Switch, widescreen, uncapped FPS, HD textures, randomizer, later full co-op. Repeated for Majora's Mask (2Ship). |
| **[C&C Remastered](https://en.wikipedia.org/wiki/Command_%26_Conquer_Remastered_Collection)** (Petroglyph, commercial) | Recovered the original 1995 source, **kept the original simulation**, rebuilt only presentation (GlyphX renderer; every asset re-modelled then re-rendered to 2D to line up *frame for frame*). | **Shipped 2020.** A funded professional studio, given a free choice, chose to reuse the original sim and replace everything around it. |
| **[OpenRA](https://github.com/OpenRA/OpenRA)** (C&C/RA, C#) | Clean-room from observation. | Successful *as a game*, but explicitly **redefined the goal**: "envision what these games may have been were they developed again now." Openly criticized for inaccuracy; the wiki attributes this to being clean-room "relying solely on observing the original games." |
| **[OpenSAGE](https://github.com/OpenSAGE/OpenSAGE)** (Generals, C#) | Clean-room blackbox, from data files. | ~9 years, 3600 commits, **still "nowhere near playable."** |
| **[OpenXcom](https://openxcom.org/)** / **[OpenTTD](https://en.wikipedia.org/wiki/OpenTTD)** / **[OpenMW](https://en.wikipedia.org/wiki/OpenMW)** | Clean-room reimplementations. | All eventually excellent. ~15 / ~9 / ~6 years respectively from original game to first playable release. |
| **[Factorio](https://wiki.factorio.com/Desynchronization)** (shipping, deterministic-lockstep) | On desync, generate a report containing **both** server and client full game states, diff them. | Works — but their own docs warn the report "does not state the cause". Diffing state is necessary, not sufficient. |
| **[Age of Empires](https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond)** ("1500 Archers on a 28.8", Bettner & Terrano) | The founding lockstep paper: send commands not state; **adaptive turn length + network metering**. | The canonical design. Everything here inherits from it, including your problem. |
| **[decomp.me](https://decomp.me/) / [objdiff](https://github.com/encounter/objdiff) / decomp-permuter** | Tooling for matching decompilation: `m2c → compile → objdiff → permuter`, and now **an AI-in-the-loop variant where an LLM iterates against objdiff until it matches**, spawning the permuter on each improvement. | **The existence proof for your actual question.** Agents *can* do faithful porting work unsupervised — when there is a mechanical, per-attempt diff oracle. |

### 3.2 The four laws that fall out of that table

**Law 1 — Incremental replacement wins; big-bang rebuilds stall.** Every project that stayed
playable throughout (OpenRCT2, Thyme, DevilutionX, SoH) shipped. Every project that built a new
engine beside the old one and hoped to eventually catch up (OpenSAGE, and OpenRA on fidelity) took
6–15 years or abandoned fidelity as a goal. The difference is not talent or funding; it's whether
there was ever a day when the thing didn't work.

**Law 2 — Without a mechanical oracle, "faithful" is unachievable, so it gets redefined.** OpenRA's
own wiki names clean-room as the cause of its inaccuracy, and OpenRA responded by rebranding as a
reimagining. That's the honest outcome available without an oracle. Your stated requirement is the
opposite — *"it has to mirror the game and everything, the glitches, everything"* — so clean-room is
ruled out by your own spec, not by my preference.

**Law 3 — The hard part of the successful projects is the part you get for free.** DevilutionX spent
two years turning an IDA dump into compilable C. ZRET spent 21 months getting to byte-matching
decompilation. Both then produced excellent modern ports *quickly* once they had readable source.
**You are starting on the far side of that wall.** This is the strongest reason to be optimistic —
and it argues for the port shape, not the rewrite shape, because the port shape is what those
projects did with their readable source.

**Law 4 — When the professionals had this exact choice, they kept the original simulation.**
Petroglyph had funding, the original team's lineage, and a commercial mandate, and they still reused
the 1995 sim and rebuilt only presentation. Treat that as the strongest available prior.

### 3.3 And the law about agents specifically

The decomp ecosystem has already answered "can an agent do this work?" — yes, *when each attempt
gets a mechanical pass/fail diff against a reference*. Their loop is `LLM → compile → objdiff →
better? → keep`. Your analogue is `agent → build → trace-diff vs. last-known-good → parity %? →
keep`. Same shape, easier problem (behavioural trace equality, not instruction-level byte matching).
**Build that loop and the agent question answers itself. Skip it and no amount of language choice or
architecture will save you.**

---

## 4. The four paths, honestly costed

### Path A — Modernize the released C++ in place
Join TheSuperHackers/GeneralsX, push the roadmap, break retail compat when the project is ready,
replace the math layer, fix pathfinding in situ.

- **Pro:** shortest distance to players actually benefiting; real users this year. Existing community,
  CI, replay corpus, mod ecosystem. Every fix is incremental value, not a step toward a distant goal.
- **Con:** it's 2003 C++, which you explicitly don't want. Agent feedback is slow (long C++ builds)
  and blunt (a replay mismatches or doesn't, with no pointer to where). The determinism fix is still
  a sim rewrite, just performed inside a hostile codebase.
- **Risk of failing outright: low. Risk of you not enjoying it: high.**

### Path B — Pure greenfield rewrite
New engine, new language, from INI rules and observed behaviour; old code as documentation only.

- **Pro:** the architecture and language you want; determinism by construction.
- **Con:** this is the OpenSAGE result, and Law 2 says the fidelity spec you stated is unreachable
  this way. Zero Hour is 3 factions × 9 sub-factions, hundreds of unit types, ~200 generals powers,
  locomotors, armor tables, garrison, stealth, EMP — with 20 years of players who will notice a 0.1 s
  difference in dozer build time. With no browser differentiator left (§1), the payoff shrinks too.
- **Risk of never shipping: very high.** This is the default outcome for projects of this shape.

### Path C — Greenfield sim, C++ as a trace oracle
Build a new headless simulation beside the old engine, diff per-frame traces, chase parity %.

- Better than B — it has an oracle — but it still violates Law 1: there is a long desert with nothing
  playable, and motivation is a real engineering resource. Keep this in your pocket as the fallback
  if D's seams turn out to be worse than expected.

### Path D — **Recommended. Strangler-fig: incremental substitution, in your language, behind C ABI seams**

This is OpenRCT2's and Thyme's method, updated for the fact that you have source instead of a binary
— which makes it *dramatically* easier, because you can substitute at **link time** rather than by
patching a running executable.

1. **Fork GeneralsGameCode** (or GeneralsX for the 64-bit/cross-platform head start). It builds, it
   runs, it has replay CI. Day one you have a working game.
2. **Add trace instrumentation and a fine-grained rolling hash** — the only new C++ you ever write,
   and it's write-once. This also turns "mismatch" into a diagnosable bug *for the whole community*,
   so it's a contribution rather than a tax, and it's the right first upstream PR.
3. **Build the trace-diff oracle and the divergence bisector** (§6). Now every change has a
   mechanical verdict.
4. **Strangle from the outside in.** Carve a subsystem behind a C ABI, reimplement it as a Rust
   `staticlib` (or C#/TS where determinism doesn't apply), link it in, and let the oracle prove
   behaviour is unchanged. Repeat. Ordering matters — see §8.
5. **The C++ shrinks monotonically.** When `GameLogic` is the last thing left, the fixed-point
   determinism rewrite is a contained, well-understood project instead of a moonshot — and by then
   you have a complete behavioural trace corpus to check it against.
6. **The browser build falls out**, because the whole thing still compiles under emscripten the way
   New Shoes already demonstrates.

- **Pro:** playable and *better than retail* within months, not years. Satisfies Law 1, Law 2 (the
  oracle makes fidelity mechanical), Law 3 (starts on the far side of the wall), and Law 4 (the sim
  is replaced last, carefully, not first, hopefully). Each step is small, isolated, and
  machine-verified — the ideal agent work item.
- **Con, and it's real:** you don't *write* new C++, but you *live with* a C++ build and toolchain
  for a long time. If "no C++ anywhere near me" is a hard requirement rather than a preference, D is
  not for you and the honest alternative is A (accept C++, get real users) or C (accept the desert).
  Be clear with yourself which it is — this is the single decision that determines the whole plan.
- **Second real con:** cross-language link seams need care. Mitigations in §5.

---

## 5. Language and stack

Constraints in priority order: (1) bit-exact determinism on x86 + ARM + wasm; (2) headless and fast
in CI — thousands of trace-diff runs per commit; (3) an agent can write *and debug* it; (4) links
cleanly against C++ via a C ABI; (5) browser; (6) not C++; (7) ideally near your .NET/TS comfort zone.

(2) is the one people underweight and it dominates. In an agent-driven project the binding constraint
is how fast and how unambiguous the feedback is. A suite that takes 4 minutes instead of 40 seconds
doesn't cost 6× wall-clock — it changes what the agent can attempt at all.

| | Determinism control | CI throughput | Links into C++ | Browser | Agent ergonomics | You know it |
|---|---|---|---|---|---|---|
| **Rust** | Best — `no_std`-able; you can *forbid* floats at the crate boundary | Best | Excellent (`extern "C"` + cbindgen; staticlib) | Best-in-class wasm | Compiler is a free verifier | No |
| **C# / .NET 10** | OK with fixed-point; GC pauses a soft risk in a 33 ms tick | Very good (NativeAOT) | Awkward in-process; NativeAOT shared lib is possible but fiddly | Works; bundle size + interop friction | Excellent | **Yes** |
| **TypeScript** | Fine with Int32 fixed-point; must ban `Math.*` in sim | Weakest — 10–30× slower diffs | Not in-process | Native | Fastest edit→run | **Yes** |
| **Go** | OK | Good | cgo, and its threading model fights you | Large wasm, GC | Good | — |

**Recommendation — split by layer, don't pick one language.**

- **Substituted engine subsystems and eventually the sim core → Rust.** It is the only option on that
  table that is simultaneously good at determinism, C ABI linkage, CI speed, and wasm. Unfamiliarity
  matters least exactly here, because this code is written from a mechanical spec (the C++ it
  replaces, plus the trace diff) rather than from taste — which is also why it's the best possible
  agent work. And you can enforce *"no `f32`/`f64`, no `HashMap` iteration, no clock, no threads, no
  I/O"* as a **lint that fails the build**: a machine-checked determinism invariant, worth a great deal.
- **Shell, UI, lobby, tooling → TypeScript.** Renderer front-end (WebGPU with WebGL2 fallback), lobby,
  replay browser, mod manager. Browser-native; the same shell wraps to desktop via Tauri. This is
  where you'll spend your own hands-on hours, in your own language.
- **Offline pipeline → C# or Python.** BIG extraction, W3D → glTF, INI → sim-table compilation, map
  formats, asset validation. Batch jobs, no determinism requirement, your comfort zone — and OpenSAGE
  has already done a lot of this format work in C# under a free licence.

**On the link seam, concretely (this is the risky bit, so be precise):** do substitution in the
**native** build first — Rust `staticlib` exposing `extern "C"`, linked into the CMake build,
replacing one translation unit's implementation at a time. This is extremely well-trodden, keeps CI
fast, and keeps debugging easy. The wasm build follows later:
`wasm32-unknown-emscripten` staticlibs link into an emscripten C++ build. Do **not** start with
cross-module wasm linking and shared linear memory — it's possible but fiddly, and it's the wrong
risk to take on day one.

**If you'd rather not take on Rust:** for the sim I'd take TypeScript over C# (browser + CI story),
and C# over TypeScript if the browser stops mattering. But note that on Path D, refusing Rust costs
more than on the other paths, because in-process linkage is exactly what C# and TS are worst at —
you'd be pushed toward process/IPC boundaries and lose the incremental property that makes D work.
**Rust is close to load-bearing for the recommended path.** Decide this once, early, and don't
relitigate it.

---

## 6. The verification ladder — the part that actually decides success

Build it in this order. Every rung is machine-checkable and needs no human eyes.

**Rung 0 — Data parity.** Parse every BIG archive and INI file from a retail install; dump the
resulting object/weapon/armor/locomotor tables; diff against a dump from the instrumented C++ build.
Pure text diff, no simulation. *Automatable on day one.* Rule: never hand-port rules into code —
write an INI→table compiler so retail data stays the source of truth. Mod compatibility then comes
almost free, and the whole Shockwave / Contra / Rise of the Reds catalogue becomes extra test data.

**Rung 1 — Combinatorial micro-specs.** Spawn N units on an empty map, run a scripted 300-frame
scenario, dump the full state trace, diff against the reference trace. Then *generate these
combinatorially from the INI catalogue*: every unit × weapon × armour × terrain. Thousands of
machine-generated micro-specs no human wrote and no human has to read. This rung is where agents are
most effective and where most behaviour actually gets ported.

**Rung 2 — Replay parity.** Real `.rep` files replayed headless in both builds, per-frame hash diff.
Constraint discovered upstream: only **VC6**-compiled builds reproduce original 1.04/1.05 replays
bit-exactly, so either pin the oracle build to VC6, or — cleaner, and probably what you want —
generate a fresh golden corpus from your own instrumented build and treat *that* as the reference.
Upstream already runs per-platform headless replay regression in CI, so the pattern is proven.

**Rung 3 — Fuzz and soak.** Random command streams from random seeds, both builds, cross-checked.
Catches the long tail Rung 2 can't reach.

**Rung 4 — Visual and feel.** Human eyes. Last, smallest, and explicitly *not* a parity target.

### The single most valuable tool in the project: the divergence bisector

Factorio's own documentation makes the point for me: their desync report contains both full states
and *still* "does not state the cause." Diffing state is necessary and not sufficient. The bisector
must automatically narrow a divergence to **which entity, which field, which frame**, and print both
builds' update sequence for that entity around that frame.

This is the difference between an agent making steady progress and an agent guessing. It is the
direct analogue of `objdiff` in the decomp world — and note that objdiff is precisely what made
LLM-driven matching decompilation work at all. **If you build exactly one thing from this document,
build this.** Give it its own milestone; it is not a debugging convenience to add later.

### Determinism discipline (enforced, not aspirational)

- No floating point anywhere in the sim. Q16.16 or Q32.32 fixed-point throughout.
- Own trig/sqrt/atan2 tables. Own PRNG (seeded per match, e.g. xoshiro), never the platform's.
- Deterministic iteration order everywhere: stable entity IDs, ordered containers, explicit sorts.
- The sim has **zero** dependency on wall-clock time, I/O, threads, or ambient randomness.
- All of the above enforced by a CI lint that fails the build. A determinism rule that isn't
  mechanically checked will be broken within a month.

---

## 7. Networking, done properly

Keep deterministic lockstep — it's why RTS games can carry thousands of entities on nothing,
it's what makes replays tiny, and it's what makes the entire verification ladder possible. Bettner &
Terrano settled this in 1997 and nothing has changed. Fix everything *around* it:

- **Sequencer, not peers.** A dumb authoritative *ordering* server (WebSocket, WebRTC with relay
  fallback). It orders and forwards commands; it runs no game logic, so it's nearly free to host and
  isn't a cheat surface. This kills the NAT/port-forwarding problem outright — which is most of what
  people mean by "the networking glitches".
- **Adaptive turn length + network metering**, exactly as the 1500-Archers paper prescribes, instead
  of the original's fixed latency — so one player on bad wifi degrades smoothly instead of stalling
  all six.
- **Per-frame rolling state hash** piggybacked on the command channel. The server detects divergence
  within a second and names the diverging player *and the first diverging entity*. A mismatch becomes
  a filed bug report instead of a dead match.
- **Snapshot resync**, Factorio-style: on divergence, the majority-consensus client ships a full state
  snapshot to the outlier. A desync becomes a two-second hitch, not a lost game. This is the single
  biggest available UX win, and the thing the original could never do.
- Reconnect, spectating, and mid-game join all fall out of the sequencer design for free.

---

## 8. Roadmap — strangle from the outside in

Order the subsystems by *(determinism risk × difficulty)* ascending, so the early wins are safe and
visible and the dangerous core comes last, when your oracle and your instincts are both mature.
Every phase ends in a number a machine produces, not an opinion.

| Phase | Work | Gate | Scale |
|---|---|---|---|
| **0. Oracle** | Fork; trace instrumentation + fine-grained hashing; replay runner; **divergence bisector**; golden corpus | Bisector localizes a *deliberately injected* divergence to entity+field+frame | 1–2 months |
| **1. Outer shell** | Substitute audio, video playback, file I/O, BIG/INI loading. Zero determinism risk, immediate wins | Rung 0 green on stock ZH + one large mod; replay corpus still bit-identical | 1–2 months |
| **2. Presentation** | Renderer front-end, UI, input, camera. **Not a parity target — make it better.** Widescreen, high refresh, modern scaling | Playable, visibly better than retail. *First public release* | 3–5 months |
| **3. Netcode** | Sequencer, adaptive turn length, per-frame hashing, snapshot resync | Stable 3v3, zero *unexplained* desyncs over a 100-match soak | 2–3 months |
| **4. Pathfinding & AI** | Substitute behind the existing interface; fix bunching/stalling once it's yours | Replay parity maintained where behaviour is meant to be unchanged; measurable improvement where it isn't | 3–6 months |
| **5. Sim core** | The big one: `GameLogic` in Rust, fixed-point throughout, determinism lint enforced | ≥99% frame parity on the full replay corpus | 9–18 months |
| **6. C++ eliminated** | Remove the last translation units; wasm build from the pure-Rust/TS stack | Byte-identical traces vs. Phase 5; browser build ships | 2–3 months |

Two things to notice about this ordering versus the greenfield roadmap it replaces:

- **You have a shippable, better-than-retail game at the end of Phase 2 — month ~6.** Under Path B or
  C, month 6 is a headless simulation that renders nothing. Law 1 says that difference is what
  decides whether a project finishes.
- **The scary phase is fifth, not first.** By the time you touch `GameLogic` you'll have a mature
  oracle, a large golden corpus, hard-won knowledge of the codebase, and — if you've been pushing the
  instrumentation and fixes upstream — possibly collaborators.

Total to a fully de-C++'d, deterministic, browser-and-desktop Zero Hour: **~2–3 years** of sustained
agent-driven work. That is not a weekend, but it's the DevilutionX/SoH number rather than the
OpenSAGE/OpenXcom number, and Phases 0–2 (~6 months) tell you almost everything about whether the
rest is real.

**Kill gates.** If Phase 0's bisector can't localize an injected divergence, stop — nothing
downstream works without it. If Phase 5 stalls below ~90% parity for a quarter, stop and ship what
you have: a modernized, well-netcoded Zero Hour with a Rust periphery is *already an excellent
outcome*, and unlike Path B the intermediate state is a real product rather than a graveyard.

*(Aside: this shape is a good Conductor workload precisely because every gate is a CI exit code and a
parity percentage — sessions can be verified without a human in the loop, which is the property
Conductor's verification rituals are built around.)*

---

## 9. Legal, and it matters

- **Licence the new code GPL-3.0.** You'll have read GPL'd source to write it; anything else invites
  argument, and it matches the ecosystem (New Shoes, GeneralsGameCode, OpenSAGE).
- **Never redistribute retail assets.** Art, audio, video, maps and INI data are not covered by the
  source release. Require the user's own installation — exactly as OpenMW, OpenRCT2, DevilutionX,
  Ship of Harkinian and New Shoes all do. This is non-negotiable and shapes onboarding from day one.
- **EA's release carries "additional terms" beyond GPL v3.** I've confirmed those terms exist but have
  **not** read them. Read `LICENSE.md` in the EA repo yourself before writing a line of code —
  particularly anything about trademarks and commercial use. Don't take my word or anyone else's.
- **Note the contamination asymmetry.** Clean-room projects (OpenSAGE, Thyme) deliberately avoided the
  original code; now that GPL source exists, that purity is a liability rather than an asset for
  them, and the source-derived path is strictly better positioned. It's also why "merge OpenSAGE's C#
  into the C++ effort" was dismissed as impractical in the community threads.
- **Don't use the marks.** Not "Command & Conquer", not "Generals", not faction logos. Pick a codename
  now so you never have to rename later.

---

## 10. How I'd actually start — the first two weeks

1. **Read EA's `LICENSE.md` and its additional terms.** Everything else is contingent on it. You, not
   an agent.
2. **Clone `TheSuperHackers/GeneralsGameCode`** (and look at `fbraz3/GeneralsX` for the 64-bit head
   start), get it building on your Windows 11 box, run a replay. **Time the edit→build→replay loop.**
   That number *is* the Path D feasibility answer — if it's 20 minutes, agent-driven work will be
   painful and you'll need to invest in build partitioning early.
3. **Play a 3v3 on the current build with friends.** Confirm the mismatch pain is still the pain. If
   it isn't, most of this document's motivation evaporates and Path A wins.
4. **Try New Shoes.** Decide honestly whether it already gives you what you wanted.
5. **Then spike the oracle — one agent-driven task**, and nothing else: *"instrument `GameLogic` to
   dump per-entity state per frame to a binary trace; write a tool that diffs two traces and reports
   the first divergence as entity + field + frame; prove it by injecting a deliberate one-bit change."*
6. **Then spike one substitution end to end** — pick something tiny and determinism-free (the INI
   tokenizer, or audio device init): carve the C ABI, reimplement in Rust, link it, prove the traces
   are byte-identical.

Steps 5 and 6 together are maybe two weeks and they answer the real question — *can an agent do this
work with a mechanical verdict on each attempt?* — far better than any further planning, this
document included. Don't hand an agent "rewrite Generals". Hand it the oracle spike.

---

## 11. Open questions for you

1. **Is "not C++" a preference or a hard constraint?** This is *the* decision. A preference → Path D,
   and you'll write Rust/TS while a shrinking C++ build sits underneath for a couple of years. A hard
   constraint → Path C and its desert, or Path A and making peace. Everything else follows from this.
2. **Given New Shoes exists, does the browser target still motivate you** — or was it a proxy for
   "accessible, no install, works everywhere"? If the latter, every path here delivers it.
3. **Rust: yes or no?** On Path D it's close to load-bearing (§5), and it's expensive to reverse.
4. **Solo, or upstream?** The community is actively asking people not to start a ninth fork. Landing
   the instrumentation and bisector *upstream* costs you nothing, helps everyone, and is the cheapest
   possible way to find out whether collaborators exist. I'd do that regardless of which path you pick.
5. **What's the actual goal** — "a better Generals that people play" (Path A/D) or "a codebase I enjoy
   owning and can drive with agents" (Path D/C)? Both legitimate; not the same project.
6. **"the bite step"** — what did you mean? (§2)

---

### Sources

**This game:**
[electronicarts/CnC_Generals_Zero_Hour](https://github.com/electronicarts/CnC_Generals_Zero_Hour) ·
[TheSuperHackers/GeneralsGameCode](https://github.com/TheSuperHackers/GeneralsGameCode) ·
[mismatch mechanism analysis (#369)](https://github.com/TheSuperHackers/GeneralsGameCode/discussions/369) ·
[replay testing wiki](https://github.com/TheSuperHackers/GeneralsGameCode/wiki/replay_testing) ·
[unification call (#266)](https://github.com/TheSuperHackers/GeneralsGameCode/discussions/266) ·
[fbraz3/GeneralsX](https://github.com/fbraz3/GeneralsX) ·
[Project New Shoes](https://newshoes.gg/) *(claims from the project's own site; not independently verified)* ·
[Thyme](https://github.com/TheAssemblyArmada/Thyme) + [calling original functions](https://github.com/TheAssemblyArmada/Thyme-Wiki/blob/master/Calling-Original-Functions.md) ·
[OpenSAGE](https://github.com/OpenSAGE/OpenSAGE) ·
[EA announcement](https://www.ea.com/games/command-and-conquer/command-and-conquer-remastered/news/steam-workshop-support)

**Prior art:**
[OpenRCT2](https://github.com/OpenRCT2/OpenRCT2) ·
[DevilutionX](https://github.com/diasurgical/devilutionx) + [devilution](https://github.com/galaxyhaxz/devilution) ·
[Ship of Harkinian](https://en.wikipedia.org/wiki/Ship_of_Harkinian) ·
[C&C Remastered Collection](https://en.wikipedia.org/wiki/Command_%26_Conquer_Remastered_Collection) ·
[OpenRA development goals](https://github.com/OpenRA/OpenRA/wiki/Development-Goals) ·
[OpenXcom history](https://openxcom.org/2014/09/a-little-history/) ·
[OpenTTD](https://en.wikipedia.org/wiki/OpenTTD) · [OpenMW](https://en.wikipedia.org/wiki/OpenMW)

**Technique:**
[1500 Archers on a 28.8](https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond) ·
[Factorio: Desynchronization](https://wiki.factorio.com/Desynchronization) + [FFF #188](https://factorio.com/blog/post/fff-188) ·
[objdiff](https://github.com/encounter/objdiff) · [decomp.me](https://decomp.me/faq) ·
[LLM-in-the-loop matching decompilation](https://macabeus.medium.com/development-journey-on-game-decompilation-using-ai-part-3-a0a322e0d274)
