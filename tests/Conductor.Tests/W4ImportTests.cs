using System.Text;
using System.Text.Json;
using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// W4.1 truth gates — an imported plan is drivable, with no hand-authored tracker in between.
///
/// <c>MarkdownPlanParser</c> parsed every checkpoint in a plan document and <c>ToImportResult</c>
/// dropped them, keeping only a session-count estimate; the advisor contract had no checkpoint key
/// either. So `plan import` produced stages the engine could schedule and no work for them to
/// schedule — the imported plan parked on its first stage until a human wrote the table by hand.
/// </summary>
public sealed class W4ImportTests
{
    private const string PlanDoc = """
        # Toy plan

        ### T1 — Foundations — the base layer
        - **T1.1** Lay the foundation
        - **T1.2** Pour the slab

        ### T2 — Walls
        - **T2.1** Frame the walls
        """;

    // ---------------------------------------------------------------- the parser stops discarding

    [Fact]
    public void StructuredImport_CarriesTheCheckpoints()
    {
        var result = PlanImportService.ParseStructured(PlanDoc);
        Assert.NotNull(result);
        Assert.Equal(["T1", "T2"], result.Stages.Select(s => s.Id));
        Assert.Equal(["T1.1", "T1.2", "T2.1"], result.Checkpoints.Select(c => c.Id));
        Assert.Equal("Lay the foundation", result.Checkpoints[0].Title);
        Assert.Equal("T1", result.Checkpoints[0].StageId);
    }

    [Fact]
    public void TrackerRowStatuses_SurviveAReimport()
    {
        // Re-importing a partly-delivered tracker must not un-deliver it.
        const string tracker = """
            # Plan

            ### T1 — Foundations
            ### T2 — Walls

            | # | Checkpoint | Status | Commit | Evidence |
            |---|---|---|---|---|
            | T1.1 | done thing | DONE | abc1234 | shipped |
            | T2.1 | next thing | TODO | | |
            """;
        var result = PlanImportService.ParseStructured(tracker);
        Assert.NotNull(result);
        Assert.Equal("DONE", Assert.Single(result.Checkpoints, c => c.Id == "T1.1").Status);
    }

    // ---------------------------------------------------------------- gates from the repo type

    [Fact]
    public void GatelessImport_ProposesTheRepoTypesBuildAndTest()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"w41-gates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "go.mod"), "module toy\n");
            var plan = new PlanConfig { Name = "p", Repo = dir };
            var result = PlanImportService.ParseStructured(PlanDoc, plan);

            Assert.NotNull(result);
            Assert.Equal(["build", "tests"], result.Gates.Select(g => g.Name));
            Assert.Equal("go build ./...", result.Gates[0].Command);

