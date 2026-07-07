using Conductor.Models;

namespace Conductor.Tests;

public class RunStateTests
{
    [Fact]
    public void RoundTripsThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-test-{Guid.NewGuid():N}.json");
        try
        {
            var s = new RunState
            {
                PlanName = "Loom",
                Status = RunStatus.NeedsHuman,
                CurrentStage = "L2",
                SessionCounter = 7,
                AttemptsThisStage = 3,
                AttentionReason = "why",
                SkippedStages = { "L1" },
                PendingFix = new PendingFix { FromSession = 6, GateFailures = "build broke", ProgressSummary = "none" },
                History =
                {
                    new SessionRecord
                    {
                        Number = 7, Stage = "L2", Kind = SessionKind.Fix,
                        StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow,
                        Outcome = SessionOutcome.GatesRed, CostUsd = 1.23m,
                        NewCommits = { "abc fix things" }, NewlyDone = { "L2.1" },
                    },
                },
            };
            s.Save(path);
            var loaded = RunState.LoadOrNew(path, "x");
            Assert.Equal(RunStatus.NeedsHuman, loaded.Status);
            Assert.Equal("L2", loaded.CurrentStage);
            Assert.Equal(7, loaded.SessionCounter);
            Assert.Equal(SessionOutcome.GatesRed, loaded.History.Single().Outcome);
            Assert.Equal(SessionKind.Fix, loaded.History.Single().Kind);
            Assert.Equal("build broke", loaded.PendingFix!.GateFailures);
            Assert.Equal(1.23m, loaded.TotalCostUsd);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CorruptStateFileIsQuarantinedNotFatal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{not json at all");
            var loaded = RunState.LoadOrNew(path, "Loom");
            Assert.Equal("Loom", loaded.PlanName);
            Assert.Equal(RunStatus.Idle, loaded.Status);
            Assert.True(File.Exists(path + ".corrupt"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".corrupt");
        }
    }

    [Fact]
    public void RoundTripsPhaseGateAuditAndTokenState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-test-{Guid.NewGuid():N}.json");
        try
        {
            var s = new RunState
            {
                PlanName = "Loom",
                CurrentStage = "L1",
                CurrentStageStartHead = "abc1234",
                ConfirmedStages = { "L0" },
                AuditedStages = { "L0" },
                PendingPhaseGate = new PendingPhaseGate { StageId = "L1", StageStartHead = "def5678" },
                PendingAudit = new PendingAudit { StageId = "L1", StageStartHead = "def5678" },
                History =
                {
                    new SessionRecord
                    {
                        Number = 3, Stage = "L1", Kind = SessionKind.Audit, Attempt = 2,
                        StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow,
                        Outcome = SessionOutcome.Progress, CostUsd = 0.5m,
                        TokensInput = 1000, TokensOutput = 200, TokensReasoning = 50, TokensCacheRead = 900,
                    },
                },
            };
            s.Save(path);
            var loaded = RunState.LoadOrNew(path, "x");
            Assert.Contains("L0", loaded.ConfirmedStages);
            Assert.Contains("L0", loaded.AuditedStages);
            Assert.Equal("L1", loaded.PendingPhaseGate!.StageId);
            Assert.Equal("def5678", loaded.PendingAudit!.StageStartHead);
            Assert.Equal("abc1234", loaded.CurrentStageStartHead);
            Assert.Equal(SessionKind.Audit, loaded.History.Single().Kind);
            Assert.Equal(1000, loaded.TotalTokensInput);
            Assert.Equal(200, loaded.TotalTokensOutput);
            Assert.Equal(50, loaded.TotalTokensReasoning);
        }
        finally { File.Delete(path); }
    }
}
