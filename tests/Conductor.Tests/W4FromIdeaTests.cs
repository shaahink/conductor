using System.Text;
using System.Text.Json;
using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// W4.2 truth gates — one command from an idea to a drivable plan.
///
/// The documented bootstrap was `init` then `plan import`, and it could not use the AI path at all:
/// init wrote no advisor block, and both prose ingresses hard-refuse without one. The Face, the
/// obvious place to type an idea, only attaches to a running control plane — which needs a plan
/// that already exists. The first mile had no entrance.
///
/// The advisor here is a fake CLI that prints plan JSON, so the prose path is proven end to end
/// with no credential and no spend (the same trick the credential-free dogfood recipe uses).
/// </summary>
public sealed class W4FromIdeaTests
{
    // ---------------------------------------------------------------- the scaffold itself

    [Fact]
    public void Scaffold_CarriesACommentedAdvisorBlock()
    {
        var json = InitCommand.BuildPlanJson("demo", "C:/repo", RepoKind.Dotnet);
        Assert.Contains("\"advisor\"", json, StringComparison.Ordinal);
        Assert.Contains("// \"advisor\": {", json, StringComparison.Ordinal);
        Assert.Contains("{model}", json, StringComparison.Ordinal);

        // Commented out, so the scaffold still loads AND still has no advisor until asked for one.
        var dir = Path.Combine(Path.GetTempPath(), $"w42-scaffold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var planPath = Path.Combine(dir, "conductor.plan.json");
            File.WriteAllText(planPath, InitCommand.BuildPlanJson("demo", dir.Replace("\\", "/"), RepoKind.Generic));
            File.WriteAllText(Path.Combine(dir, "TRACKER.md"), InitCommand.BuildTrackerMd("demo"));
            var plan = PlanConfig.Load(planPath);
            Assert.Null(plan.Advisor);
            Assert.Empty(plan.CollectErrors());
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void PlaceholderStage_StepsAsideForRealStages_ButNotOnceClaimed()
    {
        var plan = new PlanConfig { Name = "p", Repo = "." };
        plan.Stages.Add(new StageConfig { Id = "S1", Title = "First stage — rename me and describe the work" });
        plan.Stages.Add(new StageConfig { Id = "A1", Title = "Real work", DependsOn = ["S1"] });
        plan.Progress = new ProgressConfig
        {
            Kind = "plan-checkpoints",
            Checkpoints =
            [
                new PlanCheckpoint { Id = "S1.1", Title = "first checkpoint — rename me" },
                new PlanCheckpoint { Id = "A1.1", Title = "do the thing" },
            ],
        };

        InitCommand.DropPlaceholderStage(plan);
        Assert.Equal(["A1"], plan.Stages.Select(s => s.Id));
        Assert.Equal(["A1.1"], plan.Progress.Checkpoints!.Select(c => c.Id));
        Assert.True(plan.Stages[0].DependsOn is null or { Count: 0 });

        // A placeholder someone actually delivered against is content now — it stays.
        var claimed = new PlanConfig { Name = "p", Repo = "." };
        claimed.Stages.Add(new StageConfig { Id = "S1", Title = "First stage — rename me and describe the work" });
        claimed.Stages.Add(new StageConfig { Id = "A1", Title = "Real work" });
        claimed.Progress = new ProgressConfig
        {
            Kind = "plan-checkpoints",
            Checkpoints = [new PlanCheckpoint { Id = "S1.1", Title = "done by hand", Status = "DONE" }],
        };
        InitCommand.DropPlaceholderStage(claimed);
        Assert.Equal(["S1", "A1"], claimed.Stages.Select(s => s.Id));
    }

    // ---------------------------------------------------------------- idea → plan, no model needed

    [Fact]
    public void FromIdea_WithAStructuredDoc_NeedsNoAdvisorAtAll()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"w42-structured-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var planPath = Scaffold(dir);
            var doc = Path.Combine(dir, "IDEA.md");
            File.WriteAllText(doc, """
                # The idea

                ### A1 — Ingest — read the feed
                - **A1.1** Parse the feed
                - **A1.2** Store the rows

                ### A2 — Report
                - **A2.1** Render the summary
                """);

            Assert.Equal(0, InitCommand.FromIdea(planPath, doc, model: null));

            var plan = PlanConfig.Load(planPath);
            Assert.Equal(["A1", "A2"], plan.Stages.Select(s => s.Id));
            Assert.Equal(["A1.1", "A1.2", "A2.1"], plan.Progress!.Checkpoints!.Select(c => c.Id));
            Assert.Empty(plan.CollectErrors());
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void FromIdea_WithProseAndNoAdvisor_SaysExactlyWhatToDo()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"w42-noadvisor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var planPath = Scaffold(dir);
            // Prose, no advisor: the scaffold survives and the plan still loads — a refusal must not
            // leave a half-written plan behind.
            Assert.NotEqual(0, InitCommand.FromIdea(planPath, "build me a thing that ingests a feed", model: null));
            var plan = PlanConfig.Load(planPath);
            Assert.Equal(["S1"], plan.Stages.Select(s => s.Id));
        }
        finally { TryDelete(dir); }
    }

