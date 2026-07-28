using System.Text;
using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// Pulls the section of a design/plan doc relevant to a stage, so the dashboard can show "what am I
/// working on" without leaving the TUI. Matches the first heading whose text mentions the stage id
/// (e.g. "## L5 — MCP v2 …") and returns until the next heading of the same or higher level.
/// </summary>
public static class DocsExtractor
{
    public static string ForStage(string docText, string stageId)
    {
        if (string.IsNullOrWhiteSpace(docText)) return "";
        var lines = docText.Replace("\r\n", "\n").Split('\n');
        var idRx = new Regex($@"(?:^|[^A-Za-z0-9]){Regex.Escape(stageId)}(?:[^A-Za-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);
        var headingRx = new Regex(@"^(?<hashes>#{1,6})\s+(?<text>.*)$", RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

        var start = -1;
        var startLevel = 6;
        for (var i = 0; i < lines.Length; i++)
        {
            var m = headingRx.Match(lines[i]);
            if (m.Success && idRx.IsMatch(m.Groups["text"].Value))
            {
                start = i;
                startLevel = m.Groups["hashes"].Value.Length;
                break;
            }
        }
        if (start < 0) return "";

        var sb = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            if (i > start)
            {
                var m = headingRx.Match(lines[i]);
                if (m.Success && m.Groups["hashes"].Value.Length <= startLevel) break;
            }
            sb.AppendLine(lines[i]);
        }
        return sb.ToString().TrimEnd();
    }

    public static string ForStageFromFile(string? path, string stageId)
    {
        // Doc extraction for a prompt is best-effort: an unreadable doc contributes no section rather
        // than failing the session. Only I/O faults are swallowed; anything else is a real bug.
        try { return path != null && File.Exists(path) ? ForStage(File.ReadAllText(path), stageId) : ""; }
        catch (IOException) { return ""; }
    }
}