            // An existing battery is never second-guessed.
            var gated = new PlanConfig { Name = "p", Repo = dir };
            gated.Gates.Add(new GateConfig { Name = "mine", Command = "make check" });
            var second = PlanImportService.ParseStructured(PlanDoc, gated);
            Assert.Empty(second!.Gates);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void UnknownRepoType_ProposesNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"w41-generic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var result = PlanImportService.ParseStructured(PlanDoc, new PlanConfig { Name = "p", Repo = dir });
            Assert.Empty(result!.Gates);
        }
        finally { TryDelete(dir); }
    }

    // ---------------------------------------------------------------- apply lands declared work

    [Fact]
    public void Apply_DeclaresTheWorkInThePlan_AndFoldsExistingTrackerRowsIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"w41-apply-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // A markdown-table plan with one delivered row already on the board.
            File.WriteAllText(Path.Combine(dir, "TRACKER.md"), """
                # T

                | # | Checkpoint | Status | Commit | Evidence |
                |---|---|---|---|---|
                | T0.1 | already delivered | DONE | abc1234 | evidence |
                """);
            var planPath = Path.Combine(dir, "t.plan.json");
            var plan = new PlanConfig
            {
                Name = "t", Repo = dir, Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "T0", Title = "Prior" }],
                Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", "echo", "{prompt}"] },
            };
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan, PlanConfig.JsonOpts));
            plan.PlanFilePath = planPath;

            var incoming = PlanImportService.ParseStructured(PlanDoc)!;
            var diff = PlanDiff.Compute(plan, incoming);
            Assert.Equal(["T1.1", "T1.2", "T2.1"], diff.AddedCheckpoints.Select(c => c.Id));
            diff.ApplyChanges(plan);

            // The plan now declares its own work, and the pre-existing row came with it — status,
            // commit and evidence intact.
            Assert.Equal("plan-checkpoints", plan.Progress!.Kind);
            var declared = plan.Progress.Checkpoints!;
            Assert.Equal(["T0.1", "T1.1", "T1.2", "T2.1"], declared.Select(c => c.Id));
            var carried = declared[0];
            Assert.Equal("DONE", carried.Status);
            Assert.Equal("abc1234", carried.Commit);

            // …and the same import applied twice adds nothing the second time.
            var again = PlanDiff.Compute(plan, PlanImportService.ParseStructured(PlanDoc)!);
            Assert.Empty(again.AddedCheckpoints);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Apply_LeavesAScriptProviderAlone()
    {
        // A script provider computes declared work itself; an import must not silently replace it.
        var plan = new PlanConfig { Name = "t", Repo = "." };
        plan.Progress = new ProgressConfig { Kind = "script", Script = new ScriptProviderConfig { Command = "echo []" } };
        var diff = PlanDiff.Compute(plan, PlanImportService.ParseStructured(PlanDoc)!);
        diff.ApplyChanges(plan);
        Assert.Equal("script", plan.Progress.Kind);
        Assert.True(plan.Progress.Checkpoints is null or { Count: 0 });
    }

    // ---------------------------------------------------------------- the live gate

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImportedPlan_IsDrivableImmediately_WithNoHandEdits()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w41-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(repo);
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email w41@test");
            Git("config user.name W41");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r");
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");

            // The scratch repo exactly as `conductor init` leaves it: a scaffolded plan, its example
            // stage, and a tracker with the one placeholder row.
            var planPath = Path.Combine(repo, "conductor.plan.json");
            await File.WriteAllTextAsync(planPath, InitCommand.BuildPlanJson("w41-live", repo.Replace("\\", "/"), RepoKind.Generic));
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"), InitCommand.BuildTrackerMd("w41-live"));

            // The only hand edit in this test, and it is the harness's, not the plan's: point the
            // scaffold at a fake agent instead of a real CLI. No work is authored anywhere.
            var agentScript = Path.Combine(repo, "fake-agent.cmd");
            await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
                "@echo off",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"delivering\"}}",
                "exit /b 0",
                ""));
            var scaffold = PlanConfig.Load(planPath);
            scaffold.Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", agentScript, "{prompt}"], Provider = "opencode" };
            scaffold.Report.Commit = false;
            scaffold.Limits.MaxRunCostUsd = 1m;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(scaffold, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var doc = Path.Combine(repo, "PLAN-DOC.md");
            await File.WriteAllTextAsync(doc, PlanDoc);

            // `conductor plan import PLAN-DOC.md --yes` — the hand-authoring step, gone.
            Assert.Equal(0, PlanImportCommand.ExecuteImport(planPath, doc, model: null, assumeYes: true));

            var plan = PlanConfig.Load(planPath);
            Assert.Equal(["S1", "T1", "T2"], plan.Stages.Select(s => s.Id));
            // The imported stages arrived WITH their work, and the scaffold's row came along.
            Assert.Equal(["S1.1", "T1.1", "T1.2", "T2.1"], plan.Progress!.Checkpoints!.Select(c => c.Id));
            Assert.Empty(plan.CollectErrors());
            // G13, the check that used to fail on every imported plan: no stage without work.
            Assert.Equal("ok", DoctorCommand.CheckWorkCoverage(plan).State);

            // …and the engine drives it with no further edits: session 1 runs, and the board is
            // populated from the import.
            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
            var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(120), CancellationToken.None);
            Assert.Equal(0, code);
            Assert.Single(state.History);

            var store = host.Services.GetRequiredService<IRunStore>();
            var graph = new TaskGraph();
            graph.Fold(store.ReadAllEvents(state.RunId));
            Assert.Equal(["S1.1", "T1.1", "T1.2", "T2.1"],
                graph.Checkpoints().Select(c => c.TaskId).Order(StringComparer.Ordinal));
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ImportingTheProjectsOwnMaestroPlan_YieldsDrivableWork()
    {
        // The brief's named case: a real, long design doc from this repo.
        var docPath = Path.Combine(RepoRoot(), "docs", "history", "MAESTRO-PLAN.md");
        Assert.True(File.Exists(docPath), $"expected the plan doc at {docPath}");

        var result = PlanImportService.ParseStructured(File.ReadAllText(docPath));
        Assert.NotNull(result);
        Assert.True(result.Stages.Count >= 5, $"only {result.Stages.Count} stages parsed");
        Assert.NotEmpty(result.Checkpoints);

        // Every checkpoint hangs off a stage the import also declared — nothing orphaned, which is
        // the G13 authoring error doctor now fails on.
        var stageIds = result.Stages.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(result.Checkpoints, c => Assert.Contains(c.StageId, stageIds));
    }

    /// <summary>git marks pack/object files read-only, and a recursive delete trips over them —
    /// a cleanup failure must never be reported as a test failure.</summary>
    private static void TryDelete(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { }
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
