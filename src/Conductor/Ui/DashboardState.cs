using Conductor.Core;

namespace Conductor.Ui;

/// <summary>
/// Immutable snapshot of everything the dashboard draws for one frame. Built on the UI thread
/// from the live buffers, then handed to the pure <see cref="DashboardRenderer"/>. Keeping this
/// separate makes rendering testable without a terminal or the orchestrator.
/// </summary>
public sealed record DashboardState
{
    public DashboardSnapshot Snap { get; init; } = new();
    public IReadOnlyList<AgentLine> Agent { get; init; } = Array.Empty<AgentLine>();
    public IReadOnlyList<ThinkingLine> Thinking { get; init; } = Array.Empty<ThinkingLine>();
    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();
    public int Width { get; init; } = 120;
    public int Height { get; init; } = 40;
    /// <summary>Animation frame counter for the activity spinner.</summary>
    public int Tick { get; init; }

    public readonly record struct AgentLine(string Kind, string Text, DateTime Utc);
    public readonly record struct ThinkingLine(DateTime Utc, string Text);
}
