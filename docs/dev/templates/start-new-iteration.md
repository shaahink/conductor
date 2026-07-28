# Start a New Conductor Iteration — Copy & Use Template

## Before you start

- [ ] Build the conductor: `dotnet build Conductor.slnx`
- [ ] Decide the iteration name (e.g. `"Payment-Redesign"`)
- [ ] Decide stage IDs and titles (e.g. `P0`, `P1`, `P2`…)
- [ ] Decide per-stage checkpoints
- [ ] Have your repo path ready (e.g. `C:/Code/MyProject`)

---

## Step 1 — Create the plan JSON

Copy `conductor.plan.json` into your repo root and edit:

```json
{
  "version": "1.0",
  "planVersion": 1,
  "name": "MyIteration",
  "repo": "C:/Code/MyProject",
  "tracker": "TRACKER.md",
  "planDoc": "docs/PLAN.md",
  "branchPattern": "^feat/",
  "pauseOnBlocked": true,
  "batteryCollapse": true,

  "readOrder": [
    "TRACKER.md",
    "docs/PLAN.md"
  ],

  "agent": {
    "command": "opencode",
    "args": ["run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--thinking", "--format", "json", "{prompt}"],
    "resumeArgs": ["run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--thinking", "--format", "json", "--continue", "{prompt}"],
    "provider": "opencode"
  },

  "advisor": {
    "enabled": true,
    "command": "opencode",
    "args": ["run", "{prompt}", "-m", "deepseek/deepseek-v4-pro"],
    "output": "text",
    "timeoutMinutes": 6
  },

  "statusAgent": {
    "enabled": true,
    "command": "opencode",
    "args": ["run", "{prompt}", "-m", "deepseek/deepseek-v4-pro"],
    "timeoutMinutes": 5,
    "maxPerHour": 12
  },

  "setup": { "command": "dotnet build-server shutdown; exit 0", "timeoutMinutes": 2 },
  "teardown": { "command": "dotnet build-server shutdown; exit 0", "timeoutMinutes": 2 },

  "stages": [
    { "id": "P0", "title": "Land the tree",        "sessions": 2, "notes": "Set up scaffolding, patterns, and first passing gate." },
    { "id": "P1", "title": "Core domain logic",     "sessions": 3, "notes": "Implement the primary feature logic." },
    { "id": "P2", "title": "Integration & testing", "sessions": 2, "notes": "Wire up to real infrastructure, add integration tests." },
    { "id": "P3", "title": "Audit & handover",      "sessions": 1, "notes": "Final audit session — writes handover document." }
  ],

  "gatePolicy": "perPhase",

  "gates": [
    { "name": "build",  "command": "dotnet build --nologo",               "tier": "fast", "timeoutMinutes": 10 },
    { "name": "tests",  "command": "dotnet test --no-build --nologo",      "tier": "fast", "timeoutMinutes": 20 },
    { "name": "lint",   "command": "dotnet format --verify-no-changes",    "timeoutMinutes": 5 }
  ],

  "audit": { "enabled": true, "maxAttempts": 1, "enableParallel": true },

  "limits": {
    "stallMinutes": 12,
    "sessionTimeoutMinutes": 180,
    "maxResumesPerSession": 2,
    "stageSlackFactor": 2,
    "backoffMinutes": 30,
    "maxBackoffs": 5,
    "maxSessionTokens": 2000000,
    "stallPatternTermination": true,
    "stallBackoffMinutes": 12
  },

  "report": { "commit": true, "push": true, "heartbeatMinutes": 0 },

  "promptExtra": "",

  "batteries": {
    "lessons": true,
    "recentFailure": true,
    "lessonsMaxEntries": 3,
    "maxBytes": 2048
  }
}
```

---

## Step 2 — Create the tracker

Copy `TRACKER.md` into your repo root and edit:

