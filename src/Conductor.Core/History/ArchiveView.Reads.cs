using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Core.Http;

using Microsoft.Data.Sqlite;

namespace Conductor.Core.History;

// KS2.2 — the ten read projections. Each one answers the endpoint of the same name on the live plane,
// from the archived database instead of a running engine. Where the live answer needs something only a
// live run has (a burn rate, a gate battery mid-flight, the plan file on disk), the archive says the
// empty thing rather than the last recorded thing: a finished run has no current session, and a number
// that was true in July rendered in the "now" slot is the exact class of lie KS1 spent a stage closing.
public sealed partial class ArchiveView
{
    /// <summary>The Face's whole top half: identity, progress, money, tokens and the stage rail.</summary>
    public StateDto State()
    {
        var stages = _archive.Stages(Run.RunId);
        var checkpoints = _archive.Checkpoints(Run.RunId);
        var sessions = _archive.Sessions(Run.RunId);
        var done = checkpoints.Count(c => string.Equals(c.Status, "done", StringComparison.OrdinalIgnoreCase));
        var current = stages.FirstOrDefault(s => string.Equals(s.Status, "active", StringComparison.Ordinal))
                      ?? stages.LastOrDefault();
        // "Current" for a finished run means where it stopped: the first checkpoint it never closed, or
        // the last one it did when it closed them all.
        var pending = checkpoints.FirstOrDefault(c => !string.Equals(c.Status, "done", StringComparison.OrdinalIgnoreCase))
                      ?? checkpoints.LastOrDefault();
        var last = sessions.LastOrDefault();
        var limits = Run.Limits;

        return new StateDto(
            PlanName: Run.PlanName,
            // KS1.3's reconciled word, not the stored one.
            Status: Status,
            AttentionReason: null,
            StageId: current?.Id ?? "",
            StageTitle: current?.Title ?? "",
            Persona: null,
            DoneCount: done,
            TotalCount: checkpoints.Count,
            TotalCostUsd: Run.CostUsd,
            OverheadCostUsd: CostRows
                .Where(c => !string.Equals(c.Category, "agent", StringComparison.OrdinalIgnoreCase))
                .Sum(c => c.CostUsd),
            TokensInput: CostRows.Sum(c => c.TokensIn),
            TokensOutput: CostRows.Sum(c => c.TokensOut),
            TokensReasoning: CostRows.Sum(c => c.TokensThink),
            CurrentCheckpoint: pending?.Id ?? "",
            CurrentCheckpointTitle: pending?.Title ?? "",
            // The last battery the run actually recorded. There is no live battery to report, and an
            // empty gate list beside a remembered summary is the honest pair.
            GateSummary: last?.GateSummary ?? "",
            Stages: [.. stages.Select(s => StageOf(s, checkpoints))],
            RunId: Run.RunId,
            Repo: Repo,
            PlanDir: "",
            SessionNumber: Run.Sessions,
            SessionKind: last?.Kind ?? "-",
            Attempt: last?.Attempt ?? 0,
            MaxAttempts: 0,
            SessionElapsedSec: 0,
            AgentActive: false,
            SessionCostUsd: last?.CostUsd ?? 0m,
            SessionTokensInput: 0,
            SessionTokensOutput: 0,
            SessionTokensReasoning: 0,
            Gates: [],
            MaxSessionTokensThisRun: limits?.SessionTokenCap,
            Model: Events.OfType<SessionStarted>().LastOrDefault()?.Model ?? "",
            Tracker: "",
            StateDir: StateDir,
            Provider: "",
            SessionCostBasis: LiveCostEstimator.BasisMeasured,
            CostSpent: Run.CostUsd,
            CostCap: limits?.RunCostCapUsd,
            CostRemaining: limits?.RunCostCapUsd is { } cap ? cap - Run.CostUsd : null,
            MeanSessionCost: sessions.Count > 0
                ? decimal.Round(sessions.Sum(s => s.CostUsd) / sessions.Count, 4, MidpointRounding.AwayFromZero)
                : 0m,
            CheckpointsRemaining: Math.Max(0, checkpoints.Count - done),
            WindowCostUsd: Run.CostUsd,
            LifetimeCostUsd: Run.CostUsd,
            EngineVersion: Run.EngineVersion ?? "",
            EngineCommit: Run.EngineCommit ?? "");
    }

    private static StageDto StageOf(ArchivedStage s, IReadOnlyList<ArchivedCheckpoint> checkpoints)
    {
        var mine = checkpoints.Where(c => string.Equals(c.StageId, s.Id, StringComparison.Ordinal)).ToList();
        return new StageDto(
            Id: s.Id, Title: s.Title,
            Done: mine.Count(c => string.Equals(c.Status, "done", StringComparison.OrdinalIgnoreCase)),
            Total: mine.Count,
            State: s.Status,
            Attempts: 0, LastOutcome: "", CostUsd: 0m, ParentId: null, Depth: 0,
            Checkpoints: [.. mine.Select(c => new CheckpointDto(c.Id, c.Title, c.Status))]);
    }

