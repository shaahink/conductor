using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Conductor.Planning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// W4.4 truth gates — criterion 5, the QA dial reaching the individual work item.
///
/// The dial existed at plan and stage level since P2 and stopped there, so "sometimes QA a specific
/// task; sometimes just deliver tasks one-by-one with no verify step" had no expression: you could
/// only choose for a whole stage. An item's dial now sits above the stage's, and says only whether
/// it wants verification — an item does not get to reshape the stage around it.
/// </summary>
public sealed class W4ItemQaTests
{
    private static readonly DefaultQaPolicy Policy = new();

    // ---------------------------------------------------------------- precedence

    [Fact]
    public void NoItemOverride_ProjectsExactlyAsBefore()
    {
        var planRule = new QaRule { Mode = "off" };
        var stageRule = new QaRule { Mode = "phaseGate" };

        foreach (var inherit in new[] { null, "", "  ", "inherit", "INHERIT" })
        {
            var with = Policy.Project(planRule, stageRule, inherit);
            var without = Policy.Project(planRule, stageRule);
            Assert.Equal(without.WorkflowName, with.WorkflowName);
            Assert.Equal(without.SkipVerification, with.SkipVerification);
            Assert.Equal(without.VerifierThreshold, with.VerifierThreshold);
        }
    }

    [Fact]
    public void ItemVerify_BeatsAStageDialThatSaysOff()
    {
        var stageOff = new QaRule { Mode = "off" };
        Assert.True(Policy.Project(null, stageOff).SkipVerification);

        var projected = Policy.Project(null, stageOff, "verify");
        Assert.False(projected.SkipVerification);
        Assert.Equal("deliver-verify", projected.WorkflowName);
    }

    [Fact]
    public void ItemOff_BeatsAStageDialThatVerifies()
    {
        var stageEvery = new QaRule { Mode = "everySession" };
        Assert.False(Policy.Project(null, stageEvery).SkipVerification);

        var projected = Policy.Project(null, stageEvery, "off");
        Assert.True(projected.SkipVerification);
    }

    [Fact]
    public void ItemDial_InheritsTheThresholdItDidNotSet()
    {
        // The item changes QA FREQUENCY for its own session; the bar the verifier must clear, and
        // the audit's shape, still come from the dial it inherits.
        var stage = new QaRule { Mode = "off", VerifierThreshold = 91, AuditCoversPriorSessions = false };
        var projected = Policy.Project(null, stage, "verify");
        Assert.Equal(91, projected.VerifierThreshold);
        Assert.False(projected.AuditCoversPriorSessions);
    }

    [Fact]
    public void ItemDial_WorksWithNoPlanOrStageDialAtAll()
    {
        Assert.Equal(QaProjection.Classic.WorkflowName, Policy.Project(null, null).WorkflowName);

        var off = Policy.Project(null, null, "off");
        Assert.True(off.SkipVerification);
        var verify = Policy.Project(null, null, "verify");
        Assert.False(verify.SkipVerification);
    }

    [Fact]
    public void UnknownItemValue_FallsBackToTheInheritedDial_NeverInventsOne()
    {
        var stage = new QaRule { Mode = "off" };
        Assert.True(Policy.Project(null, stage, "sometimes").SkipVerification);
        Assert.Equal(QaProjection.Classic.WorkflowName, Policy.Project(null, null, "sometimes").WorkflowName);
    }

    // ---------------------------------------------------------------- the write path

