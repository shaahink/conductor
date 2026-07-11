using System.Text.Json.Serialization;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Core.Http;

/// <summary>
/// Wire-format DTOs for the F5 HTTP control plane. Deliberately separate from
/// <c>DashboardSnapshot</c>/<c>StageProgress</c> (Core/Progress.cs): those are TUI-rendering types
/// (they carry <c>ValueTuple</c> checkpoint lists System.Text.Json's source generator can't
/// serialise cleanly), not a wire contract. Mapping here is a thin field copy — no business logic
/// is duplicated, it's all still computed by <c>SnapshotBuilder</c> and <c>Events.TaskGraph</c>.
/// </summary>
public sealed record CheckpointDto(string Id, string Title, string Status);

public sealed record StageDto(
    string Id, string Title, int Done, int Total, string State,
    int Attempts, string LastOutcome, decimal CostUsd, string? ParentId, int Depth,
    IReadOnlyList<CheckpointDto> Checkpoints);

/// <summary>Live per-gate status during a running battery (F6 ticker/process visibility).</summary>
public sealed record GateDto(string Name, string State, double ElapsedSec);

public sealed record StateDto(
    string PlanName, string Status, string? AttentionReason, string StageId, string StageTitle,
    string? Persona, int DoneCount, int TotalCount, decimal TotalCostUsd, decimal OverheadCostUsd,
    long TokensInput, long TokensOutput, long TokensReasoning,
    string CurrentCheckpoint, string CurrentCheckpointTitle, string GateSummary,
    IReadOnlyList<StageDto> Stages,
    // F6: identity/location so the Face TUI needs no separate plan-file parsing — it can read/write
    // the same template + persona markdown files PromptBuilder/PersonaRegistry already hot-reload.
    string RunId, string Repo, string PlanDir,
    // F6: session-level ticker data (live cost/tokens/wall-time/gate battery) — DashboardSnapshot
    // already computes all of this for the old Spectre dashboard; it just wasn't on the wire yet.
    int SessionNumber, string SessionKind, int Attempt, int MaxAttempts,
    double SessionElapsedSec, bool AgentActive,
    decimal SessionCostUsd, long SessionTokensInput, long SessionTokensOutput, long SessionTokensReasoning,
    IReadOnlyList<GateDto> Gates);

public sealed record TaskDto(string TaskId, string CheckpointId, string Title, string Status, string Source, int Order);

public sealed record TasksDto(IReadOnlyList<TaskDto> Tasks);

/// <summary>Body accepted by <c>POST /control</c> — same shape <c>ControlFile.Parse</c> already
/// produces from control.json, so both ingresses feed <c>ControlDispatcher</c> identically.</summary>
public sealed record ControlRequestDto(string? Command, bool Confirmed, string? IntentId, string? StageId, bool Force, string? Value);

public sealed record ControlAcceptedDto(bool Accepted, string? Reason);

/// <summary>F2 supervised child process (Process pane, D11). <c>LastOutputLine</c> is best-effort —
/// only populated for <c>conductor bg start</c>-launched processes, whose stdout is teed to
/// <c>.conductor/bg-logs/*.log</c>; gate/agent children have no per-process log file today.</summary>
public sealed record ProcessDto(
    int Pid, string Purpose, string? StageId, int? SessionNumber,
    string StartedUtc, string? ExitedUtc, int? ExitCode, bool Alive, string? LastOutputLine);

public sealed record ProcessesDto(IReadOnlyList<ProcessDto> Processes);

/// <summary>One row of the run.db <c>sessions</c> table (session-history browser, D11).</summary>
public sealed record SessionRowDto(
    int Number, string StageId, string Kind, string StartedUtc, string? EndedUtc, string? Outcome,
    int Attempt, int ResumeCount, string? GateSummary, string? ResultSummary, int CommitCount);

public sealed record SessionsDto(IReadOnlyList<SessionRowDto> Sessions);

/// <summary>Ad-hoc SQL result (mirrors <c>conductor report --query</c>, F1.4) — values are pre-
/// stringified server-side so the result stays representable by a source-generated JSON context
/// (no reflection fallback for an open-ended <c>object?</c> column type).</summary>
public sealed record QueryRowDto(IReadOnlyList<string> Values);

public sealed record QueryResultDto(IReadOnlyList<string> Columns, IReadOnlyList<QueryRowDto> Rows, bool Truncated, string? Error);