    /// <summary>The work graph the run ended with — the Kanban's rows.</summary>
    public TasksDto Tasks() => ControlPlaneMapper.FromTasks(LiveTasks());

    /// <summary>Every session, in the order it was lived.</summary>
    public SessionsDto Sessions()
    {
        var commitsByNumber = Events.OfType<SessionFinished>()
            .GroupBy(e => e.Number)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Last().NewCommits.Concat(g.Last().SatelliteCommits)]);
        var thinkByNumber = CostRows.GroupBy(c => c.SessionNumber)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.TokensThink));

        var rows = _archive.Sessions(Run.RunId).Select(s => new SessionRowDto(
            Number: s.Number,
            StageId: s.StageId,
            Kind: s.Kind,
            StartedUtc: s.StartedUtc ?? "",
            EndedUtc: s.EndedUtc,
            Outcome: s.Outcome,
            Attempt: s.Attempt,
            ResumeCount: s.ResumeCount,
            GateSummary: s.GateSummary,
            ResultSummary: s.ResultSummary,
            CommitCount: s.Commits,
            CostUsd: (double)s.CostUsd,
            TokensIn: CostRows.Where(c => c.SessionNumber == s.Number).Sum(c => c.TokensIn),
            TokensOut: CostRows.Where(c => c.SessionNumber == s.Number).Sum(c => c.TokensOut),
            // Same contract as the live endpoint: null is "this run's provider never reported one",
            // which is every session claude ever ran. A recorded zero would claim no thinking happened.
            TokensThink: thinkByNumber.TryGetValue(s.Number, out var think) && think > 0 ? think : null,
            TokensCache: CostRows.Where(c => c.SessionNumber == s.Number).Sum(c => c.TokensCacheRead),
            Digest: null,
            Commits: commitsByNumber.TryGetValue(s.Number, out var cs) ? cs : [])).ToList();
        return new SessionsDto(rows);
    }

    /// <summary>The run's own history, folded by the same reader the live plane uses.</summary>
    public TimelineDto Timeline() => TimelineProjection.From(Events);

    /// <summary>The knowledge ledger. Hand-edit rows are dropped exactly as the live endpoint drops
    /// them — they are the file's own bookkeeping, not knowledge the run recorded.</summary>
    public LedgerDto Ledger()
    {
        var rows = TryQuery(
            "SELECT id, session_number, stage_id, kind, content, created_at " +
            "FROM ledger WHERE run_id = @runId ORDER BY id");
        var entries = rows
            .Where(r => !string.Equals(r["kind"] as string, "hand-edit", StringComparison.Ordinal))
            .Select(r => new LedgerEntryDto(
                Convert.ToInt64(r["id"] ?? 0L, Inv), OptInt(r, "session_number"), r["stage_id"] as string,
                r["kind"] as string ?? "", r["content"] as string ?? "", r["created_at"] as string ?? ""))
            .ToList();
        return new LedgerDto(entries);
    }

    /// <summary>The bug ledger. <paramref name="status"/> null/empty means open only (the live default),
    /// <c>all</c> means every bug, anything else is that status.</summary>
    public BugsDto Bugs(string? status = null)
    {
        var filter = string.IsNullOrWhiteSpace(status) ? "open"
            : status.Equals("all", StringComparison.OrdinalIgnoreCase) ? null
            : status;
        var sql = "SELECT id, title, detail, severity, status, stage_id, found_session, fixed_session, " +
                  "created_at, updated_at FROM bugs WHERE run_id = @runId"
                  + (filter is null ? "" : " AND status = @status")
                  + " ORDER BY id DESC";
        var rows = filter is null ? TryQuery(sql) : TryQuery(sql, ("@status", filter));
        var bugs = rows.Select(r => new BugDto(
            Convert.ToInt64(r["id"] ?? 0L, Inv), r["title"] as string ?? "", r["detail"] as string,
            r["severity"] as string ?? "", r["status"] as string ?? "", r["stage_id"] as string,
            OptInt(r, "found_session"), OptInt(r, "fixed_session"),
            r["created_at"] as string ?? "", r["updated_at"] as string ?? "")).ToList();
        return new BugsDto(bugs);
    }

    /// <summary>The verifier's verdicts as the run recorded them.</summary>
    /// <remarks>The archive serves <c>Threshold: 0</c> on purpose. The bar a score was judged against
    /// lives in the PLAN (per stage, else <c>limits.verifierThreshold</c>) and no plan file is in the
    /// database — so an archive that printed 80 would be inventing the number the run was judged by.
    /// <c>Passed</c> comes from the recorded verdict instead, which is what the run actually decided;
    /// only a row with no verdict at all falls back to the same score≥80 default <c>Verifier.Parse</c>
    /// uses.</remarks>
    public ScoresDto Scores()
    {
        var rows = TryQuery(
            "SELECT session_number, stage_id, score, verdict, findings " +
            "FROM scores WHERE run_id = @runId ORDER BY session_number DESC, id DESC");
        var scores = rows.Select(r =>
        {
            var score = Convert.ToInt32(r["score"] ?? 0, Inv);
            var verdict = r["verdict"] as string;
            var findings = r["findings"] as string;
            return new ScoreDto(
                SessionNumber: Convert.ToInt32(r["session_number"] ?? 0, Inv),
                StageId: r["stage_id"] as string,
                Score: score,
                Verdict: string.IsNullOrWhiteSpace(verdict) ? (score >= 80 ? "PASS" : "FAIL") : verdict,
                Passed: string.IsNullOrWhiteSpace(verdict)
                    ? score >= 80
                    : verdict.Equals("PASS", StringComparison.OrdinalIgnoreCase),
                Threshold: 0,
                Findings: string.IsNullOrWhiteSpace(findings)
                    ? []
                    : findings.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }).ToList();
        return new ScoresDto(scores);
    }

    /// <summary>Enough for any surface — the same ceiling the live endpoint keeps.</summary>
    private const int MaxEvidenceRows = 200;

    /// <summary>The evidence registry, folded from the archived log.</summary>
    public EvidenceDto Evidence(string? checkpoint = null, int? limit = null)
    {
        var registry = EvidenceRegistry.From(Events);
        var selected = string.IsNullOrWhiteSpace(checkpoint)
            ? registry.Latest(MaxEvidenceRows)
            : [.. registry.ForCheckpoint(checkpoint.Trim()).Reverse()];
        var take = limit is > 0 ? Math.Min(limit.Value, MaxEvidenceRows) : MaxEvidenceRows;
        var rows = selected.Take(take).Select(a => new EvidenceArtifactDto(
            a.Path, a.Kind, a.CheckpointId, a.StageId, a.SessionNumber,
            a.Sha256, a.Bytes, a.CreatedUtc.ToString("O"), a.Source,
            EvidenceKinds.IsVisual(a.Kind))).ToList();
        return new EvidenceDto(rows, registry.Count);
    }

    /// <summary>The plan as far as the database remembers it: the stages it entered, the gates it ran,
    /// and the limits it recorded. Not the plan FILE — that lives in a repo this machine may no longer
    /// have, and reading it would describe today's plan while claiming to describe the run's.</summary>
    public PlanDto Plan()
    {
        var stages = _archive.Stages(Run.RunId);
        var starts = Events.OfType<SessionStarted>().ToList();
        var limits = Run.Limits;
        var gates = Events.OfType<GateFinished>()
            .Select(g => g.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new PlanGateDto(name, "", "", 0, false))
            .ToList();

        return new PlanDto(
            Name: Run.PlanName,
            PlanVersion: Events.OfType<PlanReloaded>().LastOrDefault()?.PlanVersion ?? 0,
            PlanFile: "",
            GatePolicy: "",
            DefaultWorkflow: null,
            DefaultModel: starts.LastOrDefault()?.Model ?? "",
            Workflows: [],
            Stages: [.. stages.Select(s => new PlanStageDto(
                s.Id, s.Title, s.Sessions,
                starts.FirstOrDefault(e => string.Equals(e.StageId, s.Id, StringComparison.Ordinal))?.Kind ?? "",
                starts.LastOrDefault(e => string.Equals(e.StageId, s.Id, StringComparison.Ordinal))?.Model,
                null, null, null, []))],
            Gates: gates,
            Limits: new PlanLimitsDto(
                StallMinutes: 0, SessionTimeoutMinutes: 0,
                MaxRunCostUsd: limits?.RunCostCapUsd, MaxRunTokens: limits?.RunTokenCap,
                VerifierThreshold: 0, MaxSessions: limits?.SessionCap,
                MaxSessionTokens: limits?.SessionTokenCap, SoftBreakRatio: limits?.NudgeRatio));
    }

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    private static int? OptInt(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var v) && v is not null ? Convert.ToInt32(v, Inv) : null;

    /// <summary>A SELECT that tolerates a database written by a different engine. <c>ledger</c>,
    /// <c>bugs</c> and <c>scores</c> all arrived in later schema versions, and naming a table an older
    /// import has never heard of throws <c>SqliteException: no such table</c> — which would take the
    /// whole attach down over a section that simply has nothing to show.</summary>
    private IReadOnlyList<Dictionary<string, object?>> TryQuery(
        string sql, params (string Name, object? Value)[] extra)
    {
        var parameters = new List<(string, object?)> { ("@runId", Run.RunId) };
        parameters.AddRange(extra);
        try { return _archive.Query(sql, parameters.ToArray()); }
        catch (SqliteException) { return []; }
    }
}
