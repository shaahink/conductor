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

public sealed record StateDto(
    string PlanName, string Status, string? AttentionReason, string StageId, string StageTitle,
    string? Persona, int DoneCount, int TotalCount, decimal TotalCostUsd, decimal OverheadCostUsd,
    long TokensInput, long TokensOutput, long TokensReasoning,
    string CurrentCheckpoint, string CurrentCheckpointTitle, string GateSummary,
    IReadOnlyList<StageDto> Stages);

public sealed record TaskDto(string TaskId, string CheckpointId, string Title, string Status, string Source, int Order);

public sealed record TasksDto(IReadOnlyList<TaskDto> Tasks);

/// <summary>Body accepted by <c>POST /control</c> — same shape <c>ControlFile.Parse</c> already
/// produces from control.json, so both ingresses feed <c>ControlDispatcher</c> identically.</summary>
public sealed record ControlRequestDto(string? Command, bool Confirmed, string? IntentId, string? StageId, bool Force, string? Value);

public sealed record ControlAcceptedDto(bool Accepted, string? Reason);

public static class ControlPlaneDto
{
    public static StateDto FromSnapshot(DashboardSnapshot snap) => new(
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
        Stages: [.. snap.Stages.Select(FromStage)]);

    private static StageDto FromStage(StageProgress s) => new(
        Id: s.Id, Title: s.Title, Done: s.Done, Total: s.Total, State: s.State,
        Attempts: s.Attempts, LastOutcome: s.LastOutcome, CostUsd: s.CostUsd,
        ParentId: s.ParentId, Depth: s.Depth,
        Checkpoints: [.. s.Checkpoints.Select(c => new CheckpointDto(c.Id, c.Title, c.Status))]);

    public static TasksDto FromTasks(IReadOnlyList<TaskItem> tasks) => new(
        [.. tasks.Select(t => new TaskDto(t.TaskId, t.CheckpointId, t.Title, t.Status, t.Source, t.Order))]);
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
public sealed partial class ControlPlaneJsonContext : JsonSerializerContext;
