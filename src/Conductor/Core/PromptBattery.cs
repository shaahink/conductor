using Conductor.Models;
using System.Text;

namespace Conductor.Core;

/// <summary>
/// A pluggable, bounded section injected into every session prompt (B8.5).
/// Each battery is opt-in per plan and must be deterministic + byte-bounded.
/// </summary>
public interface IPromptBattery
{
    /// <summary>Human-readable name, shown in logs and the rendered prompt header.</summary>
    string Name { get; }

    /// <summary>Rendered content for the prompt, bounded (≤ a few hundred bytes).
    /// Empty string = nothing injected.</summary>
    string Section { get; }

    /// <summary>True when this battery has no content to contribute (saves token budget).</summary>
    bool IsEmpty { get; }
}

/// <summary>
/// Injects the most recent lessons from <c>.conductor/lessons.md</c> into the prompt (B8.2).
/// Capped at <see cref="_maxEntries"/> entries so it never bloats the context.
/// </summary>
public sealed class LessonsBattery : IPromptBattery
{
    private readonly LessonsManager _lessons;
    private readonly int _maxEntries;

    public LessonsBattery(LessonsManager lessons, int maxEntries = 3)
    {
        _lessons = lessons;
        _maxEntries = maxEntries;
    }

    public string Name => "lessons";
    public string Section => _lessons.ReadRecent(_maxEntries);
    public bool IsEmpty => string.IsNullOrEmpty(Section);
}

/// <summary>
/// Injects a compact summary of the most recent failed session so the next session
/// knows what went wrong (B8.5). Bounded to <see cref="_maxBytes"/>.
/// </summary>
public sealed class RecentFailureBattery : IPromptBattery
{
    private readonly string? _lastFailure;
    private readonly int _maxBytes;

    public RecentFailureBattery(RunState state, int maxBytes = 600)
    {
        _maxBytes = maxBytes;
        var lastRed = state.History.LastOrDefault(h =>
            h.Outcome is SessionOutcome.GatesRed or SessionOutcome.AgentError or SessionOutcome.NoProgress);
        if (lastRed == null)
        {
            _lastFailure = null;
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"Last session (#{lastRed.Number}, stage {lastRed.Stage}, outcome: {lastRed.Outcome}) did not verify.");
        if (!string.IsNullOrEmpty(lastRed.GateSummary))
            sb.AppendLine($"Gates: {lastRed.GateSummary}");
        if (!string.IsNullOrEmpty(lastRed.ResultSummary))
            sb.AppendLine($"Result: {lastRed.ResultSummary}");
        var s = sb.ToString().TrimEnd();
        if (s.Length > _maxBytes) s = s[.._maxBytes] + "…";
        _lastFailure = s.Length > 0 ? s : null;
    }

    public string Name => "recent-failure";
    public string Section => _lastFailure ?? "";
    public bool IsEmpty => string.IsNullOrEmpty(_lastFailure);
}

/// <summary>
/// Injects the most recent analysis-lane artifacts into the next session's prompt (B12.1).
/// Reads from <c>.conductor/lanes/</c> and includes artifacts from the current stage that
/// were written within a recent window, bounded to <see cref="_maxBytes"/>.
/// </summary>
public sealed class LaneArtifactBattery : IPromptBattery
{
    private readonly string _lanesDir;
    private readonly string _currentStage;
    private readonly int _maxBytes;
    private readonly string? _section;

    public LaneArtifactBattery(string stateDir, string currentStage, int maxBytes = 1024)
    {
        _lanesDir = Path.Combine(stateDir, "lanes");
        _currentStage = currentStage;
        _maxBytes = maxBytes;

        if (!Directory.Exists(_lanesDir))
        {
            _section = null;
            return;
        }

        var files = new DirectoryInfo(_lanesDir).GetFiles("*.md")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(3) // at most 3 recent artifacts
            .ToList();

        if (files.Count == 0)
        {
            _section = null;
            return;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file.FullName);
                if (string.IsNullOrWhiteSpace(content)) continue;
                if (content.Length > 800)
                    content = content[..797] + "…";
                sb.AppendLine($"--- {Path.GetFileNameWithoutExtension(file.Name)} ---");
                sb.AppendLine(content);
                sb.AppendLine();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var s = sb.ToString().TrimEnd();
        if (s.Length > _maxBytes)
            s = s[.._maxBytes] + "…";
        _section = s.Length > 0 ? s : null;
    }

    public string Name => "analysis-lanes";
    public string Section => _section ?? "";
    public bool IsEmpty => string.IsNullOrEmpty(_section);
}

/// <summary>
/// Composes multiple batteries in order, rendering each non-empty section with its
/// name as a header. The total output is bounded to <see cref="_maxBytes"/> so no
/// single battery can dominate the prompt (B8.5 trap).
/// </summary>
public sealed class BatteryGroup
{
    private readonly List<IPromptBattery> _batteries;
    private readonly int _maxBytes;

    public static BatteryGroup Empty { get; } = new(Array.Empty<IPromptBattery>());

    public BatteryGroup(IEnumerable<IPromptBattery> batteries, int maxBytes = 2048)
    {
        _batteries = batteries.ToList();
        _maxBytes = maxBytes;
    }

    /// <summary>Rendered prompt section combining all non-empty batteries, or empty string.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var b in _batteries)
        {
            if (b.IsEmpty) continue;
            sb.AppendLine($"### {b.Name}");
            sb.AppendLine(b.Section);
            sb.AppendLine();
        }
        if (sb.Length == 0) return "";

        var result = sb.ToString().TrimEnd();
        if (result.Length > _maxBytes)
        {
            // Truncate at nearest paragraph boundary within budget
            var cutoff = _maxBytes;
            while (cutoff > 0 && result[cutoff] != '\n') cutoff--;
            result = cutoff > 100 ? result[..cutoff].TrimEnd() + "\n…" : result[.._maxBytes].TrimEnd() + "…";
        }
        return result;
    }

    public bool IsEmpty => _batteries.All(b => b.IsEmpty);
}
