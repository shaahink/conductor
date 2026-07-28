namespace Conductor.Core.Http;

public sealed record TaskDto(string TaskId, string CheckpointId, string Title, string Status, string Source, int Order,
    // P3: the owner-editable per-task extra context (empty = none).
    string Context,
    // PF3: the card's declared repo-relative paths (empty = no declared claims).
    IReadOnlyList<string> Paths,
    // W1.4: the unified work-graph identity — kind (checkpoint | subtask), owning stage, and the
    // verdict engine's confirmation flag — so every view reads the same projection, not a subset.
    string Kind = "subtask", string StageId = "", bool Confirmed = false,
    // W4.4: this item's QA override ("" = inherit, "verify", "off"). Additive and last.
    string Qa = "");

public sealed record TasksDto(IReadOnlyList<TaskDto> Tasks);
