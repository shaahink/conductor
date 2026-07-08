namespace Conductor.Ui;

/// <summary>
/// Folds the agent stream so tool calls group their own (often noisy) output instead of burying the
/// narrative (B4.5). A <c>tool</c> line becomes a header; the <c>result</c>/<c>stderr</c>/<c>raw</c>/
/// <c>system</c> lines that follow it are its output and are hidden behind a "(N lines)" badge until
/// expanded. <c>text</c> lines are agent narrative — they break a group and always render, so the
/// story stays readable. Pure and deterministic for unit testing.
/// </summary>
public static class AgentFold
{
    /// <summary>A rendered row. <see cref="IsToolHeader"/> marks a folded tool call; when folded
    /// (<c>!expand</c>) <see cref="FoldedCount"/> is the number of hidden output lines. Output rows
    /// carry <see cref="Indent"/> so an expanded tool's lines read as belonging to it.</summary>
    public readonly record struct Row(string Kind, string Text, DateTime Utc, bool IsToolHeader, int FoldedCount, bool Indent);

    private static bool IsToolOutput(string kind) =>
        kind is "result" or "stderr" or "raw" or "system";

    public static IReadOnlyList<Row> Build(IReadOnlyList<DashboardState.AgentLine> lines, bool expand)
    {
        var rows = new List<Row>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (line.Kind == "tool")
            {
                var j = i + 1;
                while (j < lines.Count && IsToolOutput(lines[j].Kind)) j++;
                var outputCount = j - (i + 1);
                rows.Add(new Row(line.Kind, line.Text, line.Utc, IsToolHeader: true,
                    FoldedCount: expand ? 0 : outputCount, Indent: false));
                if (expand)
                    for (var k = i + 1; k < j; k++)
                        rows.Add(new Row(lines[k].Kind, lines[k].Text, lines[k].Utc, IsToolHeader: false, FoldedCount: 0, Indent: true));
                i = j;
            }
            else
            {
                rows.Add(new Row(line.Kind, line.Text, line.Utc, IsToolHeader: false, FoldedCount: 0, Indent: false));
                i++;
            }
        }
        return rows;
    }
}
