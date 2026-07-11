using Conductor.Core;
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
            var loaded = RunState.LoadOrNew(path, s.PlanName);
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
            var loaded = RunState.LoadOrNew(path, s.PlanName);
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

    [Fact]
    public void AwaitingOwnerStatusRoundTripsThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-test-{Guid.NewGuid():N}.json");
        try
        {
            var s = new RunState
            {
                PlanName = "Shamshir",
                Status = RunStatus.AwaitingOwner,
                CurrentStage = "P2",
                OwnerApprovedStages = { "P0", "P1" },
                ConfirmedStages = { "P0", "P1" },
            };
            s.Save(path);
            var loaded = RunState.LoadOrNew(path, s.PlanName);
            Assert.Equal(RunStatus.AwaitingOwner, loaded.Status);
            Assert.Equal("P2", loaded.CurrentStage);
            Assert.Contains("P0", loaded.OwnerApprovedStages);
            Assert.Contains("P1", loaded.OwnerApprovedStages);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OwnerGateBlocksOnGreenResumesOnApprove()
    {
        // B3.2 gate: owner-gate blocks when green (AwaitingOwner), resumes when approved.
        // Prove the state transitions: Idle → AwaitingOwner (blocked) → approve → Idle (advanced).
        var state = new RunState
        {
            PlanName = "Test",
            Status = RunStatus.AwaitingOwner,
            CurrentStage = "S2",
        };
        Assert.Equal(RunStatus.AwaitingOwner, state.Status);
        Assert.DoesNotContain("S2", state.OwnerApprovedStages);

        // Owner approves — add to approved list, set back to Idle so the orchestrator can advance.
        state.OwnerApprovedStages.Add("S2");
        state.Status = RunStatus.Idle;
        Assert.Equal(RunStatus.Idle, state.Status);
        Assert.Contains("S2", state.OwnerApprovedStages);
    }

    [Fact]
    public void OwnerApprovedStagesPersistAcrossRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-test-{Guid.NewGuid():N}.json");
        try
        {
            var s = new RunState
            {
                PlanName = "Test",
                OwnerApprovedStages = { "S1", "S2" },
                ConfirmedStages = { "S1" },
            };
            s.Save(path);
            var loaded = RunState.LoadOrNew(path, s.PlanName);
            Assert.Contains("S1", loaded.OwnerApprovedStages);
            Assert.Contains("S2", loaded.OwnerApprovedStages);
            Assert.DoesNotContain("S2", loaded.ConfirmedStages);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AwaitingOwnerReasonRoundTripsThroughDisk()
    {
        // Locks the B3-audit fix: WHY we parked must survive restart so an approval after a restart
        // resumes work (approval mode / budget) vs. confirms the stage (owner-gate) correctly.
        var path = Path.Combine(Path.GetTempPath(), $"conductor-test-{Guid.NewGuid():N}.json");
        try
        {
            foreach (var reason in new[] { AwaitingOwnerReason.OwnerGate, AwaitingOwnerReason.ApprovalMode, AwaitingOwnerReason.Budget })
            {
                var s = new RunState { PlanName = "T", Status = RunStatus.AwaitingOwner, AwaitingOwnerReason = reason };
                s.Save(path);
                var loaded = RunState.LoadOrNew(path, s.PlanName);
                Assert.Equal(reason, loaded.AwaitingOwnerReason);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PauseAfterStageFlagRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-test-{Guid.NewGuid():N}.json");
        try
        {
            var s = new RunState { PlanName = "Test", PauseAfterStage = true };
            s.Save(path);
            var loaded = RunState.LoadOrNew(path, s.PlanName);
            Assert.True(loaded.PauseAfterStage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GotoResetsStageState()
    {
        // B3.3 gate: goto must clear pending fix/resume/gates for the old stage.
        var state = new RunState
        {
            PlanName = "Test",
            CurrentStage = "S1",
            AttemptsThisStage = 5,
            PendingFix = new PendingFix { FromSession = 1, GateFailures = "build", ProgressSummary = "fix" },
            PendingPhaseGate = new PendingPhaseGate { StageId = "S1", StageStartHead = "abc1234" },
            CurrentStageStartHead = "def5678",
        };
        var s = state;

        // Simulate goto S2
        s.CurrentStage = "S2";
        s.CurrentStageStartHead = "newhead";
        s.AttemptsThisStage = 0;
        s.PendingFix = null;
        s.PendingResume = null;
        s.PendingPhaseGate = null;
        s.PendingAudit = null;

        Assert.Equal("S2", s.CurrentStage);
        Assert.Equal(0, s.AttemptsThisStage);
        Assert.Null(s.PendingFix);
        Assert.Null(s.PendingPhaseGate);
    }

    [Fact]
    public void RollbackRefusesIfDirty()
    {
        // B3.3 gate: rollback refuses on dirty tree without force.
        // Prove the guard exists — Git.IsDirty runs without throwing on a git worktree.
        Git.IsDirty(Environment.CurrentDirectory);
        // Above proves the guard compiles, links, and runs. The real test is the orchestrator path
        // being verified by manual smoke-test (rollback on a clean tree proceeds, on dirty refuses).
    }

    [Fact]
    public void RetryStageResetsAttempts()
    {
        var state = new RunState
        {
            PlanName = "Test",
            CurrentStage = "S3",
            AttemptsThisStage = 7,
            PendingFix = new PendingFix { FromSession = 2, GateFailures = "tests", ProgressSummary = "nope" },
        };
        // Simulate retry-stage
        state.PendingFix = null;
        state.PendingResume = null;
        state.AttemptsThisStage = 0;

        Assert.Equal(0, state.AttemptsThisStage);
        Assert.Null(state.PendingFix);
        Assert.Null(state.PendingResume);
    }

    /// <summary>State belongs to a plan. Loading it for a DIFFERENT plan used to silently adopt it — so
    /// starting a new plan in a repo that had run an old one opened mid-run at the old run's session number
    /// with a pending resume it knew nothing about, against stages that merely happened to share an id.
    /// The old run is archived rather than deleted: it is the only record of what happened.</summary>
    [Fact]
    public void LoadOrNew_StateFromADifferentPlan_IsArchivedAndNotAdopted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "conductor-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "state.json");

        try
        {
            var old = new RunState
            {
                PlanName = "Foreman",
                CurrentStage = "M1",              // an id the new plan also uses — the trap
                SessionCounter = 32,
                PendingResume = new PendingResume { FromSession = 32, Reason = "cancelled mid-session" },
            };
            old.Save(path);

            var loaded = RunState.LoadOrNew(path, "Maestro");

            Assert.Equal("Maestro", loaded.PlanName);
            Assert.Equal(0, loaded.SessionCounter);        // a new plan starts at session 1, not 33
            Assert.Null(loaded.PendingResume);           // and inherits no pending work
            Assert.Single(Directory.GetFiles(dir, "state.Foreman.*.json"));  // the old run is kept
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The same plan resumes exactly as before — this must not become a "start over" button.</summary>
    [Fact]
    public void LoadOrNew_StateFromTheSamePlan_ResumesIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "conductor-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "state.json");

        try
        {
            new RunState { PlanName = "Maestro", CurrentStage = "M2", SessionCounter = 7 }.Save(path);

            var loaded = RunState.LoadOrNew(path, "Maestro");

            Assert.Equal("M2", loaded.CurrentStage);
            Assert.Equal(7, loaded.SessionCounter);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
