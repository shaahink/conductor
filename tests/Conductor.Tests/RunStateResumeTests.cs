using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// U0-adjacent: <see cref="RunStateResume"/> is the read-only, before-the-host-exists resume path
/// `RunCommand` uses so `conductor run` continues the same run instead of silently starting a
/// fresh one (2026-07-17 dogfood). Added alongside the async conversion that removed its MA0045
/// suppression (the caller became a real async boundary, so the sync-before-host excuse no longer
/// applied).
/// </summary>
public sealed class RunStateResumeTests : IDisposable
{
    private readonly string _dbPath;

    public RunStateResumeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"conductor-resume-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    [Fact]
    public async Task MissingDbFile_returnsNull()
    {
        var result = await RunStateResume.TryLoadLatestAsync(_dbPath, "no-such-plan", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task NoMatchingPlanName_returnsNull()
    {
        using (var db = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance))
        {
            db.InitializeRun("r1", "other-plan", "/r", "b", Conductor.Core.EngineStamp.Parse("v"));
            db.SaveRunState("r1", "other-plan", "{\"planName\":\"other-plan\"}");
        }

        var result = await RunStateResume.TryLoadLatestAsync(_dbPath, "my-plan", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MatchingRow_deserialisesTheRealRunState()
    {
        using (var db = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance))
        {
            db.InitializeRun("r1", "my-plan", "/r", "b", Conductor.Core.EngineStamp.Parse("v"));
            var state = new RunState { PlanName = "my-plan", RunId = "r1", Status = RunStatus.Running, SessionCounter = 3 };
            db.SaveRunState("r1", "my-plan", System.Text.Json.JsonSerializer.Serialize(state, PlanConfig.JsonOpts));
        }

        var result = await RunStateResume.TryLoadLatestAsync(_dbPath, "my-plan", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("r1", result!.RunId);
        Assert.Equal(RunStatus.Running, result.Status);
        Assert.Equal(3, result.SessionCounter);
    }

    [Fact]
    public async Task TornJson_returnsNullInsteadOfThrowing()
    {
        using (var db = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance))
        {
            db.InitializeRun("r1", "my-plan", "/r", "b", Conductor.Core.EngineStamp.Parse("v"));
            db.SaveRunState("r1", "my-plan", "{ not valid json");
        }

        var result = await RunStateResume.TryLoadLatestAsync(_dbPath, "my-plan", CancellationToken.None);
        Assert.Null(result);
    }
}
