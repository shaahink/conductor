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
    public IReadOnlyList<LogEntry> Log { get; init; } = Array.Empty<LogEntry>();
    public int Width { get; init; } = 120;
    public int Height { get; init; } = 40;
    public int Tick { get; init; }
    public string? ConfirmPrompt { get; init; }
    public PlanTreeView Tree { get; init; } = new();
    public bool AgentExpanded { get; init; }
    public ToastMessage? Toast { get; init; }

    public readonly record struct AgentLine(string Kind, string Text, DateTime Utc);
    public readonly record struct ThinkingLine(DateTime Utc, string Text);
}