```markdown
# MyIteration — Tracker

**Read order for a fresh session:** this file → `docs/PLAN.md`.

## Handoff  (overwrite this block, ≤12 lines, no history)
last: (none) — scaffolded.
stage: **P0 NOT STARTED**.
gate: not yet run.
next: **P0.1** — first checkpoint.

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| P0.1 | Set up project structure and first passing gate | TODO | | |
| P0.2 | Define core interfaces and contracts | TODO | | |
| P1.1 | Implement primary domain logic | TODO | | |
| P1.2 | Add unit tests for core logic | TODO | | |
| P1.3 | Error handling and edge cases | TODO | | |
| P2.1 | Wire up infrastructure adapters | TODO | | |
| P2.2 | Integration tests pass | TODO | | |
| P3.1 | Audit all P0-P2 checkpoints | TODO | | |
```

---

## Step 3 — Dry-run

```powershell
conductor run --dry-run -p conductor.plan.json
```

Verify:
- [ ] The prompt mentions the correct stage (`P0`)
- [ ] The read order files are listed
- [ ] Template variables resolved correctly
- [ ] Gate commands look right

---

## Step 4 — First session (supervised)

```powershell
conductor run --once -p conductor.plan.json
```

Watch:
- [ ] Dashboard renders correctly
- [ ] Agent starts, reads files, works
- [ ] If stalls → `conductor kill --yes -p conductor.plan.json`
- [ ] If wrong direction → `conductor abort --yes -p conductor.plan.json`
- [ ] On success: gates pass, checkpoint flips DONE
- [ ] `git log` shows the commit(s)
- [ ] `.conductor/REPORT.md` is written

---

## Step 5 — Full autonomous run

```powershell
conductor run -p conductor.plan.json
```

In another terminal, monitor:

```powershell
# Check status every few stages
conductor status -p conductor.plan.json

# Jump in if needed
conductor kill --yes -p conductor.plan.json
conductor inject "prefer the factory pattern" -p conductor.plan.json
conductor skip --yes -p conductor.plan.json   # if a stage is stuck
```

---

## Step 6 — Iteration complete

When all checkpoints are DONE and the final audit passes:

- [ ] Read the handover: `.conductor/handovers/P3.md`
- [ ] File any follow-up issues
- [ ] Tag the iteration: `git tag iter/myiteration-done`
- [ ] Decide: next iteration or done

---

## Common patterns

### OWNER-PENDING (non-blocking human step)

```markdown
| P1.2 | Add unit tests | DONE (OWNER-PENDING: run live to verify) | abc1234 | docs/evidence/ |
```

The checkpoint auto-promotes. The human handles the verification later.

### Stage-specific persona

```json
{ "id": "P2", "title": "Integration", "sessions": 2, "persona": "qa" }
```

Available personas: `architect`, `planner`, `qa`, `docs`, `reviewer`, `refactor`, `test-writer`, `git-cleanup`, `security-audit`

### Queued instructions mid-stage

```powershell
conductor inject "use Decimal for all money amounts" -p conductor.plan.json
conductor inject "read docs/DECISIONS.md before implementing" -p conductor.plan.json
```

Appears in the next session's prompt. Chain them: each `inject` creates a linked
`.conductor/queue/NNN-slug.json` file.

### Stage dependency ordering

```json
{ "id": "P2", "title": "Integration", "dependsOn": ["P0", "P1"] }
```

Conductor won't select `P2` until `P0` and `P1` are both confirmed.

### Hierarchical stages (plan tree grouping)

```json
{ "id": "P1", "title": "Core domain", "sessions": 0 },
{ "id": "P1.1", "title": "Entity design", "parentId": "P1", "sessions": 1 },
{ "id": "P1.2", "title": "Repository", "parentId": "P1", "sessions": 1 }
```

The plan tree shows `P1` as a collapsible parent with `P1.1` and `P1.2` nested under it.

### Per-stage pre-hook (blocking)

```json
{
  "id": "P2",
  "title": "Integration",
  "preHook": { "command": "docker compose up -d", "timeoutMinutes": 5 }
}
```

Fails the stage if Docker isn't running. Runs once per stage.

### Non-rectangular checklist (Shamshir-style)

If your checkpoint IDs contain hyphens (`P-0`) or multiple dots with letters
(`P3.4b`, `F5`), override the conventions:

```json
"conventions": {
  "stageIdPattern": "(?<stage>[A-Za-z]+-?\\d+)(?:\\.\\d+)?[a-z]?",
  "status": { "inProgress": ["IN PROGRESS"] }
}
```
