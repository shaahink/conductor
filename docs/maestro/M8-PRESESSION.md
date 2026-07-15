# M8 — "AFK & smart setup" Pre-Session Brief

**Design authority:** `docs/MAESTRO-PLAN.md` §M8 \
**Tracker:** `MAESTRO-TRACKER.md` (M7 DONE — 26/30) \
**Branch:** `feat/foreman` | **Predecessor:** M7 (2/2 DONE)

---

## 0. Executive summary

M8 is about walking away from the keyboard with confidence. Two checkpoints:

- **M8.1 `conductor doctor` (< 2s):** one command that says *exactly* what is missing before a run —
  agent CLI present, model reachable, node/Face built, git clean, disk, DNS, budget — and how to fix each.
- **M8.2 Telegram v2, phone-driven:** the Telegram surface already exists on paper; drive it end-to-end
  from a phone and fix what bleeds. Session-end one-liner with score; NeedsHuman with inline buttons;
  reply-to-inject; `/status` from the database.

*Truth gates (from the design doc):* M8.1 — doctor returns in < 2s and its "what's missing" list is
correct against a deliberately-broken environment. M8.2 — a toy run driven to completion from the phone,
laptop lid closed.

---

## 1. What already exists (do NOT rebuild)

| Surface | Where | State |
|---|---|---|
| `conductor doctor` | `src/Conductor/Commands/DoctorCommand.cs` | **Exists but is the WRONG doctor.** It prints "what will happen on resume" (pending fix/resume/gate/audit) and — critically — reads `state.json` via `RunState.LoadOrNew`. M2 deleted `state.json`; run.db is authoritative. So today's `doctor` shows stale/empty state. **M8.1 must repurpose this into the health-check doctor** (or add a `--check` mode) that reads run.db the way `conductor status` does (`StatusReportBuilder` → `RunStateProjection`/`SnapshotBuilder`). |
| Telegram v2 | `src/Conductor/Core/Integrations/TelegramService.*.cs` (Callback, Commands, Messages, Dto, Dto2) | Substantial surface already built (B6.1). M8.2 is **drive + fix**, not build. Tests: `B6_1TelegramTests.cs`. |
| `conductor status` (< 1s from DB) | `StatusReportBuilder` (M5.6) | The template for "fast, from the database, under a second." M8.1's doctor should reuse this read path, not re-parse files. |
| Preflight health checks | `PreflightHealth` (used in `RunLoop`) — DNS, budget, cost ceiling | Doctor can reuse these building blocks for the "reachable / budget" checks instead of duplicating them. |
| `FaceLauncher.ResolveEntrypoint()` | `src/Conductor/Core/Face/FaceLauncher.cs` | Post-M7 this resolves the **face-go binary** (`conductor-face(.exe)`). Doctor's "Face built?" check should call this — null means "run `go build` in face-go/". |

## 2. The M7 surfaces M8 builds on

M7 landed the knowledge spine — reuse it, don't reinvent:
- **Bugs are first-class now.** When M8 (or its dogfood) finds a defect, `conductor bug new "<title>"` files
  it into run.db; it surfaces in the face-go `k` Knowledge tab and injects into later prompts. Use it.
- **The ledger compounds.** `conductor note` during a session is injected into the next prompt (`LedgerBattery`).
- **Control plane is the Face's only contract.** New read surfaces (`/ledger`, `/bugs`) followed the same
  pattern: DTO file → `ControlPlaneJsonContext` → route in `ControlPlaneServer.cs` → wire test in
  `ControlPlaneServerTests.cs` → face-go `api` DTO + fetch + tab. Copy that shape for any M8 endpoint.

## 3. Known issues to fix in M8 (or before M9)

1. **`PromptBuilder.Verify` NRE (pre-existing, real).** A Verify session queued with a null
   `PendingVerify` throws `NullReferenceException` in `PromptBuilder.Verify` → the run crashes after the
   deliver session. Repro: a toy plan on the default `deliver-verify` workflow (a `docs-only` workflow
   sidesteps it, which is how the M7 smoke got green). **File it with `conductor bug new` and fix before
   the M9 dogfood** — a crashing verify path will bleed in a real end-to-end run.
2. **`doctor` reads `state.json`.** See §1 — a leftover from before M2. Fold it onto the run.db read path.

## 4. Suggested plan of attack

**M8.1 — doctor**
1. Decide: rename the current resume-preview doctor (e.g. `conductor plan-preview` / fold into `status`)
   or add a `conductor doctor --check` health mode. Owner call — surface it if unsure.
2. Build the check battery (each returns ok/warn/fail + a one-line fix): agent CLI on PATH; model/endpoint
   reachable (reuse `PreflightHealth`); face-go binary resolvable (`FaceLauncher.ResolveEntrypoint`); git
   clean + branch; free disk; DNS; budget headroom vs `MaxRunCostUsd`.
3. Keep it < 2s: no LLM call, no network beyond a cheap reachability probe; read run.db, not files.
4. Truth gate: break the environment on purpose (rename the agent binary, dirty the tree) and assert the
   exact failing lines.

**M8.2 — Telegram v2**
1. Wire a real bot token (owner-provided — this is a `HUMAN:` item, credential-gated).
2. Drive a toy run (the M7 smoke harness in scratch is a good base) and exercise: session-end one-liner,
   NeedsHuman inline buttons, reply-to-inject, `/status`.
3. Fix what bleeds; file bugs with `conductor bug new` as you go.

## 5. Read order for the M8 session

1. `MAESTRO-TRACKER.md` handoff block (live state).
2. `docs/MAESTRO-PLAN.md` §M8 (design authority) + §4 "the rules every session obeys".
3. This brief.
4. `AGENTS.md` → "How to verify a face-go change" + "Dogfood recipe" (no-LLM verification).

## 6. Gate for M8 (match M7's bar)

`dotnet build` 0w/0e · full C# suite green · architecture ratchet green (split any file that crosses 500
lines / 3 types — that is how M7's `ControlPlaneServer.Endpoints.cs` and `PromptBattery.Batteries.cs` were
kept honest) · face-go build/vet/test green · a real (fake-agent) dogfood exercising the new surface.
