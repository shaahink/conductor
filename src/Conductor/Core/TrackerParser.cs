using System.Text.RegularExpressions;

namespace Conductor.Core;

public sealed record CheckpointRow(string Id, string Title, string Status, string Commit, string Evidence)
{
    public string StageId => Id.Split('.')[0];
    public bool IsDone => Status.StartsWith("DONE", StringComparison.OrdinalIgnoreCase);
    public bool IsBlocked => Status.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase);
    public bool IsInProgress => Status.StartsWith("IN", StringComparison.OrdinalIgnoreCase);
}

public sealed class TrackerSnapshot
{
    public List<CheckpointRow> Checkpoints { get; init; } = new();
    public string HandoffBlock { get; init; } = "";
    public string RawText { get; init; } = "";

    public bool AllDone => Checkpoints.Count > 0 && Checkpoints.All(c => c.IsDone);

    public IEnumerable<CheckpointRow> ForStage(string stageId)
        => Checkpoints.Where(c => c.StageId.Equals(stageId, StringComparison.OrdinalIgnoreCase));

    public bool StageDone(string stageId)
    {
        var rows = ForStage(stageId).ToList();
        return rows.Count > 0 && rows.All(r => r.IsDone);
    }

    public CheckpointRow? ById(string id)
        => Checkpoints.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public static class TrackerParser
{
    // Matches rows like: | L0.1 | Truth expectations (...) | TODO | | |
    // Status cell may carry decoration after the keyword (e.g. "DONE ✅").
    private static readonly Regex RowRx = new(
        @"^\|\s*(?<id>[A-Za-z]+\d+(?:\.\d+)?[a-z]?)\s*\|(?<title>[^|]*)\|\s*(?<status>TODO|IN\s+PROGRESS|DONE|BLOCKED)(?<rest>[^|]*)\|(?<commit>[^|]*)\|(?<evidence>[^|]*)\|",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HandoffRx = new(
        @"^##\s*Handoff[^\r\n]*\r?\n(?<body>.*?)(?=^##\s|\z)",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    public static TrackerSnapshot Parse(string trackerText)
    {
        var rows = new List<CheckpointRow>();
        foreach (var line in trackerText.Split('\n'))
        {
            var m = RowRx.Match(line.TrimEnd());
            if (!m.Success) continue;
            rows.Add(new CheckpointRow(
                m.Groups["id"].Value.Trim(),
                m.Groups["title"].Value.Trim(),
                (m.Groups["status"].Value + m.Groups["rest"].Value).Trim(),
                m.Groups["commit"].Value.Trim(),
                m.Groups["evidence"].Value.Trim()));
        }
        var handoff = HandoffRx.Match(trackerText) is { Success: true } h ? h.Groups["body"].Value.Trim() : "";
        return new TrackerSnapshot { Checkpoints = rows, HandoffBlock = handoff, RawText = trackerText };
    }

    public static TrackerSnapshot ParseFile(string path) => Parse(File.ReadAllText(path));
}
