using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Commands;
using Conductor.Core.Events;
using Conductor.Hosting;
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
            skipStage: (_, _) => { }, approveAwaitingOwner: (_, _) => Task.CompletedTask);

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
            try { TestTemp.DeleteTree(repo); } catch (IOException) { }
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
            try { TestTemp.DeleteTree(repo); } catch (IOException) { }
        }
    }

    /// <summary>
    /// KS5.4 — the SPEND cap, the same two halves as the session cap above.
    /// <para>Half 1 (ordering): a `plan reload` raising <c>limits.maxRunCostUsd</c>, queued while the
    /// session that would trip the old cap is still running, is in force by the time the cap is
    /// compared — so the run never parks at all. The assertion is on the EVENT, not on the eventual
    /// status: a run that parked and was then un-parked by the same reload would reach session 2 too,
    /// and only <c>OwnerApprovalRequested</c> can tell the two apart. That event's absence is the
    /// ordering.</para>
    /// <para>Half 2 (un-park): a cap raised while the run is ALREADY parked on it resumes the run, the
    /// way G3.3 resumes a session-cap park. The operator's Settings edit is the approval.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveCostCap_ReloadRaisingItBeatsTheParkAndAlsoUnParks()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-costcap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        using var cts = new CancellationTokenSource();
        try
        {
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email cost@test");
            Git("config user.name Cost");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# c", CancellationToken.None);
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| H0.1 | never done | TODO | | |\n",
                CancellationToken.None);
            // Slow enough that the reload below is queued while session 1 is still spending — which is
            // the case the ordering is about. A reload queued between sessions would prove nothing.
            var agentScript = Path.Combine(repo, "fake-agent.cmd");
            await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
                "@echo off",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"noop session.\"}}",
                "ping -n 4 127.0.0.1 >nul",
                "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.05,\"tokens\":{\"input\":10,\"output\":5}}}",
                "exit /b 0",
                ""), CancellationToken.None);

            var planPath = Path.Combine(repo, "cost.plan.json");
            var seed = new PlanConfig
            {
                Name = "cost-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "H0", Title = "Cost", Sessions = 5 }],
                Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", agentScript, "{prompt}"], Provider = "opencode" },
                GatePolicy = "perSession",
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
            };
            seed.Limits.MaxRunCostUsd = 0.01m;  // one session's $0.05 is over it
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), CancellationToken.None);
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0), consoleSink: false);
            var store = host.Services.GetRequiredService<Conductor.Core.Store.IRunStore>();
            var inbox = host.Services.GetRequiredService<System.Collections.Concurrent.ConcurrentQueue<ControlCommand>>();
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

            // Half 1 — raise the cap in the plan file and queue the reload WHILE session 1 is running.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (state.Status != RunStatus.Running && DateTime.UtcNow < deadline)
                await Task.Delay(50, CancellationToken.None);
            Assert.Equal(RunStatus.Running, state.Status);
            var raised = PlanConfig.Load(planPath);
            // Above session 1's $0.05 and below session 2's $0.10: the reload has to clear the boundary
            // the run is about to reach, and the run has to reach a ceiling again shortly after so half
            // 2 has a park to un-park. A cap so high the run never parks again would time out here
            // having exhausted the stage's attempts instead — a different failure wearing this one's hat.
            raised.Limits.MaxRunCostUsd = 0.08m;
            raised.Save();
            inbox.Enqueue(ControlCommand.Of(ControlAction.ReloadPlan));

            deadline = DateTime.UtcNow.AddSeconds(90);
            while (state.SessionCounter < 2 && state.Status != RunStatus.AwaitingOwner && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.True(state.SessionCounter >= 2,
                $"the reload raised the cap before the boundary — the run should not have stopped (status {state.Status})");
            Assert.Empty(store.ReadAllEvents(state.RunId).OfType<OwnerApprovalRequested>());
            Assert.True(state.PerRunCostUsd > seed.Limits.MaxRunCostUsd,
                "the run really did spend past the ORIGINAL cap — otherwise this proves nothing");

            // Half 2 — let it park on the raised cap, then raise that one and watch the reload un-park.
            deadline = DateTime.UtcNow.AddSeconds(180);
            while (state.Status != RunStatus.AwaitingOwner && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.True(state.Status == RunStatus.AwaitingOwner,
                $"the run should have reached the raised $0.08 ceiling; it is {state.Status} " +
                $"after {state.SessionCounter} session(s) having spent ${state.BilledWindowCostUsd}");
            Assert.Equal(AwaitingOwnerReason.Budget, state.AwaitingOwnerReason);
            var parkedAt = state.SessionCounter;

            var raisedAgain = PlanConfig.Load(planPath);
            raisedAgain.Limits.MaxRunCostUsd = 100.00m;
            raisedAgain.Save();
            inbox.Enqueue(ControlCommand.Of(ControlAction.ReloadPlan));

            deadline = DateTime.UtcNow.AddSeconds(90);
            while (state.SessionCounter <= parkedAt && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.True(state.SessionCounter > parkedAt, "raising the cost cap should un-park the run it parked");
            Assert.Null(state.AwaitingOwnerReason);
            // The un-park is a reload, not an approval: no ceiling was granted and no approval counted.
            Assert.Equal(0, state.BudgetApprovals);
            Assert.Equal(0m, state.BudgetGrantUsd);

            await cts.CancelAsync();
            var code = await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal(130, code);
        }
        finally
        {
            await cts.CancelAsync();
            try { TestTemp.DeleteTree(repo); } catch (IOException) { }
        }
    }

    /// <summary>
    /// KS5.4 — a reload must not rewrite somebody else's park. The top-of-loop cap check runs after
    /// EVERY applied reload, and round 2 caught it converting an operator `pause` on a
    /// still-over-budget run into a fresh AwaitingOwner/Budget park with a second owner-approval
    /// event pushed to the queue — against this path's own rule, "an operator pause stays paused".
    /// The sequence is exactly the reachable one from the finding: park on budget, `pause` (applied
    /// immediately between sessions), edit the plan to a cap the spend is still over, reload. The run
    /// must come out of it Paused, holding the one park event it always had.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveBudgetPark_PauseThenReload_StaysPausedWithNoSecondApprovalRequest()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-pausereload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        using var cts = new CancellationTokenSource();
        try
        {
            ProcResult Git(string args) => ProcessRunner.Run("git",
                args.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo,
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email pause@test");
            Git("config user.name Pause");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# p", CancellationToken.None);
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| H0.1 | never done | TODO | | |\n",
                CancellationToken.None);
            var agentScript = Path.Combine(repo, "fake-agent.cmd");
            await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
                "@echo off",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"noop session.\"}}",
                "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.05,\"tokens\":{\"input\":10,\"output\":5}}}",
                "exit /b 0",
                ""), CancellationToken.None);

            var planPath = Path.Combine(repo, "pause.plan.json");
            var seed = new PlanConfig
            {
                Name = "pause-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "H0", Title = "Pause", Sessions = 5 }],
                Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", agentScript, "{prompt}"], Provider = "opencode" },
                GatePolicy = "perSession",
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
            };
            seed.Limits.MaxRunCostUsd = 0.01m;  // session 1's $0.05 parks the run
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), CancellationToken.None);
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0), consoleSink: false);
            var store = host.Services.GetRequiredService<Conductor.Core.Store.IRunStore>();
            var inbox = host.Services.GetRequiredService<System.Collections.Concurrent.ConcurrentQueue<ControlCommand>>();
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

            // The budget park, announced once.
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (!(state.Status == RunStatus.AwaitingOwner && state.AwaitingOwnerReason == AwaitingOwnerReason.Budget)
                   && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.Equal(RunStatus.AwaitingOwner, state.Status);
            Assert.Equal(AwaitingOwnerReason.Budget, state.AwaitingOwnerReason);
            // The event spine drains asynchronously (SqliteRunStore.Events), so the park's event is
            // polled for rather than demanded the instant the status flips.
            deadline = DateTime.UtcNow.AddSeconds(30);
            while (!store.ReadAllEvents(state.RunId).OfType<OwnerApprovalRequested>().Any() && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.Single(store.ReadAllEvents(state.RunId).OfType<OwnerApprovalRequested>());

            // The operator's pause — applied immediately, no session is running.
            inbox.Enqueue(ControlCommand.Of(ControlAction.PauseAfterSession));
            deadline = DateTime.UtcNow.AddSeconds(30);
            while (state.Status != RunStatus.Paused && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.Equal(RunStatus.Paused, state.Status);

            // The plan edit + reload, with the spend still over the edited cap.
            var edited = PlanConfig.Load(planPath);
            edited.Limits.MaxRunCostUsd = 0.02m;   // $0.05 spent stays over it
            edited.Save();
            inbox.Enqueue(ControlCommand.Of(ControlAction.ReloadPlan));
            deadline = DateTime.UtcNow.AddSeconds(60);
            while (!store.ReadAllEvents(state.RunId).OfType<PlanReloaded>().Any() && DateTime.UtcNow < deadline)
                await Task.Delay(100, CancellationToken.None);
            Assert.Single(store.ReadAllEvents(state.RunId).OfType<PlanReloaded>());

            // A few loop turns to let a wrong implementation do the wrong thing, then the claim: the
            // pause survived its reload, and nobody was asked to approve anything a second time.
            await Task.Delay(2000, CancellationToken.None);
            Assert.Equal(RunStatus.Paused, state.Status);
            Assert.Single(store.ReadAllEvents(state.RunId).OfType<OwnerApprovalRequested>());
            Assert.Equal(0, state.BudgetApprovals);
            Assert.Equal(0m, state.BudgetGrantUsd);

            await cts.CancelAsync();
            var code = await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal(130, code);
        }
        finally
        {
            await cts.CancelAsync();
            try { TestTemp.DeleteTree(repo); } catch (IOException) { }
        }
    }
}
