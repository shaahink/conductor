using Conductor.Models;
using System.Text;

namespace Conductor.Core;

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
        {
            // K5.1: a structured result is compacted by dropping whole fields, so the next prompt pays
            // for fields rather than for half a sentence. Unstructured text takes the old byte cut below.
            var parsed = SessionResult.Parse(lastRed.ResultSummary);
            sb.AppendLine($"Result: {(parsed.IsStructured ? parsed.ToCompact(_maxBytes) : lastRed.ResultSummary)}");
        }
        var s = sb.ToString().TrimEnd();
        if (s.Length > _maxBytes) s = s[.._maxBytes] + "…";
        _lastFailure = s.Length > 0 ? s : null;
    }

    public string Name => "recent-failure";
    public string Section => _lastFailure ?? "";
    public bool IsEmpty => string.IsNullOrEmpty(_lastFailure);
}

/// <summary>PK4.2 / D7 - the words a claim carried for the room that the room has NOT heard, because the
/// verdict has not confirmed the claim: gates red, or verification still to come. The session that
/// wrote them is gone; this is how the next one learns they are held rather than lost, and that posting
/// them by hand would be exactly the claim-time card D7 retired.</summary>
public sealed class HeldWordsBattery : IPromptBattery
{
    private readonly string? _section;

    public HeldWordsBattery(IReadOnlyList<Models.TaskItem> checkpoints, int maxBytes = 900)
    {
        var held = checkpoints.Where(c => c.Status == "done" && !c.Confirmed && c.Tell.Length > 0).ToList();
        if (held.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("Words for the room are HELD - claimed with --tell, not yet confirmed, so no card has been posted:");
        foreach (var c in held) sb.AppendLine($"- {c.CheckpointId}: {c.Tell}");
        sb.Append("The engine posts each card when the verdict confirms its claim. Do not post them yourself; "
            + "a re-claim with --tell replaces the words.");
        var s = sb.ToString();
        _section = s.Length > maxBytes ? s[..maxBytes] + "…" : s;
    }

    public string Name => "held-words";
    public string Section => _section ?? "";
    public bool IsEmpty => _section is null;
}

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
            .Take(3)
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
