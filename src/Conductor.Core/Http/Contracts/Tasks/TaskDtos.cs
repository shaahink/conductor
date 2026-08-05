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
    string Qa = "",
    // SF3.2: the card meta a board shows WITHOUT selecting the card — the session whose work last
    // moved it (0 = none), when it entered its current status ("O", null = never moved / unstamped),
    // and how many times it has been picked up. Folded by TaskGraph, so every reader gets the same
    // numbers instead of three views deriving three different ones.
    int SessionNumber = 0, string? StatusSinceUtc = null, int Attempts = 0);

public sealed record TasksDto(IReadOnlyList<TaskDto> Tasks);
