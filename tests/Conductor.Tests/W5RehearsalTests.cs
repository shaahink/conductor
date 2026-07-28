using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Core.Http;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;
using Conductor.Planning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// W5.1 truth gates — the two defects the credential-free dress rehearsal exposed, both of them
/// invisible to a single-session test and to every in-process harness written before it.
///
/// <para><b>1. The engine scheduled on the declaration, not the graph.</b> W1 established that the
/// graph is the runtime truth and declared work is only a declaration. Every reader moved except the
/// run loop, which kept taking checkpoint STATUS from the progress provider. On the markdown-table
/// path that is invisible: the tracker is regenerated from the graph after each session, so the
/// declaration agrees a moment later. An inline (<c>plan-checkpoints</c>) plan — which is what every
/// W4.1 import produces — has no write-back at all, so its statuses read <c>TODO</c> for the life of
/// the run. The loop re-picked delivered work, the prompt's card section rendered empty (it reads the
/// graph, where the card was already done), the circuit breaker correctly called that no progress and
/// parked, and <c>AllEffectivelyDone</c> could never be true — so a plan imported from a document
/// could not reach <c>RunFinished</c>, the event no run had ever emitted.</para>
///
/// <para><b>2. The plan reload skipped the control plane.</b> <c>ApplyPlanReload</c> swapped the fresh
/// plan into the context, gates, lanes and dispatcher; the HTTP server — which every Face surface
/// reads — cached its own reference and was never on that list. A plan edit reached the engine and
/// the generated tracker while the TUI served the pre-edit plan for the rest of the run, which is
/// criterion 2 ("tweak the plan and the TUI reflects it") failing on the read side.</para>
/// </summary>
public sealed class W5RehearsalTests
{
    // ---------------------------------------------------------------- the projection

    [Fact]
    public void WorkSnapshot_TakesStatusFromTheGraph_NotTheFrozenDeclaration()
    {
        using var repo = new TempRepo("w5-proj");
        using var store = repo.OpenStore();
        var runId = "r1";
        store.InitializeRun(runId, "p", repo.Path, "main", "1.0.0");
        store.SeedCheckpoints(runId, [("T1.1", "T1", "first", "TODO", "", ""), ("T1.2", "T1", "second", "TODO", "", "")]);
        store.UpdateCheckpoint(runId, "T1.1", "DONE", "abc1234", "evidence.txt", source: "agent");
        store.FlushEvents();

        // The declaration a W4.1 import writes: every row TODO, forever, with nothing to update it.
        var declared = new TrackerSnapshot
        {
            Checkpoints =
            [
                Conductor.Core.CheckpointRow.Create(new ProgressConventions(), "T1.1", "first", "TODO", "", ""),
                Conductor.Core.CheckpointRow.Create(new ProgressConventions(), "T1.2", "second", "TODO", "", ""),
            ],
            HandoffBlock = "carried over from the declared source",
        };

        var work = WorkSnapshot.Read(store, runId, () => declared);
        Assert.True(work.ById("T1.1")!.IsDone);
        Assert.False(work.ById("T1.2")!.IsDone);
        Assert.False(work.StageDone("T1"));
        // The handoff block is view-only prose the graph does not model, so it still comes from the
        // declared read.
        Assert.Equal("carried over from the declared source", work.HandoffBlock);

        store.UpdateCheckpoint(runId, "T1.2", "DONE", "def5678", "evidence2.txt", source: "agent");
        store.FlushEvents();
        Assert.True(WorkSnapshot.Read(store, runId, () => declared).StageDone("T1"));
    }

    [Fact]
    public void WorkSnapshot_FallsBackToTheDeclaration_BeforeAnythingIsSeeded()
    {
        using var repo = new TempRepo("w5-proj-empty");
        using var store = repo.OpenStore();
        store.InitializeRun("r1", "p", repo.Path, "main", "1.0.0");
        var declared = new TrackerSnapshot
        {
            Checkpoints = [Core.CheckpointRow.Create(new ProgressConventions(), "T1.1", "first", "TODO", "", "")],
        };

        // An unseeded graph is a run at its very start: the declaration is all there is. And with no
        // store at all (a dry run) nothing changes either — never an empty board.
        Assert.Single(WorkSnapshot.Read(store, "r1", () => declared).Checkpoints);
        Assert.Single(WorkSnapshot.Read(null, "r1", () => declared).Checkpoints);
        // A tracker that genuinely has no parseable rows still reads as none, so the run loop's
        // "no checkpoint rows" park is untouched.
        Assert.Empty(WorkSnapshot.Read(store, "r1", () => new TrackerSnapshot()).Checkpoints);
    }

