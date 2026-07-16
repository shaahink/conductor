using System.Text.Json;
using Conductor.Core;

namespace Conductor.Tests;

public class ControlFileTests
{
    // The CLI writes nullable confirmed/force as JSON null for non-destructive commands. Parsing must
    // never throw on that (a prior GetBoolean() on a JSON null crashed the orchestrator control loop).
    [Theory]
    [InlineData("pause", ControlAction.PauseAfterSession)]
    [InlineData("resume", ControlAction.ResumeRun)]
    [InlineData("approve", ControlAction.ResumeRun)]
    [InlineData("retry-stage", ControlAction.RetryStage)]
    [InlineData("pause-after-stage", ControlAction.PauseAfterStage)]
    [InlineData("heartbeat", ControlAction.Heartbeat)]
    [InlineData("reload-plan", ControlAction.ReloadPlan)]
    public void NonDestructiveCommandWithNullFlagsParsesWithoutThrowing(string command, ControlAction expected)
    {
        var json = JsonSerializer.Serialize(new
        {
            command,
            issuedUtc = DateTime.UtcNow,
            confirmed = (bool?)null,
            intentId = (string?)null,
            force = (bool?)null,
        });
        var parsed = ControlFile.Parse(json);
        Assert.Equal(expected, parsed.Action);
        Assert.False(parsed.Confirmed);
        Assert.False(parsed.Force);
    }

    [Fact]
    public void DestructiveCommandCarriesConfirmedAndIntent()
    {
        var json = JsonSerializer.Serialize(new
        {
            command = "abort",
            confirmed = true,
            intentId = "deadbeef",
            force = (bool?)null,
        });
        var parsed = ControlFile.Parse(json);
        Assert.Equal(ControlAction.AbortNow, parsed.Action);
        Assert.True(parsed.Confirmed);
        Assert.Equal("deadbeef", parsed.IntentId);
    }

    [Fact]
    public void RollbackForceFlagIsParsed()
    {
        Assert.True(ControlFile.Parse("""{"command":"rollback","confirmed":true,"force":true}""").Force);
        Assert.False(ControlFile.Parse("""{"command":"rollback","confirmed":true,"force":null}""").Force);
    }

    [Fact]
    public void GotoCarriesStageId()
    {
        var parsed = ControlFile.Parse("""{"command":"goto","stageId":"B3"}""");
        Assert.Equal(ControlAction.Goto, parsed.Action);
        Assert.Equal("B3", parsed.StageId);
    }

    [Fact]
    public void UnknownCommandYieldsNullAction()
        => Assert.Null(ControlFile.Parse("""{"command":"nope"}""").Action);

    [Fact]
    public void WrongTypedFieldsDoNotThrow()
    {
        // Operator typo (numbers/strings where bools/strings expected) must degrade, not crash.
        var parsed = ControlFile.Parse("""{"command":"rollback","confirmed":"yes","force":1,"stageId":42}""");
        Assert.Equal(ControlAction.Rollback, parsed.Action);
        Assert.False(parsed.Confirmed);
        Assert.False(parsed.Force);
        Assert.Null(parsed.StageId);
    }
}
