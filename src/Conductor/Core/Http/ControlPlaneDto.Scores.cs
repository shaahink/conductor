namespace Conductor.Core.Http;

// SF1.1: verifier scores get a real wire type. This section of the Report tab used to be a canned
// SELECT through GET /report/query — the single reason the Face still needed a SQL endpoint to
// render a report. Everything the section shows now arrives typed, and SF1.2 deleted that endpoint.

/// <summary>One verifier verdict. <c>Passed</c> and <c>Threshold</c> are computed by the ENGINE, from
/// the same expression the run judged with (the QA dial's threshold, else limits.verifierThreshold,
/// resolved per stage) — a client that re-derived "did it pass" from a hardcoded 80 would disagree
/// with the run itself on any stage that set its own dial.</summary>
public sealed record ScoreDto(
    int SessionNumber,
    string? StageId,
    int Score,
    string Verdict,
    bool Passed,
    int Threshold,
    IReadOnlyList<string> Findings);

/// <summary>Newest session first, mirroring <c>GET /sessions</c>: the verdict you care about is the
/// one that just landed.</summary>
public sealed record ScoresDto(IReadOnlyList<ScoreDto> Scores);
