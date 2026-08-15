namespace Conductor.Core.Integrations.Github;

/// <summary>KS9.1 — what ONE checkpoint should look like on the mirror. Produced by
/// <see cref="GithubBoardPlan"/> from the fold alone; consumed by <see cref="GithubBoardSync"/>,
/// which is the only thing that knows an HTTP verb.</summary>
/// <param name="TaskId">The graph's task id — the identity, carried in the body as a marker.</param>
/// <param name="Retired">The card left the declared plan. It is closed and LABELLED, never deleted:
/// a deleted issue takes its history with it, and the history is the reason to mirror at all.</param>
public sealed record GithubCard(
    string TaskId,
    string Title,
    string Body,
    List<string> Labels,
    string Stage,
    bool Closed,
    bool Retired = false);

/// <summary>One session's line in the run diary. The key is the session marker, which is what makes
/// "one comment per SessionFinished" survive a second backfill.</summary>
public sealed record GithubDiaryComment(string Key, string Body);

/// <summary>KS9.1 — the run's diary as one issue plus its comments.</summary>
public sealed record GithubDiary(
    string RunId,
    string Title,
    string Body,
    bool Closed,
    List<GithubDiaryComment> Comments);
