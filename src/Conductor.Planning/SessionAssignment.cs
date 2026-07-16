namespace Conductor.Planning;

/// <summary>The assignment policy's decision for one session (P1): which agent runs it (only the
/// fields a role rule overrides — null means "keep the stage/plan default") and which ready items
/// it claims. The first item is always the active one; more than one only when the rules enable
/// multi-item sessions and the extra items are conflict-free.</summary>
public sealed class SessionAssignment
{
    public string? Model { get; init; }
    public string? Persona { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<ReadyItem> Items { get; init; } = [];
}
