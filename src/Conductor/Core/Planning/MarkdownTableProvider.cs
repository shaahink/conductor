using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>
/// The default <see cref="IProgressProvider"/>: Conductor's original strict Markdown-table tracker
/// (a checkpoint table + a handoff block). This holds the canonical parsing logic that used to live in
/// <see cref="TrackerParser"/>; <see cref="TrackerParser"/> now delegates here. The row/handoff shapes
/// and the stage/status vocabulary come from <see cref="ProgressConventions"/> (B1.4), so with the
/// defaults the parse is byte-identical for every existing call site, and a plan can retarget the
/// tracker shape (Shamshir's P-0/P3.4b/F5 ids) purely by config (F-1 decoupling, D-2 default format).
/// </summary>
public sealed class MarkdownTableProvider : IProgressProvider
{
    public string Name => "markdown-table";

    public TrackerSnapshot Read(PlanConfig plan) => Parse(File.ReadAllText(plan.TrackerPath), plan.Conventions);

    public static TrackerSnapshot ParseFile(string path) => Parse(File.ReadAllText(path), ProgressConventions.Default);

    public static TrackerSnapshot Parse(string trackerText) => Parse(trackerText, ProgressConventions.Default);

    public static TrackerSnapshot Parse(string trackerText, ProgressConventions conventions)
    {
        var rowRx = conventions.BuildRowRegex();
        var rows = new List<CheckpointRow>();
        foreach (var line in trackerText.Split('\n'))
        {
            var m = rowRx.Match(line.TrimEnd());
            if (!m.Success) continue;
            rows.Add(CheckpointRow.Create(
                conventions,
                m.Groups["id"].Value,
                m.Groups["title"].Value,
                m.Groups["status"].Value + m.Groups["rest"].Value,
                m.Groups["commit"].Value,
                m.Groups["evidence"].Value));
        }
        var handoff = conventions.BuildHandoffRegex().Match(trackerText) is { Success: true } h
            ? h.Groups["body"].Value.Trim()
            : "";
        return new TrackerSnapshot { Checkpoints = rows, HandoffBlock = handoff, RawText = trackerText };
    }
}
