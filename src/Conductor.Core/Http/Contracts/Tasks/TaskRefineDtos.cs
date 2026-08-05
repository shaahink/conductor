namespace Conductor.Core.Http;

/// <summary>P3: edit a task's own data (<c>POST /tasks/edit</c>) — title, extra context, and/or
/// declared paths (PF3). null = leave unchanged; an empty context clears it; an empty paths array
/// clears the declared claims. This is the confirm step of the card-detail editor AND of the
/// advisor-refine flow: nothing the advisor proposes lands until the owner posts it here.</summary>
/// <para>W4.4: <c>Qa</c> sets this item's QA override — <c>inherit</c> | <c>verify</c> | <c>off</c>;
/// null leaves it unchanged and <c>inherit</c> clears it.</para>
public sealed record TaskEditRequestDto(string? TaskId, string? Title, string? Context, string[]? Paths = null,
    string? Qa = null);

/// <summary>P3: ask the plan's advisor model to refine one task (<c>POST /tasks/refine</c>).
/// <c>Instruction</c> is the owner's optional steer ("split this", "make it testable").
/// The endpoint only PROPOSES — the result carries suggested title/context and mutates nothing.</summary>
public sealed record TaskRefineRequestDto(string? TaskId, string? Instruction);

/// <summary>The advisor's proposed edit for a task — apply it by posting <c>/tasks/edit</c>.</summary>
public sealed record TaskRefineResultDto(
    bool Ok, string? Error, string? TaskId, string? Title, string? Context, string? Interpreter);
