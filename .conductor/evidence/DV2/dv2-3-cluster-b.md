# DV2.3 — cluster B, channels: the four defects, and what proves each one

Stage DV2, checkpoint DV2.3. Session #4, 2026-08-25.
Source fixes: `103e387` (previous session, part-built) and `2b37a01` (this session).
Tests: `tests/Conductor.Tests/DV2_3ChannelDefectTests.cs`, `tests/Conductor.Tests/DV2_3FailureReasonTests.cs`.

Every Telegram assertion is made against a REAL `TelegramService` talking over a loopback socket to
`RecordingBotApi`, the stub standing in for api.telegram.org — the `TelegramConfig.ApiBaseUrl` seam.
No real credential is reachable: `TestEnvironmentIsolation` clears `CONDUCTOR_TELEGRAM_TOKEN` for the
whole test process, and each fixture writes its own scratch token (`dv23-scratch-token`) into its own
temp state dir. Nothing in this checkpoint polls, or could poll, a real bot token.

## The four rows, and the file and line each fix lives at

| bug | defect | fix | test |
|---|---|---|---|
| #64 | the startup line counted the RAW `allowedChatIds`, so a `chats`-block plan was told it would deliver nothing while delivering perfectly | `src/Conductor.Core/Integrations/TelegramService.Lifecycle.cs:59` — `_cfg!.ChatCount` | `Started_line_counts_the_resolved_chats_not_the_raw_allow_list`, plus the negative control |
| #65 | the same raw-versus-resolved read in the test endpoint: "there is no chat to send it to" on a bot that reaches two chats | `src/Conductor.Core/Integrations/TelegramService.cs:241-247` — `Targets`, admin first | `Test_connection_sends_to_the_resolved_admin_chat_on_a_chats_only_plan`, plus the SC1.1 control |
| #38 | a getUpdates 409 was thrown away by `EnsureSuccessStatusCode`; the loop logged a generic transport warning every interval and never named the other consumer | `src/Conductor.Core/Integrations/TelegramService.Polling.cs` — 409 checked BEFORE the throw, `TgResponse.Description` read, linear capped backoff | `A_getUpdates_conflict_names_the_other_consumer_and_is_loud_exactly_once`, `A_conflict_with_an_unreadable_body_still_says_what_a_409_means`, `Conflict_backoff_is_five_seconds_per_streak_capped_at_a_minute` (7 cases) |
| #66 | `report push failed:` with nothing after the colon — the message quoted `Output`, and git writes its refusals to STDERR | `src/Conductor.Core/ProcessRunner.cs` — `FailureReason()`, stderr first, stdout fallback, exit code when both are empty | `A_real_failing_git_push_has_an_empty_stdout_and_a_reason_anyway` and four unit cases |

## What the strand doc got wrong, corrected here

- **#65 is not an index crash.** The strand doc filed it as `_cfg.AllowedChatIds[0]` on an empty list.
  The guard above the index is real, so it never threw; the defect is a FALSE NEGATIVE — a valid,
  deliverable plan reported as "no chat to send it to", which is what made the Face's guided setup
  uncompletable on a correct plan. The disposition stands, the failure mode was mis-stated.
- **#66's first fix was wrong, and the regression test is what found it.** `FailureReason` kept the
  LAST three non-empty lines — the `GateRunner.TailOf` convention, which is right for a build log
  because a build log ends with its error. A refused command is the other shape: it announces the
  refusal and then explains how to fix it. On the exact failure this method exists for, `git push`
  with no remote, the tail read

      git remote add <name> <url> | and then push using the remote name | git push <name>

  — every word of the advice and none of the reason. It takes the HEAD now, and the live test asserts
  against a real failing `git push` whose stdout really is empty.

## The tests pass — and, inverted, they fail

A regression test that has never been red proves nothing. Every fix was therefore inverted back to
its pre-fix behaviour in the source, the suite re-run, and the source restored (`git checkout --`,
working tree verified clean of the probe markers afterwards).

The inversions, one line each:

| file | fixed | inverted back to |
|---|---|---|
| `TelegramService.Lifecycle.cs` | `_cfg!.ChatCount` | `_cfg!.AllowedChatIds.Count` |
| `TelegramService.cs` | `var targets = Targets;` | `_cfg.AllowedChatIds.Select(i => new ChatTarget(i, ChatProfile.Admin))` |
| `TelegramService.Polling.cs` | the `HttpStatusCode.Conflict` check before `EnsureSuccessStatusCode` | removed — the throw swallows the body again |
| `ProcessRunner.cs` | `.Take(Math.Max(1, lines))` | `.TakeLast(Math.Max(1, lines))` |

    fixed source  : Passed!  - Failed:     0, Passed:    21, Skipped:     0, Total:    21, Duration: 5 s
    inverted      : Failed!  - Failed:     6, Passed:    15, Skipped:     0, Total:    21, Duration: 1 m