/// <summary>Body accepted by <c>POST /inject</c> (D11 "inject editor"). Recorded to run.db's
/// <c>injections</c> table for visibility in reporting/the ledger; NOT YET consumed by the run
/// loop — threading a recorded injection into the next session's prompt is F8 (Telegram/chat)
/// scope, so this is honest storage + acknowledgement, not a silent no-op.</summary>
public sealed record InjectRequestDto(string? Content, string? StageId);

public sealed record InjectAcceptedDto(bool Accepted, string? Reason, string? RunId, string? StageId, string? RecordedUtc);

public static class ControlPlaneDto
{
    public static StateDto FromSnapshot(DashboardSnapshot snap, string runId, string repo, string planDir) => new(
        PlanName: snap.PlanName,
        Status: snap.Status,
        AttentionReason: snap.AttentionReason,
        StageId: snap.StageId,
        StageTitle: snap.StageTitle,
        Persona: snap.Persona,
        DoneCount: snap.DoneCount,
        TotalCount: snap.TotalCount,
        TotalCostUsd: snap.TotalCostUsd,
        OverheadCostUsd: snap.OverheadCostUsd,
        TokensInput: snap.TokensInput,
        TokensOutput: snap.TokensOutput,
        TokensReasoning: snap.TokensReasoning,
        CurrentCheckpoint: snap.CurrentCheckpoint,
        CurrentCheckpointTitle: snap.CurrentCheckpointTitle,
        GateSummary: snap.GateSummary,
        Stages: [.. snap.Stages.Select(FromStage)],
        RunId: runId,
        Repo: repo,
        PlanDir: planDir,
        SessionNumber: snap.SessionNumber,
        SessionKind: snap.SessionKind,
        Attempt: snap.Attempt,
        MaxAttempts: snap.MaxAttempts,
        SessionElapsedSec: snap.SessionElapsed.TotalSeconds,
        AgentActive: snap.AgentActive,
        SessionCostUsd: snap.SessionCostUsd,
        SessionTokensInput: snap.SessionTokensInput,
        SessionTokensOutput: snap.SessionTokensOutput,
        SessionTokensReasoning: snap.SessionTokensReasoning,
        Gates: [.. snap.Gates.Select(g => new GateDto(g.Name, g.State, g.LiveElapsed(DateTime.UtcNow).TotalSeconds))]);

    private static StageDto FromStage(StageProgress s) => new(
        Id: s.Id, Title: s.Title, Done: s.Done, Total: s.Total, State: s.State,
        Attempts: s.Attempts, LastOutcome: s.LastOutcome, CostUsd: s.CostUsd,
        ParentId: s.ParentId, Depth: s.Depth,
        Checkpoints: [.. s.Checkpoints.Select(c => new CheckpointDto(c.Id, c.Title, c.Status))]);

    public static TasksDto FromTasks(IReadOnlyList<TaskItem> tasks) => new(
        [.. tasks.Select(t => new TaskDto(t.TaskId, t.CheckpointId, t.Title, t.Status, t.Source, t.Order))]);

    public static ProcessDto FromPid(PidRow p, bool alive, string? lastOutputLine) => new(
        Pid: p.Pid, Purpose: p.Purpose, StageId: p.StageId, SessionNumber: p.SessionNumber,
        StartedUtc: p.StartedUtc.ToString("O"), ExitedUtc: p.ExitedUtc?.ToString("O"), ExitCode: p.ExitCode,
        Alive: alive, LastOutputLine: lastOutputLine);
}

/// <summary>Source-generated (de)serialisation for the control plane's DTOs — camelCase, matching
/// <c>Events.EventJsonContext</c>'s convention for the rest of the wire spine.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(StateDto))]
[JsonSerializable(typeof(TasksDto))]
[JsonSerializable(typeof(ControlRequestDto))]
[JsonSerializable(typeof(ControlAcceptedDto))]
[JsonSerializable(typeof(ProcessesDto))]
[JsonSerializable(typeof(SessionsDto))]
[JsonSerializable(typeof(QueryResultDto))]
[JsonSerializable(typeof(InjectRequestDto))]
[JsonSerializable(typeof(InjectAcceptedDto))]
[JsonSerializable(typeof(ControlPlaneInfo))]
public sealed partial class ControlPlaneJsonContext : JsonSerializerContext;

/// <summary>Contents of <c>.conductor/control-plane.json</c> — how a client finds a live run's control
/// plane without being told its (auto-scanned) port. Written on bind, deleted on shutdown.</summary>
public sealed record ControlPlaneInfo(int Port, string BaseUrl, int Pid, string PlanName, DateTime StartedUtc);
