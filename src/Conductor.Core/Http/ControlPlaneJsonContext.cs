using System.Text.Json.Serialization;

namespace Conductor.Core.Http;

/// <summary>Source-generated (de)serialisation for the control plane's DTOs â€” camelCase, matching
/// <c>Events.EventJsonContext</c>'s convention for the rest of the wire spine.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(StateDto))]
[JsonSerializable(typeof(TasksDto))]
[JsonSerializable(typeof(TaskUpdateRequestDto))]
[JsonSerializable(typeof(TaskAddRequestDto))]
[JsonSerializable(typeof(TaskEditRequestDto))]
[JsonSerializable(typeof(TaskRefineRequestDto))]
[JsonSerializable(typeof(TaskRefineResultDto))]
[JsonSerializable(typeof(TaskSplitRequestDto))]
[JsonSerializable(typeof(TaskSplitResultDto))]
[JsonSerializable(typeof(TaskWriteResultDto))]
[JsonSerializable(typeof(PromptBlocksDto))]
[JsonSerializable(typeof(ControlRequestDto))]
[JsonSerializable(typeof(ControlAcceptedDto))]
[JsonSerializable(typeof(ProcessesDto))]
[JsonSerializable(typeof(ProcessKillRequestDto))]
[JsonSerializable(typeof(ProcessKillResultDto))]
[JsonSerializable(typeof(SessionsDto))]
[JsonSerializable(typeof(ScoresDto))]
[JsonSerializable(typeof(InjectRequestDto))]
[JsonSerializable(typeof(InjectAcceptedDto))]
[JsonSerializable(typeof(PromptPreviewDto))]
[JsonSerializable(typeof(TimelineDto))]
[JsonSerializable(typeof(LedgerDto))]
[JsonSerializable(typeof(BugsDto))]
[JsonSerializable(typeof(NoteRequestDto))]
[JsonSerializable(typeof(BugNewRequestDto))]
[JsonSerializable(typeof(BugResolveRequestDto))]
[JsonSerializable(typeof(KnowledgeWriteResultDto))]
[JsonSerializable(typeof(ConsoleLineDto))]
[JsonSerializable(typeof(ControlPlaneInfo))]
[JsonSerializable(typeof(PlanDto))]
[JsonSerializable(typeof(PlanEditRequestDto))]
[JsonSerializable(typeof(PlanMutationResultDto))]
[JsonSerializable(typeof(PlanImportRequestDto))]
[JsonSerializable(typeof(PlanImportResultDto))]
[JsonSerializable(typeof(TelegramStatusDto))]
[JsonSerializable(typeof(TelegramTestResultDto))]
[JsonSerializable(typeof(TelegramSetTokenRequestDto))]
[JsonSerializable(typeof(TelegramSetTokenResultDto))]
[JsonSerializable(typeof(OwnerQueueDto))]
[JsonSerializable(typeof(EvidenceDto))]
public sealed partial class ControlPlaneJsonContext : JsonSerializerContext;
