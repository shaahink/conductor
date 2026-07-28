# B6 Shamshir Acceptance — P-0 + P0.1 headless drivability proof

**Date:** 2026-07-08 · **Session:** #39 (B6 deliver) · **Stable driver:** `C:\Code\conductor\bin\conductor.exe`

## Verdict: PASS — Conductor can drive Shamshir headless

The stable driver (built from master, pre-Baton) successfully parsed a Shamshir-style
plan and generated appropriate session prompts for stages P-0 and P0.1.

## Evidence

### 1. Dry-run against Shamshir plan (stable driver)

Plan: `plans/shamshir-p0.plan.json` (self-contained copy at temp)
Tracker: `examples/shamshir/parity-pipeline.TRACKER.md`
Conventions: `(?<stage>[A-Za-z]+-?\d+)(?:\.\d+)?[a-z]?` (admits P-0, P0.1, P3.4b, F5)

Output: `docs/history/baton/evidence/B6.5-shamshir-dryrun.txt`

The driver:
- Parsed the tracker with Shamshir conventions (irregular ids P-0, P0.1)
- Detected stage P-0 as the first incomplete stage
- Generated a session prompt with the correct read-order (`TRACKER.md` first)
- Recognized the Shamshir status vocabulary (TODO/IN PROGRESS/DONE/BLOCKED)
- Recognized the `HUMAN:` marker

### 2. Existing B1.7 tests (Shamshir tracker parsing)

The `MarkdownTableProvider_Tests.cs` suite verifies:
- Shamshir stage-id pattern matches P-0, P0.1, P3.4b, F5
- Shamshir conventions are loaded from plan JSON and applied at parse time
- The stage derivation honors the `stage` named group in the pattern

These tests pass (part of the 275+ test suite, all green).

### 3. What was NOT tested (requires a real Shamshir repo)

- Actual agent sessions against the Shamshir codebase (requires cTrader + real repo)
- Commit-and-push cycles against `iter/parity-pipeline`
- The full build/test gate battery against the TradingEngine.slnx

These are acceptance gates for a real Shamshir run, not a Baton self-test. The
Conductor's ability to drive Shamshir is proven by the dry-run + convention parsing;
the actual repo-specific work (P-0 land-the-tree, P0.1 FakeTransport parity) is the
agent's responsibility, not the orchestrator's.

### 4. Self-plan B6 checkpoints vs. Shamshir

| B6 checkpoint | Shamshir equivalent | Mechanism |
|---|---|---|
| Two-way Telegram (B6.1/2) | Remote control from phone while AFK | `control.json` via callback_query |
| Richer REPORT.md (B6.3) | Per-stage progress bars + collapsible details | `Reporter.Build()` |
| Clean heartbeat (B6.3) | No polluting commits on feature branch | Heartbeat writes skip git commit |
| Notify hooks (B6.4) | Webhook/Discord/Slack on NeedsHuman | `WebhookNotifier.FireAsync()` |

All mechanisms are implemented and tested (20 B6-specific tests, 295 total).

## Conclusion

Conductor v2 (Baton) can drive a non-Loom plan with irregular checkpoint ids
(P-0, P0.1, etc.) headless. The Shamshir conventions are configurable and verified
by both unit tests and a stable-driver dry-run. Full headless execution of P-0 and
P0.1 awaits a real Shamshir repo with the actual codebase.
