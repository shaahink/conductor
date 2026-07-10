using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

public class B5_4McpMetricsTests
{
    private static IReadOnlyList<ConductorEvent> ParseEvents(string ndjson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-b54-mcp-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, ndjson);
        try { return EventLog.ReadAll(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Empty_log_produces_zero_report()
    {
        var events = Array.Empty<ConductorEvent>();
        var report = McpMetrics.Compute(events);

        Assert.Equal(0, report.TotalCalls);
        Assert.Equal(0, report.Successes);
        Assert.Equal(0, report.Failures);
        Assert.Equal(McpMetrics.Severity.Ok, report.Worst);
        Assert.Contains("no MCP calls", report.HealthLine);
    }

    [Fact]
    public void Folds_mcp_calls_into_counts_and_latency()
    {
        const string ndjson = """
        {"type":"mcpCallFinished","toolName":"list_files","durationMs":120,"success":true,"seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r"}
        {"type":"mcpCallFinished","toolName":"read_file","durationMs":85,"success":true,"seq":2,"ts":"2026-07-08T10:00:01Z","runId":"r"}
        {"type":"mcpCallFinished","toolName":"list_files","durationMs":200,"success":true,"seq":3,"ts":"2026-07-08T10:00:02Z","runId":"r"}
        {"type":"mcpCallFinished","toolName":"search","durationMs":500,"success":false,"seq":4,"ts":"2026-07-08T10:00:03Z","runId":"r"}
        """;

        var report = McpMetrics.Compute(ParseEvents(ndjson));

        Assert.Equal(4, report.TotalCalls);
        Assert.Equal(3, report.Successes);
        Assert.Equal(1, report.Failures);
        Assert.Equal(McpMetrics.Severity.Warn, report.Worst);
        Assert.Contains("Warning", report.HealthLine);
        Assert.Equal("list_files", report.MostCalledTool);
        Assert.Equal(2, report.MostCalledCount);
        Assert.Equal(3, report.ToolsUsed.Count);
    }

    [Fact]
    public void All_successful_calls_produce_ok_health()
    {
        const string ndjson = """
        {"type":"mcpCallFinished","toolName":"list_files","durationMs":10,"success":true,"seq":1,"ts":"2026-07-08T10:00:00Z","runId":"r"}
        """;

        var report = McpMetrics.Compute(ParseEvents(ndjson));

        Assert.Equal(McpMetrics.Severity.Ok, report.Worst);
        Assert.Contains("Ok", report.HealthLine);
    }

    [Fact]
    public void Format_renders_calls_and_tools_used()
    {
        var report = new McpMetrics.McpReport(3, 3, 0, 300, 100, "list_files", 2, ["list_files", "read_file"]);

        var lines = McpMetrics.Format(report).ToList();

        Assert.Contains(lines, l => l.Contains("calls 3"));
        Assert.Contains(lines, l => l.Contains("successes 3"));
        Assert.Contains(lines, l => l.Contains("failures 0"));
        Assert.Contains(lines, l => l.Contains("avg latency 100 ms"));
        Assert.Contains(lines, l => l.Contains("list_files"));
        Assert.Contains(lines, l => l.Contains("read_file"));
        Assert.Contains(lines, l => l.Contains("most-called: list_files (2"));
    }
}

public class B5_4RepoTests
{
    [Fact]
    public void RepoStrip_computes_branch_and_head_on_real_repo()
    {
        string repo;
        try { repo = Git.Exec(Directory.GetCurrentDirectory(), "rev-parse", "--show-toplevel").Output.Trim(); }
        catch { return; } // Not a git repo — skip

        var info = RepoStrip.Compute(repo);

        Assert.NotNull(info.Branch);
        Assert.NotEmpty(info.Branch);
        Assert.NotEqual("?", info.Branch);
        Assert.NotNull(info.Head);
        Assert.Equal(7, info.Head.Length);
        Assert.Null(info.Error);
    }

    [Fact]
    public void RepoStrip_handles_invalid_repo_gracefully()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"conductor-nonrepo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            // Not a git repo — Compute catches the error and stashes it in RepoInfo.Error
            var info = RepoStrip.Compute(tmp);

            Assert.NotNull(info.Error);
        }
        finally { try { Directory.Delete(tmp, recursive: true); } catch { } }
    }

    [Fact]
    public void Format_renders_branch_and_working_tree()
    {
        var info = new RepoStrip.RepoInfo("feat/baton-b5", "abc1234", false, "clean", 0, 0, false, null);

        var lines = RepoStrip.Format(info).ToList();

        Assert.Contains(lines, l => l.Contains("branch: feat/baton-b5"));
        Assert.Contains(lines, l => l.Contains("working tree: clean"));
    }

    [Fact]
    public void Format_shows_ahead_behind_when_upstream_exists()
    {
        var info = new RepoStrip.RepoInfo("feat/baton-b5", "abc1234", true, "M file.cs", 3, 1, true, null);

        var lines = RepoStrip.Format(info).ToList();

        Assert.Contains(lines, l => l.Contains("3 ahead, 1 behind"));
        Assert.Contains(lines, l => l.Contains("working tree: M file.cs"));
    }

    [Fact]
    public void Format_shows_up_to_date_when_synced()
    {
        var info = new RepoStrip.RepoInfo("main", "abc1234", false, "clean", 0, 0, true, null);

        var lines = RepoStrip.Format(info).ToList();

        Assert.Contains(lines, l => l.Contains("up to date"));
    }
}

public class B5_4ReporterTests
{
    private static PlanConfig PlanIn(string repo) => new()
    {
        Name = "T",
        Repo = repo,
        Report = new ReportConfig { Commit = true, Push = false },
        Stages = { new StageConfig { Id = "B5", Title = "observability" } },
    };

    [Fact]
    public void BuildRendersMcpSectionWhenCallsExist()
    {
        var mcp = new McpMetrics.McpReport(5, 4, 1, 500, 100, "search", 3, ["search", "list_files"]);

        var report = Reporter.Build(PlanIn(Path.GetTempPath()), new RunState { PlanName = "T" },
            new TrackerSnapshot(), null, null, null, null, mcp);

        Assert.Contains("## MCP", report);
        Assert.Contains("calls 5", report);
        Assert.Contains("successes 4", report);
        Assert.Contains("most-called: search", report);
    }

    [Fact]
    public void BuildOmitsMcpSectionWhenZeroCalls()
    {
        var mcp = new McpMetrics.McpReport(0, 0, 0, 0, 0, "", 0, []);

        var report = Reporter.Build(PlanIn(Path.GetTempPath()), new RunState { PlanName = "T" },
            new TrackerSnapshot(), null, null, null, null, mcp);

        Assert.DoesNotContain("## MCP", report);
    }

    [Fact]
    public void BuildRendersRepoSection()
    {
        var repo = new RepoStrip.RepoInfo("feat/baton-b5", "abc1234", false, "clean", 0, 0, false, null);

        var report = Reporter.Build(PlanIn(Path.GetTempPath()), new RunState { PlanName = "T" },
            new TrackerSnapshot(), null, null, null, null, null, repo);

        Assert.Contains("## Repo", report);
        Assert.Contains("branch: feat/baton-b5", report);
        Assert.Contains("working tree: clean", report);
        Assert.DoesNotContain("HEAD:", report);  // FormatStable omits volatile HEAD
    }
}
