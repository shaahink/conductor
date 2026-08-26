# DV5.2 — the cloud lane, behind a flag, default off

Delivered 2026-08-26. Companion to [`dv5.1-cloud-flags.md`](dv5.1-cloud-flags.md) and
[`dv5.1-live-proof.md`](dv5.1-live-proof.md), whose measurements this checkpoint is built on.

## 1. Flag verification first (trap 16)

The card's premise was that the engine would spawn `claude --cloud` for a lane and **consume the
branch the session pushes**. DV5.1 measured that `--cloud` refuses every non-interactive invocation
— it cannot be combined with `--print`, `--bg` is "a different backend", and without a TTY it
refuses outright — so an engine cannot start a cloud session at all, and there is no branch to
consume. The card was amended rather than argued with.

One cloud surface on this CLI *is* headless, and it is exactly the CL-1 shape. Measured the same day
by running it with stdout piped in a scratch repo:

```
$ claude ultrareview --json --timeout 1
Ultrareview could not launch: No changes to review: the diff against origin/main (merge-base
bff6d5d) is empty. If you have local edits, stage or commit them first. If your branch was already
merged or you meant a different base, pass one explicitly, e.g. `claude ultrareview <branch>`.
```

It did not ask for a terminal — it validated and refused **by name**. From `claude ultrareview
--help`: *"Run a cloud-hosted multi-agent code review of the current branch (or a PR number / base
branch) and print the findings"*, with `--json`, `--post` / `--no-post` (default no-post) and
`--timeout <minutes>` (default 30).

Two facts that refusal decides, and that the implementation follows rather than guesses:

* **A dirty tree is refused by the CLI itself** ("stage or commit them first"), so it is a real gate.
* **It bundles the local branch** — the no-arg form needs no reachable GitHub remote — so requiring a
  *pushed* branch would be a gate stricter than the thing it guards, refusing work that would have
  succeeded. That is the same mistake DV5.1 recorded about inventing a stricter session id than the
  CLI's, and `CloudLane.Blocks` is deliberately narrower than `/cloud`'s preflight because of it.

**No real review was launched.** §2.4 item 5: a cloud session buys no extra capacity and drains the
same Max pool the local run needs — and the local run here is this session.

## 2. What shipped, and where each clause is pinned

| acceptance clause | where it is enforced | test |
| --- | --- | --- |
| behind a flag, **default off** | `CloudLaneConfig.Enabled` is `false`; `PlanConfig.Cloud` is null in every plan that says nothing; `LaneCoordinator.StartCloudReviewLane` returns before a pool, a preflight or a process exists | `The_lane_is_off_by_default_…`, `A_disabled_lane_never_reaches_the_process_seam`, `A_disabled_lane_does_not_even_measure_the_repo` |
| only work needing **no conductor tools** | the lane runs `ultrareview`, which has no control-plane reach | — |
| **no verdict**; every gate re-runs locally; the referee never moves | source rule over the *whole* `Integrations/Cloud` namespace forbidding `VerdictEngine`, `SessionVerdict`, `VerdictDisposition`, `GateOrchestrator`, `GateRunner`, `GateResult`, `IRunStore`, `SqliteRunStore`, `IEventSink`, `TaskWrites` — plus a staleness check so the list cannot rot into a rule that forbids nothing | `ArchitectureBoundaryTests.TheCloudLaneNeverReachesTheReferee` |
| cost recorded and reported as **unknown**, never zero | `CloudLaneResult.Cost` is the word; `.Spend` is always null, so `RunSpendLedger.Record(null, …)` takes its "unknown, not zero" branch and writes **no** cost row | `Every_outcome_prices_the_lane_as_unknown_and_none_of_them_prints_a_zero` (walks all five outcomes), `A_lane_with_no_receipt_is_logged_as_unknown_and_writes_no_cost_row` |
| droppable without losing DV5.1 | the lane is additive: `/cloud` does not reference it, and `plan.cloud` absent is byte-identical behaviour | DV5.1's 26 tests are untouched and still green |