    [Fact]
    public void ItemQa_IsWrittenAndFolded_AndInheritClearsIt()
    {
        var graph = new TaskGraph();
        graph.Fold([new TaskAdded { RunId = "r", TaskId = "H1.1", CheckpointId = "H1.1", Title = "t",
            Source = "tracker", Kind = WorkItemKinds.Checkpoint, StageId = "H1" }]);

        var (set, err) = TaskWrites.BuildDetailEdit(graph, "r", "H1.1", null, null, null, qa: "VERIFY");
        Assert.Null(err);
        graph.Fold([set!]);
        Assert.Equal("verify", graph.Find("H1.1")!.Qa);

        var (cleared, _) = TaskWrites.BuildDetailEdit(graph, "r", "H1.1", null, null, null, qa: "inherit");
        graph.Fold([cleared!]);
        Assert.Equal("", graph.Find("H1.1")!.Qa);

        // An edit that names no field at all is still refused; an unknown mode is refused by name.
        Assert.NotNull(TaskWrites.BuildDetailEdit(graph, "r", "H1.1", null, null, null).Error);
        Assert.Contains("invalid qa",
            TaskWrites.BuildDetailEdit(graph, "r", "H1.1", null, null, null, qa: "maybe").Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherEdits_LeaveTheDialAlone()
    {
        var graph = new TaskGraph();
        graph.Fold([new TaskAdded { RunId = "r", TaskId = "H1.1", CheckpointId = "H1.1", Title = "t",
            Source = "tracker", Kind = WorkItemKinds.Checkpoint, StageId = "H1" }]);
        var (set, _) = TaskWrites.BuildDetailEdit(graph, "r", "H1.1", null, null, null, qa: "off");
        graph.Fold([set!]);

        var (titleOnly, _) = TaskWrites.BuildDetailEdit(graph, "r", "H1.1", "renamed", null, null);
        graph.Fold([titleOnly!]);
        Assert.Equal("off", graph.Find("H1.1")!.Qa);
        Assert.Equal("renamed", graph.Find("H1.1")!.Title);
    }

    // ---------------------------------------------------------------- the live gate

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TwoItemsOneStage_OneOffOneVerify_GetDifferentPipelines()
    {
        // Criterion 5, end to end: "deliver these one-by-one, but verify THAT one." Same stage,
        // same plan dial, two cards — and the pipeline differs per card.
        var repo = Environment.GetEnvironmentVariable("W44_DEBUG_REPO")
            ?? Path.Combine(Path.GetTempPath(), $"conductor-w44-{Guid.NewGuid():N}");
        using var cts = new CancellationTokenSource();
        try
        {
            Directory.CreateDirectory(repo);
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email w44@test");
            Git("config user.name W44");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r", CancellationToken.None);
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            // The table lives under its own heading: an unheaded table directly after `## Handoff`
            // becomes part of the handoff BLOCK, which the generator then replays verbatim at the
            // top of every regenerated tracker — and the parser, reading top-down, would keep seeing
            // that frozen copy instead of the live rows below it.
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n## Checkpoints\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
                + "| H0.1 | delivered one-by-one | TODO | | |\n| H0.2 | this one gets verified | TODO | | |\n",
                CancellationToken.None);

            // Commits each session, so the green verdict path advances the workflow — the exact
            // path where deliver-verify queues a verify. PowerShell for the multiline {prompt}.
            var agentScript = Path.Combine(repo, "fake-agent.ps1");
            await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
                "param([string]$Repo, [string]$Prompt = \"\")",
                "function O($type, $part) {",
                "    $o = @{ type = $type; session_id = 'fake' }",
                "    if ($null -ne $part) { $o.part = $part }",
                "    Write-Output ($o | ConvertTo-Json -Compress -Depth 6)",
                "}",
                "O 'step_start' $null",
                "Add-Content (Join-Path $Repo 'work.txt') ([Guid]::NewGuid().ToString())",
                "$null = git -C $Repo add -A 2>&1",
                "$null = git -C $Repo commit -m session --no-gpg-sign --quiet 2>&1",
                "O 'step_finish' @{ cost = 0.0001; tokens = @{ input = 10; output = 5; reasoning = 0; cache = @{ read = 0 } } }",
                "O 'text' @{ text = 'SESSION-RESULT: delivered.' }",
                "exit 0",
                ""), Encoding.ASCII, CancellationToken.None);

            var planPath = Path.Combine(repo, "w44.plan.json");
            var seed = new PlanConfig
            {
                Name = "w44-item-qa",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "H0", Title = "Per-item", Sessions = 8 }],
                Agent = new AgentConfig
                {
                    Command = "powershell",
                    Args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", agentScript,
                            "-Repo", repo.Replace("\\", "/"), "-Prompt", "{prompt}"],
                    Provider = "opencode",
                },
                GatePolicy = "perSession",
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
                // The STAGE says verify everything. The items get the last word.
                Pipeline = new PipelineRules { Qa = new QaRule { Mode = "everySession" } },
            };
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), CancellationToken.None);
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0), consoleSink: false);
            var store = host.Services.GetRequiredService<IRunStore>();

            // Seed the graph the way a run start would, then set each card's dial — H0.1 off,
            // H0.2 verify — before the first session picks anything up.
            store.InitializeRun(state.RunId, plan.Name, repo, "main", "1.0.0");
            store.SeedCheckpoints(state.RunId,
            [
                ("H0.1", "H0", "delivered one-by-one", "TODO", "", ""),
                ("H0.2", "H0", "this one gets verified", "TODO", "", ""),
            ]);
            foreach (var (id, qa) in new[] { ("H0.1", "off"), ("H0.2", "verify") })
            {
                var graph = new TaskGraph();
                graph.Fold(store.ReadAllEvents(state.RunId));
                var (evt, err) = TaskWrites.BuildDetailEdit(graph, state.RunId, id, null, null, null, qa);
                Assert.Null(err);
                store.AppendEvent(evt!);
            }
            store.FlushEvents();

            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

            // The agent claims H0.1 DURING its session, the way `conductor task --done` does — so
            // the verdict sees it delivered and the next session moves to H0.2.
            await ClaimWhenSessionStartsAsync(repo, store, state.RunId, 1, "H0.1");

            // H0.1 (qa: off) — a green committing deliver queues NO verification.
            await WaitAsync(() => state.History.Count >= 1 && state.History[0].Outcome is not null, TimeSpan.FromSeconds(120));
            Assert.Equal(SessionKind.Deliver, state.History[0].Kind);
            Assert.NotEmpty(state.History[0].NewCommits);
            Assert.Equal(["H0.1"], state.History[0].NewlyDone);
            Assert.Null(state.PendingVerify);
            Assert.DoesNotContain(state.History, h => h.Kind == SessionKind.Verify);

            // H0.2 (qa: verify) — the very same stage, and now a verify IS queued or running.
            await WaitAsync(
                () => state.PendingVerify is not null || state.History.Any(h => h.Kind == SessionKind.Verify),
                TimeSpan.FromSeconds(180));
            Assert.True(state.PendingVerify is not null || state.History.Any(h => h.Kind == SessionKind.Verify),
                "the item dial 'verify' should have produced a verify step on a stage the first item skipped");

            await cts.CancelAsync();
            var code = await runTask.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
            Assert.Equal(130, code);
        }
        finally
        {
            await cts.CancelAsync();
            if (Environment.GetEnvironmentVariable("W44_DEBUG_REPO") is null) TryDelete(repo);
        }
    }

    private static async Task ClaimWhenSessionStartsAsync(string repo, IRunStore engineStore, string runId,
        int sessionNumber, string checkpointId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (engineStore.ReadAllEvents(runId).OfType<SessionStarted>().Any(s => s.Number == sessionNumber)) break;
            await Task.Delay(100, CancellationToken.None);
        }
        using var cli = new SqliteRunStore(TestState.RunDb(repo),
            NullLogger<SqliteRunStore>.Instance);
        cli.UpdateCheckpoint(runId, checkpointId, "DONE", "fake1234", "claimed via task --done", source: "agent");
    }

    private static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(100, CancellationToken.None);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { }
            TestTemp.DeleteTree(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