    // ---------------------------------------------------------------- the live gate

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImportedPlan_DrivesToCompletion_AndEmitsRunFinished()
    {
        // The rehearsal in miniature, and the regression gate for defect 1: an inline-declared plan
        // (what `conductor plan import` produces) driven across two stages to a finished run. Before
        // the fix this parks in stage T1 with the first card re-picked forever.
        using var repo = new TempRepo("w5-finish");
        var plan = await repo.ScaffoldInlinePlanAsync(
            [("T1", "First"), ("T2", "Second")],
            [("T1.1", "T1", "the first card"), ("T2.1", "T2", "the second card")]);

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: false, MaxSessions: 0), consoleSink: false);
        var store = host.Services.GetRequiredService<IRunStore>();
        var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);

        // The agent claims through the one claim path, one card per session, exactly as
        // `conductor task --done` does from inside the worker.
        await ClaimWhenSessionStartsAsync(repo.Path, store, state.RunId, 1, "T1.1");
        await ClaimWhenSessionStartsAsync(repo.Path, store, state.RunId, 2, "T2.1");

        var code = await runTask.WaitAsync(TimeSpan.FromSeconds(240), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal(RunStatus.Completed, state.Status);

        var events = store.ReadAllEvents(state.RunId);
        var finished = Assert.Single(events.OfType<RunFinished>());
        Assert.Equal(nameof(RunStatus.Completed), finished.Status);
        Assert.Equal(2, finished.CheckpointsTotal);
        Assert.Equal(2, finished.CheckpointsDone);
        // RunFinished is terminal: nothing may follow it, or `status` reports the run as idle or
        // interrupted after it completed.
        Assert.Equal(finished, events.Where(e => e is not TokenDelta).Last());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheLastCheckpointsVerification_RunsBeforeTheRunCloses()
    {
        // Defect 3, which reading the graph directly exposed: done-ness used to lag a tracker
        // regeneration behind the claim, so a queued verify always got its turn before the loop
        // noticed the plan was finished. Without that lag, completion would close the run over the
        // top of the verification it had just queued — leaving the plan's LAST card the only one
        // nobody checked.
        using var repo = new TempRepo("w5-lastverify");
        var plan = await repo.ScaffoldInlinePlanAsync(
            [("T1", "Only")], [("T1.1", "T1", "the only card")], verify: true);

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: false, MaxSessions: 0), consoleSink: false);
        var store = host.Services.GetRequiredService<IRunStore>();
        var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);

        await ClaimWhenSessionStartsAsync(repo.Path, store, state.RunId, 1, "T1.1");

        var code = await runTask.WaitAsync(TimeSpan.FromSeconds(240), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal(RunStatus.Completed, state.Status);

        var verify = Assert.Single(state.History, h => h.Kind == SessionKind.Verify);
        var deliver = Assert.Single(state.History, h => h.Kind == SessionKind.Deliver);
        Assert.True(verify.Number > deliver.Number, "the verify must follow the delivery it checks");
        Assert.Single(store.ReadAllEvents(state.RunId).OfType<RunFinished>());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PlanEditMidRun_ReachesTheControlPlane_WithoutARestart()
    {
        // Regression gate for defect 2, over the wire the Face actually uses.
        using var repo = new TempRepo("w5-reload");
        var plan = await repo.ScaffoldInlinePlanAsync(
            [("T1", "First"), ("T2", "Rename me")], [("T1.1", "T1", "a card"), ("T2.1", "T2", "another card")]);

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: false, MaxSessions: 0, ControlPlane: true,
                ControlPlanePort: ProbeFreePort(), StartPaused: true), consoleSink: false);
        var server = host.Services.GetRequiredService<ControlPlaneServer>();
        Assert.True(server.Start());
        var baseUrl = $"http://127.0.0.1:{server.Port}";
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);

        using var cts = new CancellationTokenSource();
        var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
        try
        {
            await WaitForAsync(async () =>
                (await http.GetStringAsync($"{baseUrl}/state", CancellationToken.None))
                    .Contains("Rename me", StringComparison.Ordinal), TimeSpan.FromSeconds(60));

            const string NewTitle = "Renamed while the run was live";
            var res = await http.PostAsJsonAsync($"{baseUrl}/plan/edit",
                new PlanEditRequestDto([new PlanEditDto("stage", "T2", "title", NewTitle)]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web), CancellationToken.None);
            // 202: the edit is validated and written here, and the LOOP adopts it at its next boundary.
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

            // The reload lands at the loop's session boundary, which a PAUSED loop still reaches —
            // it drains control and applies reloads before parking. No restart, no resume.
            var landed = await WaitForAsync(async () =>
                (await http.GetStringAsync($"{baseUrl}/state", CancellationToken.None))
                    .Contains(NewTitle, StringComparison.Ordinal),
                TimeSpan.FromSeconds(60));
            Assert.True(landed, "the control plane kept serving the pre-edit plan after /plan/edit");
        }
        finally
        {
            await cts.CancelAsync();
            try { await runTask.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static async Task ClaimWhenSessionStartsAsync(string repo, IRunStore engineStore, string runId,
        int sessionNumber, string checkpointId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (engineStore.ReadAllEvents(runId).OfType<SessionStarted>().Any(s => s.Number == sessionNumber)) break;
            await Task.Delay(100, CancellationToken.None);
        }
        using var cli = new SqliteRunStore(Path.Combine(repo, ".conductor", "run.db"),
            NullLogger<SqliteRunStore>.Instance);
        cli.UpdateCheckpoint(runId, checkpointId, "DONE", "fake1234", "claimed via task --done", source: "agent");
    }

    private static int ProbeFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try { if (await condition()) return true; } catch (HttpRequestException) { }
            await Task.Delay(200, CancellationToken.None);
        }
        return false;
    }

    /// <summary>A throwaway git repo with a plan that declares its work INLINE — the shape
    /// <c>conductor plan import</c> writes, and the one no multi-session test covered.</summary>
    private sealed class TempRepo : IDisposable
    {
        public string Path { get; }

        public TempRepo(string tag)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"conductor-{tag}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            Git("init -b main");
            Git("config user.email w5@test");
            Git("config user.name W5");
            File.WriteAllText(System.IO.Path.Combine(Path, "README.md"), "# r");
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
        }

        public SqliteRunStore OpenStore() => new(
            System.IO.Path.Combine(Path, ".conductor", "run.db"), NullLogger<SqliteRunStore>.Instance);

        public async Task<PlanConfig> ScaffoldInlinePlanAsync(
            (string Id, string Title)[] stages, (string Id, string StageId, string Title)[] checkpoints,
            bool verify = false)
        {
            // Commits every session, so a green verdict advances the workflow. PowerShell rather than
            // cmd.exe: cmd truncates its command line at the first newline, and {prompt} is multi-line.
            var agentScript = System.IO.Path.Combine(Path, "fake-agent.ps1");
            await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
                "param([string]$Repo, [string]$Prompt = \"\")",
                "function O($type, $part) {",
                "    $o = @{ type = $type; session_id = 'fake' }",
                "    if ($null -ne $part) { $o.part = $part }",
                "    Write-Output ($o | ConvertTo-Json -Compress -Depth 6)",
                "}",
                "O 'step_start' $null",
                // A verify prompt demands one {\"score\":…} object; anything else burns a stage attempt.
                "if ($Prompt -match 'VERIFICATION session') {",
                "    O 'text' @{ text = '{\"score\":95,\"findings\":[],\"verdict\":\"PASS\"}' }",
                "    exit 0",
                "}",
                "Add-Content (Join-Path $Repo 'work.txt') ([Guid]::NewGuid().ToString())",
                "$null = git -C $Repo add -A 2>&1",
                "$null = git -C $Repo commit -m session --no-gpg-sign --quiet 2>&1",
                "O 'step_finish' @{ cost = 0.0001; tokens = @{ input = 10; output = 5; reasoning = 0; cache = @{ read = 0 } } }",
                "O 'text' @{ text = 'SESSION-RESULT: delivered.' }",
                "exit 0",
                ""), Encoding.ASCII, CancellationToken.None);

            // An inline plan still DECLARES a tracker (plan validation requires the file to exist) —
            // it is simply a generated view now, so it starts empty of rows and the generator fills it.
            await File.WriteAllTextAsync(System.IO.Path.Combine(Path, "TRACKER.md"),
                "# tracker\n\n## Handoff\nnone.\n", CancellationToken.None);

            var planPath = System.IO.Path.Combine(Path, "w5.plan.json");
            var seed = new PlanConfig
            {
                Name = $"w5-{Guid.NewGuid():N}"[..12],
                Repo = Path.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [.. stages.Select((s, i) => new StageConfig
                {
                    Id = s.Id,
                    Title = s.Title,
                    Sessions = 2,
                    DependsOn = i == 0 ? null : [stages[i - 1].Id],
                })],
                Agent = new AgentConfig
                {
                    Command = "powershell",
                    Args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", agentScript,
                            "-Repo", Path.Replace("\\", "/"), "-Prompt", "{prompt}"],
                    Provider = "opencode",
                },
                GatePolicy = "perSession",
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
                // Deliver-only by default: what is under test is the scheduling truth, not the QA dial
                // (W4.4 owns that). `verify: true` puts the classic verify step back for the one test
                // that needs a verification to still be pending when the plan finishes.
                Pipeline = verify ? null : new PipelineRules { Qa = new QaRule { Mode = "off" } },
                Progress = new ProgressConfig
                {
                    Kind = "plan-checkpoints",
                    Checkpoints = [.. checkpoints.Select(c => new PlanCheckpoint { Id = c.Id, Title = c.Title, Status = "TODO" })],
                },
            };
            seed.Report.Commit = false;
            seed.Limits.MaxRunCostUsd = 1m;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), CancellationToken.None);
            return PlanConfig.Load(planPath);
        }

        private ProcResult Git(string args) => ProcessRunner.Run("git",
            args.Split(' ', StringSplitOptions.RemoveEmptyEntries), Path,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        /// <summary>git marks pack/object files read-only and a recursive delete trips over them — a
        /// cleanup failure must never be reported as a test failure.</summary>
        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { }
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
        }
    }
}
