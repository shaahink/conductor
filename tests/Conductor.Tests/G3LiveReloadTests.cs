using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Commands;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>G3.2 — live plan reload. Unit half: the reload-plan verb is always DEFERRED by the
/// dispatcher (mid-session or not) and consumed exactly once; the swap itself only ever happens at
/// the run loop's session boundary. Integration half (Category=Integration): a paused run whose plan
/// file is edited + reloaded actually runs its next session against the new plan, proven by the
/// StageEntered event carrying the edited title and a PlanReloaded event in the run's event log.</summary>
public sealed class G3LiveReloadTests
{
    private sealed class RecordingSink : IProgressSink
    {
        public readonly List<ToastMessage> Toasts = [];
        public void Log(string line) { }
        public void AgentEvent(AgentEvent ev) { }
        public void Snapshot(DashboardSnapshot snap) { }
        public ControlCommand? PollControl() => null;
        public void Toast(ToastMessage toast) => Toasts.Add(toast);
    }

    private static ControlDispatcher Dispatcher(RecordingSink sink, RunState state) =>
        new(new PlanConfig { Name = "p", Repo = ".", Tracker = "T.md" }, state, sink,
            NullEventSink.Instance, log: _ => { }, save: () => { }, deleteControlFile: () => { },
            skipStage: (_, _) => { }, approveAwaitingOwner: _ => Task.CompletedTask);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReloadPlan_IsAlwaysDeferred_AndConsumedOnce(bool inSession)
    {
        var sink = new RecordingSink();
        var d = Dispatcher(sink, new RunState());

        var action = await d.DispatchAsync(ControlCommand.Of(ControlAction.ReloadPlan), inSession, CancellationToken.None);

        Assert.Equal(ControlAction.ReloadPlan, action);
        var toast = Assert.Single(sink.Toasts);
        Assert.Contains("queued", toast.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(d.ConsumeReloadPending());   // pending exactly once…
        Assert.False(d.ConsumeReloadPending());  // …then drained
    }

    [Fact]
    public async Task NoReloadRequested_NothingPending()
    {
        var d = Dispatcher(new RecordingSink(), new RunState());
        await d.DispatchAsync(ControlCommand.Of(ControlAction.Heartbeat), inSession: false, CancellationToken.None);
        Assert.False(d.ConsumeReloadPending());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PausedRun_PlanFileEditPlusReload_NextSessionUsesTheNewPlan()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        try
        {
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email reload@test");
            Git("config user.name Reload");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r");
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| H0.1 | cp | TODO | | |\n");
            var agentScript = Path.Combine(repo, "fake-agent.cmd");
            await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
                "@echo off",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"noop session.\"}}",
                "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
                "exit /b 0",
                ""));

            // The plan must come from a FILE — that is what the live reload re-reads.
            var planPath = Path.Combine(repo, "test.plan.json");
            var seed = new PlanConfig
            {
                Name = "reload-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "H0", Title = "Original Title", Sessions = 1 }],
                Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", agentScript, "{prompt}"], Provider = "opencode" },
                GatePolicy = "perSession",
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
            };
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: true, MaxSessions: 0, StartPaused: true), consoleSink: false);
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (state.Status != RunStatus.Paused && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.Equal(RunStatus.Paused, state.Status);

            // Author while parked: edit the plan FILE (what the Face's /plan/edit does), then queue
            // the reload + resume — exactly the verbs the control plane enqueues.
            var edited = PlanConfig.Load(planPath);
            edited.Stages[0].Title = "Reloaded Title";
            edited.Save();

            var inbox = host.Services.GetRequiredService<System.Collections.Concurrent.ConcurrentQueue<ControlCommand>>();
            inbox.Enqueue(ControlCommand.Of(ControlAction.ReloadPlan));
            inbox.Enqueue(ControlCommand.Of(ControlAction.ResumeRun));

            var code = await runTask.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(0, code);

            var store = host.Services.GetRequiredService<Conductor.Core.Store.IRunStore>();
            var events = store.ReadAllEvents(state.RunId);
            var reloaded = Assert.Single(events.OfType<PlanReloaded>());
            Assert.True(reloaded.PlanVersion >= 2); // Save() bumped it
            var entered = Assert.Single(events.OfType<StageEntered>());
            Assert.Equal("Reloaded Title", entered.Title); // session 1 ran against the SWAPPED plan
            Assert.Single(state.History);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveSessionCap_ParksAtBoundary_AndRaisingItResumes()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-cap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        using var cts = new CancellationTokenSource();
        try
        {
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email cap@test");
            Git("config user.name Cap");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# c", CancellationToken.None);
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| H0.1 | never done | TODO | | |\n",
                CancellationToken.None);
            var agentScript = Path.Combine(repo, "fake-agent.cmd");
            await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
                "@echo off",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"noop session.\"}}",
                "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
                "exit /b 0",
                ""), CancellationToken.None);

            var planPath = Path.Combine(repo, "cap.plan.json");
            var seed = new PlanConfig
            {
                Name = "cap-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "H0", Title = "Cap", Sessions = 5 }],
                Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", agentScript, "{prompt}"], Provider = "opencode" },
                GatePolicy = "perSession",
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
            };
            seed.Limits.MaxSessions = 1; // cap-down: park after session 1
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), CancellationToken.None);
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0), consoleSink: false);
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

            // Gate half 1 — limit-down PARKS: session 1 runs, then the boundary parks the run
            // (Paused + ParkedBySessionCap + a reason), it does NOT hard-stop or crash.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!(state.Status == RunStatus.Paused && state.ParkedBySessionCap) && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.True(state.ParkedBySessionCap, "run should park when the session cap is reached");
            Assert.Equal(RunStatus.Paused, state.Status);
            Assert.Single(state.History);
            Assert.Contains("session cap", state.AttentionReason, StringComparison.OrdinalIgnoreCase);
            Assert.False(runTask.IsCompleted); // parked, still up — the dashboard stays alive

            // Gate half 2 — limit-up CONTINUES: raise the cap in the plan file (what the Face
            // Settings edit does) and queue the live reload; the reload itself is the resume.
            var edited = PlanConfig.Load(planPath);
            edited.Limits.MaxSessions = 3;
            edited.Save();
            var inbox = host.Services.GetRequiredService<System.Collections.Concurrent.ConcurrentQueue<ControlCommand>>();
            inbox.Enqueue(ControlCommand.Of(ControlAction.ReloadPlan));

            deadline = DateTime.UtcNow.AddSeconds(60);
            while (state.SessionCounter < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.True(state.SessionCounter >= 2, "raising the cap should let the next session run");
            Assert.False(state.ParkedBySessionCap);

            await cts.CancelAsync();
            var code = await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal(130, code); // clean cancellation path, state saved
        }
        finally
        {
            await cts.CancelAsync();
            try { Directory.Delete(repo, recursive: true); } catch (IOException) { }
        }
    }
}