The six that go red under the inversion are exactly the six that pin behaviour, and each fails with
the FIELD symptom, not a generic assertion:

    DV2_3ChannelDefectTests.Started_line_counts_the_resolved_chats_not_the_raw_allow_list [FAIL]
      Assert.DoesNotContain() Failure: Filter matched in collection   <- "will deliver nothing" is back
    DV2_3ChannelDefectTests.Test_connection_sends_to_the_resolved_admin_chat_on_a_chats_only_plan [FAIL]
      test connection failed: token present but no allowedChatIds - bot is push-only to nobody
      ---- bot API calls ----                                        <- nothing was sent at all
    DV2_3ChannelDefectTests.A_getUpdates_conflict_names_the_other_consumer_and_is_loud_exactly_once [FAIL]
      the poll loop did not come back after the conflict (polls=1)   <- no backoff, no naming
    DV2_3ChannelDefectTests.A_conflict_with_an_unreadable_body_still_says_what_a_409_means [FAIL]
      no conflict line was logged
    DV2_3FailureReasonTests.A_real_failing_git_push_has_an_empty_stdout_and_a_reason_anyway [FAIL]
      FailureReason=[git remote add <name> <url> | and then push using the remote name | git push <name>]
    DV2_3FailureReasonTests.The_first_lines_are_kept_and_joined_because_a_refusal_leads_with_its_reason [FAIL]

The fifteen that stay green under the inversion are the controls and the pure cases, which is the
other half of the claim: the no-chats warning still fires, the test endpoint still refuses when there
is genuinely nobody to reach (SC1.1's fix, un-undone), `ConflictBackoff` is arithmetic, and
`FailureReason` still prefers stderr and still never returns the empty string.

Full logs: `dv2-3-tests-green.log`, `dv2-3-prefix-probe.log`, both in this directory.

## What the live 409 test actually drives

`RecordingBotApi` answers every `getUpdates` with Telegram's own conflict body, verbatim from the
wire, at HTTP 409. The fixture sets `pollIntervalSeconds: 3600`, so a SECOND poll inside the test
window cannot be the ordinary interval — it can only be the backoff bringing the loop back. What the
real engine logs, captured verbatim from the passing run
(`dotnet test --filter FullyQualifiedName~DV2_3 --logger "console;verbosity=detailed"`, lines 90-94 of
`dv2-3-detailed-run.log`, wrapped here only for width):

    ---- poll log under a 409 ----
    Information|Telegram bot started (poll interval 3600s, 2 allowed chat id(s))
    Error|Telegram getUpdates conflict: Conflict: terminated by other getUpdates request; make sure
      that only one bot instance is running - another process is polling getUpdates with this same
      bot token. Telegram allows exactly one consumer per token, so the two are stealing each other's
      updates and inbound control is unreliable for both. Stop the other conductor, or give this run
      its own bot token. Backing off 5s.
    Debug|Telegram getUpdates still conflicted (poll 2); backing off 10s.
    ---- end ----

Note the first line of that same capture: the started line, on a plan whose `allowedChatIds` is
empty, says **2 allowed chat id(s)** - that is #64's fix and #38's proof in one log.

And #66's field line, from the same run (`The_report_push_failure_line_now_carries_a_reason`, a real
`Reporter.WriteAndPublish` pushing from a repo with no remote):

    report push failed: fatal: No configured push destination. | Either specify the URL from the
    command-line or configure a remote repository using | git remote add <name> <url>

Loud exactly once per streak, quiet after, and no `Warning|Telegram poll error` line at all — that
generic warning, once per interval forever, was the whole of the old behaviour.

## Runs, in order, all in this directory

| run | filter | result | log |
|---|---|---|---|
| scoped, fixed source | `~DV2_3` | Passed 21 / Failed 0 | `dv2-3-tests-green.log` |
| scoped, INVERTED source | `~DV2_3` | Failed 6 / Passed 15 | `dv2-3-prefix-probe.log` |
| full suite, fixed source | none | **Passed 3161 / Failed 0** | `dv2-3-full-suite.log` |
| scoped, detailed | `~DV2_3` | Passed 22 / Failed 0 | `dv2-3-detailed-run.log` |

The full suite ran before the last test (`The_report_push_failure_line_now_carries_a_reason`) was
added, which is why the scoped count is 22 and the suite's is 3161 — 3140 as the previous session
left it, plus this checkpoint's 21 at that moment. Nothing was skipped, no expectation was relaxed,
and no existing test moved: `RecordingBotApi`'s three additions are all opt-in and default to the
behaviour every other suite already relies on.

## Ledger

`conductor bug fix` for #38, #64, #65 and #66 — cluster B closed, no row moved between clusters and
none dropped. Cluster C (#67, #68, #69, #71, `FU-F1-06`) is DV2.4's, untouched here.
