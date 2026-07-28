using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
/// W1.2 — one WorkGraphSync at every boundary. Unit half: upsert-with-provenance semantics (add /
/// refresh-title / retire-as-archived / revive / zero-item-stage scaffold, never clobbering runtime
/// status), the G13 coverage validation in CollectErrors and doctor, and the torn-tracker safety
/// rail. Truth-gate half (Category=Integration): a LIVE paused run gains a stage over the real
/// /plan/edit wire and its scaffolded card is on GET /tasks immediately — no restart.
/// </summary>
public sealed class W1WorkGraphSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-w12-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly SqliteRunStore _db;

    public W1WorkGraphSyncTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "run.db");
        _db = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance);
        _db.InitializeRun("r1", "p", _dir, "b", "v");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private PlanConfig PlanWithTracker(string trackerBody, params StageConfig[] stages)
    {
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), trackerBody);
        return new PlanConfig
        {
            Name = "sync-test",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Stages = [.. stages],
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
        };
    }

    private static string Tracker(params string[] rows) =>
        "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
        + string.Join("\n", rows) + "\n";

    [Fact]
    public void Sync_adds_declared_items_and_is_idempotent()
    {
        var plan = PlanWithTracker(Tracker("| S1.1 | first | TODO | | |", "| S1.2 | second | DONE | abc1234 | green |"),
            new StageConfig { Id = "S1", Title = "Stage One" });

        var first = WorkGraphSync.Sync(plan, _db, "r1");
        Assert.Equal(2, first.Added);
        Assert.True(first.Changed);

        var rows = _db.GetCheckpoints("r1");
        Assert.Equal(2, rows.Count);
        Assert.Equal("DONE", rows.Single(r => r.Id == "S1.2").Status);
        Assert.Equal("abc1234", rows.Single(r => r.Id == "S1.2").Commit);

        var second = WorkGraphSync.Sync(plan, _db, "r1");
        Assert.False(second.Changed); // nothing to do — no writes, no tracker rewrite
    }

    [Fact]
    public void Sync_scaffolds_a_checkpoint_for_a_stage_with_no_declared_work()
    {
        var plan = PlanWithTracker(Tracker("| S1.1 | first | TODO | | |"),
            new StageConfig { Id = "S1", Title = "Stage One" },
            new StageConfig { Id = "S2", Title = "Added Mid-Run" });

        var result = WorkGraphSync.Sync(plan, _db, "r1");

        Assert.Equal(1, result.Scaffolded);
        var scaffold = _db.GetCheckpoints("r1").Single(r => r.StageId == "S2");
        Assert.Equal("S2.1", scaffold.Id);
        Assert.Equal("Added Mid-Run", scaffold.Title);
        Assert.Equal("TODO", scaffold.Status);

        // The tracker view regenerated with the scaffolded row — the engine's schedule sees it.
        var regenerated = File.ReadAllText(Path.Combine(_dir, "TRACKER.md"));
        Assert.Contains("S2.1", regenerated, StringComparison.Ordinal);
    }

    [Fact]
    public void Sync_archives_items_of_a_deleted_stage_and_revives_on_redeclare()
    {
        var plan = PlanWithTracker(Tracker("| S1.1 | first | TODO | | |", "| S2.1 | second | TODO | | |"),
            new StageConfig { Id = "S1", Title = "One" },
            new StageConfig { Id = "S2", Title = "Two" });
        WorkGraphSync.Sync(plan, _db, "r1");

        // The owner deletes stage S2 (what /plan/edit does) — the regenerated tracker follows the
        // plan, so S2's row leaves the declared list too.
        var shrunk = PlanWithTracker(Tracker("| S1.1 | first | TODO | | |"),
            new StageConfig { Id = "S1", Title = "One" });
        var result = WorkGraphSync.Sync(shrunk, _db, "r1");

        Assert.Equal(1, result.Archived);
        Assert.DoesNotContain(_db.GetCheckpoints("r1"), r => r.Id == "S2.1"); // out of every view

        // History survived — the graph still knows the item, as archived.
        var graph = new TaskGraph();
        graph.Fold(_db.ReadAllEvents("r1"));
        Assert.Equal("archived", graph.Find("S2.1")!.Status);

        // Re-declaring it (stage + row return) revives it with its declared status.
        var restored = PlanWithTracker(Tracker("| S1.1 | first | TODO | | |", "| S2.1 | second | IN PROGRESS | | |"),
            new StageConfig { Id = "S1", Title = "One" },
            new StageConfig { Id = "S2", Title = "Two" });
        var revive = WorkGraphSync.Sync(restored, _db, "r1");
        Assert.Equal(1, revive.Revived);
        Assert.Equal("IN PROGRESS", _db.GetCheckpoints("r1").Single(r => r.Id == "S2.1").Status);
    }

    [Fact]
    public void Sync_never_clobbers_runtime_status_and_never_archives_on_an_empty_declared_list()
    {
        var plan = PlanWithTracker(Tracker("| S1.1 | first | TODO | | |"),
            new StageConfig { Id = "S1", Title = "One" });
        WorkGraphSync.Sync(plan, _db, "r1");
        _db.UpdateCheckpoint("r1", "S1.1", "DONE", "eng1234", "delivered", source: "engine");

        // Re-sync with the tracker still saying TODO — runtime status wins.
        var after = WorkGraphSync.Sync(plan, _db, "r1");
        Assert.False(after.Changed);
        Assert.Equal("DONE", _db.GetCheckpoints("r1").Single().Status);

        // A torn/empty tracker read must never read as "retire everything".
        var torn = PlanWithTracker("# half-written", new StageConfig { Id = "S1", Title = "One" });
        var result = WorkGraphSync.Sync(torn, _db, "r1");
        Assert.Equal(0, result.Archived);
        Assert.Single(_db.GetCheckpoints("r1"));
    }

    [Fact]
    public void CollectErrors_rejects_inline_checkpoints_that_cover_no_stage()
    {
        var plan = new PlanConfig
        {
            Name = "inline",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Stages = [new StageConfig { Id = "S1", Title = "One" }],
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Progress = new ProgressConfig
            {
                Kind = "plan-checkpoints",
                Checkpoints = [new PlanCheckpoint { Id = "S9.1", Title = "orphan" }],
            },
        };
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), "# t");

        var errors = plan.CollectErrors();
        Assert.Contains(errors, e => e.Contains("S9.1", StringComparison.Ordinal) && e.Contains("S9", StringComparison.Ordinal));

        // Fixing the id clears it.
        plan.Progress.Checkpoints = [new PlanCheckpoint { Id = "S1.1", Title = "covered" }];
        Assert.DoesNotContain(plan.CollectErrors(), e => e.Contains("progress.checkpoints", StringComparison.Ordinal));
    }

    [Fact]
    public void Doctor_work_coverage_check_flags_orphans_and_uncovered_stages()
    {
        var covered = PlanWithTracker(Tracker("| S1.1 | first | TODO | | |"),
            new StageConfig { Id = "S1", Title = "One" });
        Assert.Equal("ok", DoctorCommand.CheckWorkCoverage(covered).State);

        var uncovered = PlanWithTracker(Tracker("| S1.1 | first | TODO | | |"),
            new StageConfig { Id = "S1", Title = "One" },
            new StageConfig { Id = "S2", Title = "Two" });
        var warn = DoctorCommand.CheckWorkCoverage(uncovered);
        Assert.Equal("warn", warn.State);
        Assert.Contains("S2", warn.Message, StringComparison.Ordinal);

        var orphaned = PlanWithTracker(Tracker("| S1.1 | first | TODO | | |", "| Z9.1 | lost | TODO | | |"),
            new StageConfig { Id = "S1", Title = "One" });
        var fail = DoctorCommand.CheckWorkCoverage(orphaned);
        Assert.Equal("fail", fail.State);
        Assert.Contains("Z9.1", fail.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveRun_StageAddedOverPlanEditWire_CardsAppearOnTheBoardWithoutRestart()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w12-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        using var http = new HttpClient();
        try
        {
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email w12@test");
            Git("config user.name W12");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r");
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                Tracker("| H0.1 | seeded checkpoint | TODO | | |"));

            var planPath = Path.Combine(repo, "live.plan.json");
            var seed = new PlanConfig
            {
                Name = "w12-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "H0", Title = "Original", Sessions = 1 }],
                Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", "echo", "{prompt}"], Provider = "opencode" },
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
            };
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var plan = PlanConfig.Load(planPath);

            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: true, MaxSessions: 0,
                    ControlPlane: true, ControlPlanePort: port, StartPaused: true), consoleSink: false);
            var server = host.Services.GetRequiredService<Conductor.Core.Http.ControlPlaneServer>();
            Assert.True(server.Start(), "control plane failed to bind");
            http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);

            using var cts = new CancellationTokenSource();
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (state.Status != RunStatus.Paused && DateTime.UtcNow < deadline)
                await Task.Delay(50, CancellationToken.None);
            Assert.Equal(RunStatus.Paused, state.Status); // live and parked — NOT restarted below

            // THE TRUTH GATE — add a stage over the real /plan/edit wire, mid-run.
            using var editBody = new StringContent(
                """{"edits":[{"op":"add","target":"stage","id":"H1","value":"Added Mid-Run"}]}""",
                Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"http://127.0.0.1:{server.Port}/plan/edit", editBody, cts.Token);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            // Its card is on the board NOW — same process, no restart, no session boundary crossed.
            var tasksJson = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/tasks", cts.Token);
            using var doc = JsonDocument.Parse(tasksJson);
            var cards = doc.RootElement.GetProperty("tasks").EnumerateArray().ToList();
            Assert.Contains(cards, c => c.GetProperty("taskId").GetString() == "H1.1");
            Assert.Contains(cards, c => c.GetProperty("taskId").GetString() == "H0.1");

            // The tracker view followed, so the engine can schedule the new stage too.
            var tracker = await File.ReadAllTextAsync(Path.Combine(repo, "TRACKER.md"), cts.Token);
            Assert.Contains("H1.1", tracker, StringComparison.Ordinal);

            await cts.CancelAsync();
            await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch (IOException) { }
        }
    }
}