    // ---------------------------------------------------------------- idea → plan, through a model

    [Fact]
    [Trait("Category", "Integration")]
    public void FromIdea_WithProse_RoutesThroughTheAdvisor_AndLandsDeclaredWork()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"w42-prose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var planPath = Scaffold(dir, WithFakeAdvisor(dir));

            Assert.Equal(0, InitCommand.FromIdea(planPath, "a service that ingests a feed and reports on it", model: null));

            var plan = PlanConfig.Load(planPath);
            // The advisor's stages AND its checkpoints landed — W4.1's contract, over the prose path.
            Assert.Equal(["A1", "A2"], plan.Stages.Select(s => s.Id));
            Assert.Equal(["A1.1", "A1.2", "A2.1"], plan.Progress!.Checkpoints!.Select(c => c.Id));
            Assert.Equal("Parse the feed", plan.Progress.Checkpoints![0].Title);
            Assert.Empty(plan.CollectErrors());
            Assert.Equal("ok", DoctorCommand.CheckWorkCoverage(plan).State);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IdeaToPausedRun_PutsStagesAndCardsOnTheBoard()
    {
        // The brief's gate: an empty scratch repo, one command, then `conductor run --paused` with
        // stages and cards to look at.
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w42-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(repo);
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email w42@test");
            Git("config user.name W42");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r");
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");

            var planPath = Scaffold(repo, WithFakeAdvisor(repo));
            Assert.Equal(0, InitCommand.FromIdea(planPath, "a service that ingests a feed and reports on it", model: null));

            var plan = PlanConfig.Load(planPath);
            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0, StartPaused: true), consoleSink: false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var run = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
            await run.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);

            // Parked before any session — and the board is already the idea.
            Assert.Empty(state.History);
            var store = host.Services.GetRequiredService<IRunStore>();
            var graph = new TaskGraph();
            graph.Fold(store.ReadAllEvents(state.RunId));
            Assert.Equal(["A1.1", "A1.2", "A2.1"],
                graph.Checkpoints().Select(c => c.TaskId).Order(StringComparer.Ordinal));
            Assert.Equal(["A1", "A2"], graph.Checkpoints().Select(c => c.StageId).Distinct().Order(StringComparer.Ordinal));
        }
        finally { TryDelete(repo); }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A "model" that costs nothing: a script printing the import contract's JSON, wired as
    /// the plan's advisor exactly as a real CLI would be.</summary>
    private static AdvisorConfig WithFakeAdvisor(string dir)
    {
        var payload = JsonSerializer.Serialize(new
        {
            stages = new object[]
            {
                new
                {
                    id = "A1", title = "Ingest", sessions = 2, kind = "deliver",
                    checkpoints = new object[]
                    {
                        new { id = "A1.1", title = "Parse the feed" },
                        new { id = "A1.2", title = "Store the rows" },
                    },
                },
                new
                {
                    id = "A2", title = "Report", sessions = 2, kind = "deliver", dependsOn = new[] { "A1" },
                    checkpoints = new object[] { new { id = "A2.1", title = "Render the summary" } },
                },
            },
            gates = Array.Empty<object>(),
        });

        var script = Path.Combine(dir, "fake-advisor.cmd");
        // `echo` mangles JSON quoting; a here-file written by the script keeps the payload verbatim.
        var payloadPath = Path.Combine(dir, "advisor-answer.json");
        File.WriteAllText(payloadPath, payload);
        File.WriteAllText(script, string.Join("\r\n", "@echo off", $"type \"{payloadPath}\"", "exit /b 0", ""));

        return new AdvisorConfig { Enabled = true, Command = "cmd.exe", Args = ["/c", script, "{prompt}"], Output = "text" };
    }

    private static string Scaffold(string dir, AdvisorConfig? advisor = null)
    {
        var planPath = Path.Combine(dir, "conductor.plan.json");
        File.WriteAllText(planPath, InitCommand.BuildPlanJson(Path.GetFileName(dir), dir.Replace("\\", "/"), RepoKind.Generic));
        File.WriteAllText(Path.Combine(dir, "TRACKER.md"), InitCommand.BuildTrackerMd(Path.GetFileName(dir)));
        if (advisor is null) return planPath;

        var plan = PlanConfig.Load(planPath);
        plan.Advisor = advisor;
        plan.Report.Commit = false;
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan, PlanConfig.JsonOpts),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return planPath;
    }

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
}
