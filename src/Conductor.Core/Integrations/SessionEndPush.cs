namespace Conductor.Core.Integrations;

/// <summary>K5.2: everything the session-end push needs, in one argument, because the five defects
/// it fixes were all "the message could not see the record". The number is the RECORD's, so a push
/// that lands late cannot disagree with itself; the commits and claims are what K1.1 records even on
/// the rollover path; and the result arrives WHOLE — the notifier bounds it once, rather than the
/// caller cutting a paragraph the notifier then cuts again.
/// <para>Its own file since K5.3: it arrived alongside the evidence push and took
/// <c>TelegramService.cs</c> past the architecture ratchet's type ceiling.</para></summary>
/// <param name="Number">The session's own number, not the live counter.</param>
/// <param name="Stage">Stage id; the title is looked up from the plan.</param>
/// <param name="IsRollover">A rollover defers its gates by design and burns no attempt.</param>
/// <param name="CommitShas">K5.4: the work commits themselves, so the landed line can be LINKS. The
/// count alone was all the push carried, and a count is not something an owner can open.</param>
/// <param name="Duration">K5.4: how long the session ran. The completion push is asked to lead with
/// it and the record has always known it; nothing rendered it.</param>
public sealed record SessionEndPush(
    int Number,
    string Stage,
    string Outcome,
    string? GateSummary,
    string? ResultSummary,
    decimal? CostUsd,
    decimal? Score,
    int Commits,
    IReadOnlyList<string> NewlyDone,
    bool IsRollover,
    IReadOnlyList<string>? CommitShas = null,
    TimeSpan? Duration = null);

/// <summary>K5.4 — the run-complete push. It used to be one line of prose built at the call site:
/// the plan name twice, the repo path, and the engine build string given more room than anything the
/// run had actually done. The spec's ask is the opposite ordering — outcome, cost, checkpoint count,
/// duration, and a link to the report — so the facts are handed over as facts and the composition
/// happens in one place with every other push's.</summary>
/// <param name="Sessions">How many sessions the run took.</param>
/// <param name="CheckpointsDone">Checkpoints done, of <paramref name="CheckpointsTotal"/>.</param>
/// <param name="Duration">Wall-clock from the run's first session to now, when it can be derived.</param>
/// <param name="SkippedStages">Stages retired without being delivered — a completion that is not a
/// clean sweep must say so, because "COMPLETE" over three skipped stages is a lie of omission.</param>
public sealed record RunCompletePush(
    int Sessions,
    int CheckpointsDone,
    int CheckpointsTotal,
    TimeSpan? Duration,
    IReadOnlyList<string> SkippedStages);
