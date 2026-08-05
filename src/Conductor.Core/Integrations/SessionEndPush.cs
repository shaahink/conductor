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
    bool IsRollover);