Two things were added that the card did not ask for and the measurement did:

* **`--no-post` is passed explicitly** on every invocation although it is the CLI's own default. A
  lane the *engine* spawns must never write a comment on a pull request as the owner, and relying on
  a research preview's default for that is one release note away from being wrong.
* **The payload is stored whole and never parsed.** DV5.1 already paid for guessing a shape this
  engine had not observed; a review summarised by a parser that misread it reads as a conclusion,
  which is worse than one nobody summarised. `The_review_is_stored_byte_for_byte_…` asserts the
  artifact is byte-identical and that nothing from inside it appears in the summary.

## 2a. What the card asked for that does not exist here

The card also said the lane "records id and URL, pushes them to the chat". A review has neither: it
is a command that returns findings, not a session with an id and a page. Nothing is invented to fill
the field — the artifact path is what the run logs, and `LaneResult.ArtifactPath` is what carries it,
the same way an analysis lane's artifact is carried today. The id-and-URL half belongs to `/cloud`
(DV5.1), where the owner supplies the id because only a person with a terminal can create one.

The lane also fires **once per session**, alongside the analysis lanes. That is stated in
`docs/plan-config.md` and in the method's own doc comment rather than left to be discovered, because
with no meter on the other side "a cloud review per session" is the number an owner needs before
turning the flag on for a long run.

## 3. The honesty rule, in the run's own words

The lane hands the ledger nothing, and the ledger — which already had this branch, from KS5.2 —
says so out loud instead of writing a row:

```
Record(null) returned False
  cloud lane 'cloud-review': the provider reported no billed figure — not recorded (unknown, not zero)
```

`RecordLaneSpend` was made kind-aware so the line says *cloud lane* rather than *analysis lane*: the
two are priced by completely different rules, one has a receipt and the other never can, and a log
that calls them the same thing is where a $0.00 would eventually come from.

## 4. Live proof

Driven through the FRESH build as a tracked `conductor bg` child against a scratch repo under the
temp directory. Full transcript: [`dv5.2-live-proof.log`](dv5.2-live-proof.log).

```
=== the flag is off: nothing is spawned, nothing is measured ===
outcome=Disabled spawned=False cost=unknown spend=null
  the cloud lane is off (plan.cloud.enabled is not set); nothing was sent anywhere.

=== enabled: the REAL claude binary, real argv ===
argv: claude ultrareview --no-post --timeout 1
outcome=Failed spawned=True cost=unknown spend=null
  cloud lane failed (exit 1): Ultrareview could not launch: No changes to review: the diff against
  origin/main (merge-base bff6d5d) is empty. If you have local edits, stage or commit them first...
  Cost: unknown.

=== the ledger's own sentence for a lane with no receipt ===
Record(null) returned False
  cloud lane 'cloud-review': the provider reported no billed figure - not recorded (unknown, not zero)
```

Four things this establishes that no synthetic test can:

1. The off path constructs nothing and measures nothing — the preflight in that rig throws if called.
2. The real argv is accepted by the real binary. **`--no-post` did not draw an "unknown option"**,
   which is the only way to know the explicit refusal-to-post is actually in force.
3. A failing cloud lane still prices itself `unknown`, and hands the ledger nothing (`spend=null`).
4. The ledger's own sentence is what the run records — not a number, and not silence.

No review was launched: the scratch branch was level with its remote, so the CLI refused before
reaching the cloud, which is exactly the cheap probe this needed.

## 5. Suite

See section 6 of this file's sibling for DV5.1. For DV5.2: `DV5_2CloudLaneTests` plus `DV5_1` and
both architecture classes, and the schema/docs classes that a new plan block trips
(`SF7_1DocsMatchRealityTests`, `KS3_3SchemaHonestyTests`) — all green. Full-suite result is recorded
in the commit message.

The `cloud` block is documented in `docs/plan-config.md`, which is what
`PlanConfigDocDocumentsEveryKeyThePlanSchemaDeclares` demanded: it went red on the new block before
the section existed, which is the seeded-red proof that the doc rule is live.
