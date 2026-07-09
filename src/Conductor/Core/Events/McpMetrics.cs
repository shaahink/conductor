namespace Conductor.Core.Events;

/// <summary>
/// B5.4: MCP-compatible tool-call metrics, folded from <see cref="McpCallFinished"/> events.
/// Forward-looking — the B9 MCP task server will emit real events; until then the fold works on
/// synthetic streams and is used in a TUI panel + REPORT.md section.
/// </summary>
public static class McpMetrics
{
    public enum Severity { Ok, Warn }

    public sealed record McpReport(
        int TotalCalls, int Successes, int Failures, long TotalDurationMs,
        double AverageDurationMs, string MostCalledTool, int MostCalledCount,
        IReadOnlyList<string> ToolsUsed)
    {
        public Severity Worst => Failures > 0 ? Severity.Warn : Severity.Ok;
        public string HealthLine => Worst switch
        {
            Severity.Warn => $"overall Warning ({Failures} failures)",
            _ when TotalCalls == 0 => "no MCP calls recorded yet",
            _ => $"overall Ok ({TotalCalls} calls, {Successes} successes)",
        };
    }

    public static McpReport Compute(IEnumerable<ConductorEvent> events)
    {
        var calls = events.OfType<McpCallFinished>().OrderBy(e => e.Seq).ToList();

        if (calls.Count == 0)
            return new McpReport(0, 0, 0, 0, 0, "", 0, []);

        var byTool = calls.GroupBy(c => c.ToolName, StringComparer.Ordinal)
            .Select(g => (Tool: g.Key, Count: g.Count()))
            .OrderByDescending(t => t.Count)
            .ToList();
        var most = byTool.FirstOrDefault();

        return new McpReport(
            calls.Count,
            calls.Count(c => c.Success),
            calls.Count(c => !c.Success),
            calls.Sum(c => c.DurationMs),
            calls.Average(c => c.DurationMs),
            most.Tool,
            most.Count,
            byTool.Select(t => t.Tool).ToList()
        );
    }

    public static IEnumerable<string> Format(McpReport report)
    {
        yield return $"calls {report.TotalCalls}  ·  successes {report.Successes}  ·  failures {report.Failures}  ·  avg latency {report.AverageDurationMs:0} ms  ·  {report.HealthLine}";
        yield return "";
        yield return "tools used:";
        foreach (var t in report.ToolsUsed)
            yield return $"  {t}";
        if (report.TotalCalls > 0)
            yield return $"";
        if (!string.IsNullOrEmpty(report.MostCalledTool))
            yield return $"most-called: {report.MostCalledTool} ({report.MostCalledCount}×)";
    }
}
