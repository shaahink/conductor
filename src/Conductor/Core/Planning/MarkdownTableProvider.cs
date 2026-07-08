using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>
/// The default <see cref="IProgressProvider"/>: Conductor's original strict Markdown-table tracker
/// (a checkpoint table + a <c>## Handoff</c> block). This holds the canonical parsing logic that used
/// to live in <see cref="TrackerParser"/>; <see cref="TrackerParser"/> now delegates here so the parse
/// is byte-identical for every existing call site (F-1 decoupling, D-2 default format).
/// </summary>
public sealed partial class MarkdownTableProvider : IProgressProvider
{
    public string Name => "markdown-table";

    // Matches rows like: | L0.1 | Truth expectations (...) | TODO | | |
    // Status cell may carry decoration after the keyword (e.g. "DONE ✅").
    [GeneratedRegex(
        @"^\|\s*(?<id>[A-Za-z]+\d+(?:\.\d+)?[a-z]?)\s*\|(?<title>[^|]*)\|\s*(?<status>TODO|IN\s+PROGRESS|DONE|BLOCKED)(?<rest>[^|]*)\|(?<commit>[^|]*)\|(?<evidence>[^|]*)\|",
        RegexOptions.IgnoreCase)]
    private static partial Regex RowRx();

    [GeneratedRegex(
        @"^##\s*Handoff[^\r\n]*\r?\n(?<body>.*?)(?=^##\s|\z)",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex HandoffRx();

    public TrackerSnapshot Read(PlanConfig plan) => ParseFile(plan.TrackerPath);

    public static TrackerSnapshot ParseFile(string path) => Parse(File.ReadAllText(path));

    public static TrackerSnapshot Parse(string trackerText)
    {
        var rows = new List<CheckpointRow>();
        foreach (var line in trackerText.Split('\n'))
        {
            var m = RowRx().Match(line.TrimEnd());
            if (!m.Success) continue;
            rows.Add(new CheckpointRow(
                m.Groups["id"].Value.Trim(),
                m.Groups["title"].Value.Trim(),
                (m.Groups["status"].Value + m.Groups["rest"].Value).Trim(),
                m.Groups["commit"].Value.Trim(),
                m.Groups["evidence"].Value.Trim()));
        }
        var handoff = HandoffRx().Match(trackerText) is { Success: true } h ? h.Groups["body"].Value.Trim() : "";
        return new TrackerSnapshot { Checkpoints = rows, HandoffBlock = handoff, RawText = trackerText };
    }
}
