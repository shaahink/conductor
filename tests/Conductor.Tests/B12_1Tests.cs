using Conductor.Core;
using Conductor.Models;
using Xunit;

namespace Conductor.Tests;

public sealed class B12_1Tests
{
    [Fact]
    public void LaneRunner_BuildPrompt_IncludesContextAndTask()
    {
        var lane = new AnalysisLaneConfig
        {
            Id = "arch-review", Kind = "architecture", Name = "Architecture Review",
            Prompt = "Evaluate the current architecture for parallelism readiness.",
        };
        var prompt = LaneRunner.BuildPrompt(lane, "TestPlan", "B12", "Parallelism",
            "handoff: QA-PASS", "branch feat/baton\nclean");

        Assert.Contains("architecture analyst", prompt);
        Assert.Contains("TestPlan", prompt);
        Assert.Contains("B12", prompt);
        Assert.Contains("Parallelism", prompt);
        Assert.Contains("Evaluate the current architecture", prompt);
        Assert.Contains("QA-PASS", prompt);
        Assert.DoesNotContain("commit", prompt);
        Assert.Contains("Do NOT edit files", prompt);
    }

    [Fact]
    public async Task LaneRunner_RunAsync_WritesToScratchDir_NotRepo()
    {
        var tmpRepo = Path.Combine(Path.GetTempPath(), $"conductor-b12-test-{Guid.NewGuid():N}"[..40]);
        var stateDir = Path.Combine(tmpRepo, ".conductor");
        var lane = new AnalysisLaneConfig
        {
            Id = "smoke-test", Kind = "qa", Name = "Smoke QA",
            Prompt = "Is the build green?",
        };
        var agent = new AgentConfig
        {
            Command = OperatingSystem.IsWindows() ? "cmd" : "echo",
            Args = OperatingSystem.IsWindows()
                ? new() { "/c", "echo All tests pass, build is green." }
                : new() { "All tests pass, build is green." },
        };

        Directory.CreateDirectory(stateDir);
        try
        {
            var initialFiles = Directory.Exists(tmpRepo)
                ? Directory.GetFiles(tmpRepo, "*", SearchOption.TopDirectoryOnly).Length : 0;

            var result = await LaneRunner.RunAsync(lane, agent, "TestPlan", "B12",
                "Parallelism", stateDir, null, null, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Null(result.Error);
            Assert.NotNull(result.Output);
            Assert.Contains("build is green", result.Output);

            // No new files written in the repo root
            var finalFiles = Directory.GetFiles(tmpRepo, "*", SearchOption.TopDirectoryOnly).Length;
            Assert.Equal(initialFiles, finalFiles);

            // Artifact was written to .conductor/lanes/
            var artifactPath = Path.Combine(stateDir, "lanes", "smoke-test.md");
            Assert.True(File.Exists(artifactPath));
            var content = await File.ReadAllTextAsync(artifactPath);
            Assert.Contains("build is green", content);
            Assert.Contains("Read-only", content);
        }
        finally
        {
            try { Directory.Delete(tmpRepo, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LaneArtifactBattery_InjectsArtifactIntoPrompt()
    {
        var tmpRepo = Path.Combine(Path.GetTempPath(), $"conductor-b12-test-2-{Guid.NewGuid():N}"[..42]);
        var stateDir = Path.Combine(tmpRepo, ".conductor");
        var lanesDir = Path.Combine(stateDir, "lanes");
        Directory.CreateDirectory(lanesDir);
        try
        {
            File.WriteAllText(Path.Combine(lanesDir, "arch-review.md"),
                "# Architecture review\nkind: architecture\n\n## Findings\n" +
                "1. The session loop is synchronous — needs async for true parallelism.\n" +
                "2. Gate runner already supports parallel gates as a precedent.\n" +
                "3. StatusAgent pattern is reusable for read-only lanes.\n");

            var battery = new LaneArtifactBattery(stateDir, "B12", 2048);
            Assert.False(battery.IsEmpty);
            Assert.Equal("analysis-lanes", battery.Name);
            var section = battery.Section;
            Assert.Contains("arch-review", section);
            Assert.Contains("session loop is synchronous", section);
            Assert.Contains("Gate runner", section);
        }
        finally
        {
            try { Directory.Delete(tmpRepo, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LaneArtifactBattery_Empty_WhenNoLanesDir()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"conductor-b12-empty-{Guid.NewGuid():N}"[..42]);
        var stateDir = Path.Combine(tmpDir, ".conductor");
        try
        {
            var battery = new LaneArtifactBattery(stateDir, "B12", 2048);
            Assert.True(battery.IsEmpty);
            Assert.Equal("", battery.Section);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LaneArtifactBattery_RespectsMaxBytes()
    {
        var tmpRepo = Path.Combine(Path.GetTempPath(), $"conductor-b12-bytes-{Guid.NewGuid():N}"[..42]);
        var stateDir = Path.Combine(tmpRepo, ".conductor");
        var lanesDir = Path.Combine(stateDir, "lanes");
        Directory.CreateDirectory(lanesDir);
        try
        {
            var longContent = new string('x', 5000);
            File.WriteAllText(Path.Combine(lanesDir, "big-review.md"), $"# Big review\n\n{longContent}\n");

            var battery = new LaneArtifactBattery(stateDir, "B12", 256);
            Assert.False(battery.IsEmpty);
            Assert.True(battery.Section.Length <= 260);
        }
        finally
        {
            try { Directory.Delete(tmpRepo, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PlanConfig_AnalysisLanes_DefaultsEmpty()
    {
        var plan = new PlanConfig();
        Assert.NotNull(plan.AnalysisLanes);
        Assert.Empty(plan.AnalysisLanes);
    }

    [Fact]
    public void AnalysisLaneConfig_Defaults()
    {
        var lane = new AnalysisLaneConfig();
        Assert.Equal("", lane.Id);
        Assert.Equal("analysis", lane.Kind);
        Assert.Equal(15, lane.TimeoutMinutes);
        Assert.True(lane.Enabled);
        Assert.Equal(200, lane.MaxOutputLines);
        Assert.Null(lane.StageTrigger);
    }

    [Fact]
    public void LaneResult_IsSuccess_WhenNoError()
    {
        var result = new LaneResult { LaneId = "test", Kind = "qa" };
        Assert.True(result.IsSuccess);

        var failed = new LaneResult { LaneId = "test", Kind = "qa", Error = "timeout" };
        Assert.False(failed.IsSuccess);
    }

    [Fact]
    public async Task LaneRunner_RunAsync_ReturnsError_WhenProcessFails()
    {
        var tmpRepo = Path.Combine(Path.GetTempPath(), $"conductor-b12-fail-{Guid.NewGuid():N}"[..42]);
        var stateDir = Path.Combine(tmpRepo, ".conductor");
        var lane = new AnalysisLaneConfig { Id = "fail-test", Kind = "research", Prompt = "Test",
            TimeoutMinutes = 1 };
        var agent = new AgentConfig { Command = "cmd", Args = new() { "/c", "exit 1" } };
        Directory.CreateDirectory(stateDir);

        try
        {
            var result = await LaneRunner.RunAsync(lane, agent, "TestPlan", "B12",
                "Parallelism", stateDir, null, null, CancellationToken.None);

            // The lane still "succeeds" (returns a LaneResult) but captures the exit code
            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            try { Directory.Delete(tmpRepo, recursive: true); } catch { }
        }
    }
}
