using System.Diagnostics;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Integrations.Github;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS9.2 — the failure posture, asserted rather than asserted-about.
///
/// <para>The bar the contract sets is strong: with the endpoint dead, the run must produce an
/// outcome <b>identical</b> to the same run with sync disabled. So the test runs the same sequence
/// twice — once with no mirror at all, once with a mirror whose every request fails — and compares
/// the two event logs event for event. "One line is logged and the cursor does not advance" is the
/// designed difference, and it is the ONLY difference allowed.</para>
///
/// <para>The second claim is about time, not correctness: a boundary must not wait on the network.
/// <c>RunContext.MirrorBoard</c> is <c>_ = Mirror?.Fire(...)</c>, so what has to be true is that
/// <see cref="GithubMirror.Fire"/> returns before the request it started does — measured against a
/// handler that hangs, because a handler that fails fast cannot tell the two designs apart.</para>
/// </summary>
public sealed class KS9_2NetworkFailureDoesNotBlockTheLoopTests
{
    private const string Repo = "owner/scratch";

    /// <summary>A handler that never answers in the lifetime of a test — the shape of a dead port
    /// that accepts and then does nothing, which is strictly worse than one that refuses.</summary>
    private sealed class HangingGithub : HttpMessageHandler
    {
        public int Started;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Started);
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private static SqliteRunStore Store(string dir, string runId)
    {
        Directory.CreateDirectory(dir);
        var store = new SqliteRunStore(Path.Combine(dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        store.SetRunId(runId);
        store.InitializeRun(runId, "karvansara", "C:/code/conductor", "feat/karvansara",
            new EngineStamp("0.4.1", "abc123", false));
        return store;
    }

    private static ConductorEvent[] Session(int number) =>
    [
        new TaskAdded { TaskId = "A" + number, CheckpointId = "A" + number, Title = "work", Source = "plan", Kind = "checkpoint", StageId = "S1" },
        new TaskStatusChanged { TaskId = "A" + number, Status = "done", Source = "agent", Commit = "cafe000000000000" },
        new SessionFinished { Number = number, StageId = "S1", Outcome = "Delivered", NewlyDone = ["A" + number], CostUsd = 1m },
    ];

    /// <summary>One run's worth of boundaries, with or without a mirror wired in. Returns the event
    /// log the run left behind — the thing that must not depend on GitHub.</summary>
    private static async Task<List<string>> DriveAsync(GithubMirror? mirror, SqliteRunStore store, string runId)
    {
        for (var n = 1; n <= 3; n++)
        {
            foreach (var e in Session(n)) store.Emit(e);
            store.FlushEvents();
            if (mirror is not null)
                await mirror.ReconcileAsync($"session {n} end").ConfigureAwait(true);
        }
        store.FlushEvents();
        return [.. store.ReadAllEvents(runId).Select(e => $"{e.Seq}:{e.GetType().Name}")];
    }

    [Fact]
    public async Task ADeadEndpointProducesTheSameRunAsNoMirrorAtAll()
    {
        var quiet = Path.Combine(Path.GetTempPath(), "ks92q-" + Guid.NewGuid().ToString("N")[..8]);
        var dead = Path.Combine(Path.GetTempPath(), "ks92d-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            List<string> withoutMirror;
            using (var store = Store(quiet, "run-quiet00000"))
                withoutMirror = await DriveAsync(null, store, "run-quiet00000").ConfigureAwait(true);

            List<string> withDeadMirror;
            var log = new List<string>();
            using (var store = Store(dead, "run-dead000000"))
            using (var fake = new FakeGithub { Outage = "No connection could be made because the target machine actively refused it" })
            using (var mirror = new GithubMirror(store, "run-dead000000", Repo, "t", "conductor", true, log.Add, fake))
            {
                withDeadMirror = await DriveAsync(mirror, store, "run-dead000000").ConfigureAwait(true);

                // The designed difference, and the whole of it: the cursor never moved, and every
                // failed pass said so out loud exactly once.
                Assert.Equal(0L, store.ReadGithubCursor("run-dead000000", Repo).Seq);
                Assert.Equal(3, mirror.FailedPasses);
                Assert.Equal(3, log.Count(l => l.Contains("cursor held", StringComparison.Ordinal)));
            }

            Assert.Equal(withoutMirror, withDeadMirror);
        }
        finally
        {
            foreach (var d in new[] { quiet, dead })
                try { Directory.Delete(d, recursive: true); } catch (IOException) { /* not the assertion */ }
        }
    }

    [Fact]
    public async Task ABoundaryDoesNotWaitForTheNetwork()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ks92h-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var store = Store(dir, "run-hang000000");
            foreach (var e in Session(1)) store.Emit(e);
            store.FlushEvents();

            using var hanging = new HangingGithub();
            using var mirror = new GithubMirror(store, "run-hang000000", Repo, "t", "conductor", true, _ => { }, hanging);

            // This is literally what RunContext.MirrorBoard does at every boundary but the last.
            var sw = Stopwatch.StartNew();
            var inFlight = mirror.Fire("session 1 end");
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 1000,
                $"the boundary waited {sw.ElapsedMilliseconds}ms on a hung endpoint — a mirror may not be back-pressure");
            Assert.False(inFlight.IsCompleted);

            // The pass really did start: the claim is "does not block", not "does not run".
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref hanging.Started) == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(25).ConfigureAwait(true);
            Assert.True(Volatile.Read(ref hanging.Started) > 0);
            Assert.Equal(0L, store.ReadGithubCursor("run-hang000000", Repo).Seq);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* not the assertion */ }
        }
    }

    /// <summary>Off by default is a code path, not a promise: with no <c>github</c> block, with the
    /// block disabled, and with the live mirror switched off, there is no mirror object at all — so
    /// the boundaries call nothing, which is exactly what "identical to sync disabled" means.</summary>
    [Fact]
    public void NoMirrorExistsUnlessThePlanAsksForOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ks92c-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var store = Store(dir, "run-cfg0000000");
            var lines = new List<string>();

            Assert.Null(GithubMirror.TryCreate(new PlanConfig { Name = "p", Repo = dir }, store, "run-cfg0000000", lines.Add));
            Assert.Null(GithubMirror.TryCreate(
                new PlanConfig { Name = "p", Repo = dir, Github = new GithubConfig { Enabled = false, Repo = Repo } },
                store, "run-cfg0000000", lines.Add));
            Assert.Null(GithubMirror.TryCreate(
                new PlanConfig { Name = "p", Repo = dir, Github = new GithubConfig { Enabled = true, LiveMirror = false, Repo = Repo } },
                store, "run-cfg0000000", lines.Add));
            // A null store (no run.db) has nothing to reconcile against and no cursor to hold.
            Assert.Null(GithubMirror.TryCreate(
                new PlanConfig { Name = "p", Repo = dir, Github = new GithubConfig { Enabled = true, Repo = Repo } },
                null, "run-cfg0000000", lines.Add));

            Assert.Empty(lines);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* not the assertion */ }
        }
    }
}
