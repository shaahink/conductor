using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

using Conductor.Core;
using Conductor.Core.Commands;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Lanes;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Core.Providers;
using Conductor.Models;
using Conductor.Planning;

using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS2.6 — park hygiene: the flood, the silence, and the spin.
///
/// <para><b>The incident.</b> On 2026-08-02 a tracker handoff MENTIONED the escalation token in prose.
/// The match is a plain case-insensitive substring over the handoff block and STAYS one (house
/// decree: fix the flood, not the match), so the loop read it as a request for a human, parked,
/// pushed, and <c>continue</c>d — and because the park's idle delay was gated on <c>!DryRun</c>, the
/// next iteration arrived instantly and did it all again. Roughly two hundred phone notifications for
/// one unchanged fact.</para>
///
/// <para><b>The token is never written literally in this file.</b> It is assembled at runtime
/// (<see cref="Escalation"/>), because the run driving this repo reads its own tracker with the same
/// substring match — a literal in a file that run reads parks it exactly as hard as raising one.</para>
///
/// <para><b>Why the one-push clause and the zero-push clause are measured differently.</b> They would
/// contradict each other measured at one layer: the flood happened UNDER <c>--dry-run</c>, and a dry
/// run must now send nothing at all. So the replay drives the real loop with dry-run OPTIONS (nothing
/// spawns, nothing spends) and a LIVE <see cref="ParkNotifier"/>, counting what the engine asks to
/// send — one, where it used to be hundreds — and the dry-run clause drives the same loop with the
/// notifier the composition root actually builds under <c>--dry-run</c>, counting what leaves:
/// nothing, on any leg. The live-run replay below closes the pair.</para>
/// </summary>
public sealed class KS2_6ParkHygieneTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks26-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _open = new();

    /// <summary>The escalation token, built rather than typed. See the class remarks.</summary>
    private static string Escalation => "HUMAN" + ":";

    public KS2_6ParkHygieneTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var d in _open) { try { d.Dispose(); } catch (ObjectDisposedException) { } }
        try { TestTemp.DeleteTree(_tmp); } catch (IOException) { }
    }

    // ───────────────────────────────── the limiter itself

    [Fact]
    public void OneIncidentNotifiesOnceHoweverManyTimesItIsRaised()
    {
        var notifier = new ParkNotifier(dryRun: false);

        Assert.True(notifier.Admit("NeedsHuman", "agent asked for a human in the tracker handoff"));
        for (var i = 0; i < 200; i++)
            Assert.False(notifier.Admit("NeedsHuman", "agent asked for a human in the tracker handoff"));

        Assert.Equal(200, notifier.SuppressedInIncident);
    }

    [Fact]
    public void ANewDistinctReasonOpensANewIncidentAndDoesNotify()
    {
        var notifier = new ParkNotifier(dryRun: false);

        Assert.True(notifier.Admit("NeedsHuman", "tracker has no parseable checkpoint rows"));
        Assert.False(notifier.Admit("NeedsHuman", "tracker has no parseable checkpoint rows"));
        Assert.True(notifier.Admit("NeedsHuman", "auth preflight failed before session 1"));
        Assert.False(notifier.Admit("NeedsHuman", "auth preflight failed before session 1"));

        // Same reason, different status: still a different incident.
        Assert.True(notifier.Admit("AwaitingOwner", "auth preflight failed before session 1"));
    }

    /// <summary>Whitespace and case are re-rendering, not a new fact.</summary>
    [Fact]
    public void TheSameReasonInDifferentCaseOrSpacingIsTheSameIncident()
    {
        var notifier = new ParkNotifier(dryRun: false);

        Assert.True(notifier.Admit("NeedsHuman", "Checkpoint(s) newly BLOCKED: S1.2"));
        Assert.False(notifier.Admit("NeedsHuman", "  checkpoint(s) newly blocked: s1.2  "));
    }

    [Fact]
    public void TheCapIsConfigurableAndZeroMeansUncapped()
    {
        var three = new ParkNotifier(dryRun: false, maxPerIncident: 3);
        Assert.True(three.Admit("NeedsHuman", "r"));
        Assert.True(three.Admit("NeedsHuman", "r"));
        Assert.True(three.Admit("NeedsHuman", "r"));
        Assert.False(three.Admit("NeedsHuman", "r"));

        var uncapped = new ParkNotifier(dryRun: false, maxPerIncident: 0);
        for (var i = 0; i < 50; i++) Assert.True(uncapped.Admit("NeedsHuman", "r"));

        // A nonsense cap must not mean silence.
        Assert.True(new ParkNotifier(dryRun: false, maxPerIncident: -4).Admit("NeedsHuman", "r"));
        Assert.Equal(ParkNotifier.DefaultMaxPerIncident, new ParkNotifier(false, -4).MaxPerIncident);
    }

    [Fact]
    public void RealWorkClosesTheIncidentSoTheSameCauseIsNewsAgain()
    {
        var notifier = new ParkNotifier(dryRun: false);

        Assert.True(notifier.Admit("NeedsHuman", "same cause"));
        Assert.False(notifier.Admit("NeedsHuman", "same cause"));

        notifier.Resolve();                 // a session ran
        Assert.Null(notifier.OpenIncident);
        Assert.True(notifier.Admit("NeedsHuman", "same cause"));
    }

    [Fact]
    public void UnderDryRunNothingIsEverAdmitted()
    {
        var notifier = new ParkNotifier(dryRun: true, maxPerIncident: 0);

        Assert.False(notifier.AllowOneOff());
        Assert.False(notifier.Admit("NeedsHuman", "anything"));
        Assert.False(notifier.Admit("AwaitingOwner", "anything else"));
    }

    /// <summary>The plan key is READ — a limits key nothing consults is the SF0.1 bug by another road.</summary>
    [Fact]
    public void ThePlanKeyReachesTheLimiterThroughTheRunContext()
    {
        Assert.Equal(ParkNotifier.DefaultMaxPerIncident, new LimitsConfig().MaxPushesPerIncident);

        var rig = Rig(dryRun: false, handoff: "last: nothing.", limits: l => l.MaxPushesPerIncident = 4);
        Assert.Equal(4, rig.Ctx.Notifier.MaxPerIncident);

        var uncapped = Rig(dryRun: false, handoff: "last: nothing.", limits: l => l.MaxPushesPerIncident = 0);
        Assert.Equal(0, uncapped.Ctx.Notifier.MaxPerIncident);
    }

    /// <summary>The house decree, pinned: fix the FLOOD, not the MATCH. The escalation token stays a
    /// plain case-insensitive substring over the handoff block, so prose describing the convention
    /// still parks the run — it just no longer buzzes a phone two hundred times about it. Narrowing
    /// the match (to a line start, say) would silently un-park runs that mean it.</summary>
    [Fact]
    public void TheEscalationTokenIsStillAPlainCaseInsensitiveSubstringOverTheHandoff()
    {
        var conventions = new ProgressConventions();

        Assert.Equal(Escalation, conventions.HumanToken);
        Assert.True(conventions.MentionsHuman($"last: stuck. {Escalation} pick the auth provider."));
        Assert.True(conventions.MentionsHuman($"describing the convention ({Escalation.ToLowerInvariant()}) mid-sentence"));
        Assert.True(conventions.MentionsHuman(Escalation));
        Assert.False(conventions.MentionsHuman("last: nothing at all, carrying on."));
    }

    // ───────────────────────────────── the 2026-08-02 replay

    /// <summary>The incident itself, driven through the REAL run loop. Dry-run options (nothing
    /// spawns) with a live notifier, so what is counted is what the engine ASKS to send. Before
    /// KS2.6 this loop never idled and never counted: the same park was raised and pushed on every
    /// iteration until something stopped the process.</summary>
    [Fact]
    public async Task TheEscalationTokenReplayProducesExactlyOneNotification()
    {
        var rig = Rig(dryRun: true, handoff: $"last: stuck. {Escalation} pick the auth provider.",
            notifier: new ParkNotifier(dryRun: false));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var exit = await rig.Loop.RunAsync(cts.Token);

        Assert.Equal(0, exit);
        Assert.Equal(RunStatus.NeedsHuman, rig.Ctx.State.Status);
        Assert.Equal(1, rig.Telegram.Count(m => m.Contains("needs attention", StringComparison.Ordinal)));
        // The only other push in the whole replay is the run-start line — no repeats of anything,
        // on any leg: two Telegram sentences, one keyboard, two notify-command invocations.
        Assert.Equal(2, rig.Telegram.Sent.Count);
        Assert.Single(rig.Telegram.Keyboards);
        Assert.Equal(2, rig.NotifyCommandRuns());
    }

    /// <summary>…and in the shape a real overnight run has: live options, so the park idles at 800ms
    /// and the loop keeps turning. Three seconds is ~4 iterations; the pre-KS2.6 code pushed on every
    /// one of them once the handoff was re-read.</summary>
    [Fact]
    public async Task TheSameParkHeldForSecondsStillNotifiedOnlyOnce()
    {
        var rig = Rig(dryRun: false, handoff: $"last: stuck. {Escalation} pick the auth provider.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await rig.Loop.RunAsync(cts.Token);

        Assert.Equal(RunStatus.NeedsHuman, rig.Ctx.State.Status);
        Assert.Equal(1, rig.Telegram.Count(m => m.Contains("needs attention", StringComparison.Ordinal)));
        Assert.True(rig.Sink.Snapshots > 1, "the loop did idle-park and push snapshots, so it really did keep turning");
    }

    // ───────────────────────────────── a dry run notifies nobody

    /// <summary>Every leg, every path. The notifier the composition root builds under <c>--dry-run</c>
    /// is the one under test, because <c>TelegramService</c> IS constructed and started under a dry
    /// run — assuming the service is absent is exactly the assumption that let the flood out.</summary>
    [Fact]
    public async Task ADryRunNotifiesNobodyOnAnyLeg()
    {
        var rig = Rig(dryRun: true, handoff: $"last: stuck. {Escalation} pick the auth provider.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var exit = await rig.Loop.RunAsync(cts.Token);

        Assert.Equal(0, exit);
        Assert.Empty(rig.Telegram.Sent);        // run start + needs human: both silent
        Assert.Empty(rig.Telegram.Keyboards);
        Assert.Equal(0, rig.NotifyCommandRuns());
    }

    /// <summary>The other five push paths, driven directly at the muted service the dry-run context
    /// hands every collaborator: session end, run complete, evidence, blocked-until and the owner
    /// queue. None of them goes through <c>Notify</c>, so none of them would be covered by a guard
    /// there — which is why the guard is a decorator on the service instead.</summary>
    [Fact]
    public void ADryRunsTelegramServiceDropsEveryOtherPushPathToo()
    {
        var rig = Rig(dryRun: true, handoff: "last: nothing.");
        var telegram = rig.Ctx.Telegram;

        Assert.NotSame(rig.Telegram, telegram);   // the context wrapped it
        _ = telegram.PushAsync("blocked until 09:00Z");
        _ = telegram.PushSessionEndAsync(new SessionEndPush(1, "S1", "Advanced", "", null, 0m, null, 0, [], false, [], null));
        _ = telegram.PushRunCompleteAsync(new RunCompletePush(1, 1, 1, null, []));
        _ = telegram.PushEvidenceAsync([]);
        _ = telegram.PushWithKeyboardAsync("approve?", [("Approve", "approve")]);
        rig.Ctx.NotifyNewOwnerQueueItems(
            [new OwnerQueueItem("q1", "human", "pick a provider", "S1", "conductor resume", null, 0)]);

        Assert.Empty(rig.Telegram.Sent);
        Assert.Empty(rig.Telegram.Keyboards);
        Assert.Empty(rig.Telegram.Other);
    }

    /// <summary>…and the run-start readiness line stops claiming delivery on a run that sends
    /// nothing. It still defers to the REAL blocker when there is one (SF0.1's sentence).</summary>
    [Fact]
    public void ADryRunSaysOutLoudThatItWillNotifyNobody()
    {
        var rig = Rig(dryRun: true, handoff: "last: nothing.");
        Assert.Equal(ParkNotifier.DryRunSilence, rig.Ctx.Telegram.DeliveryBlocker);

        var real = new ParkNotifier(dryRun: true);
        Assert.True(real.DryRun);
    }

    [Fact]
    public async Task ADryRunPostsToNoWebhook()
    {
        using var endpoint = new CountingEndpoint();
        var plan = new PlanConfig
        {
            Name = "ks26", Repo = _tmp, Tracker = "TRACKER.md",
            Notify = new NotifyConfig { Webhook = new WebhookNotifyConfig { Url = endpoint.Url } },
        };

        var muted = new WebhookNotifier(plan, NullLogger<WebhookNotifier>.Instance, dryRun: true);
        _open.Add(muted);
        muted.FireAsync("this must not leave the process");
        await Task.Delay(400);
        Assert.Equal(0, endpoint.Hits);

        // The endpoint is real — a live notifier does reach it, so the zero above is the guard and
        // not a broken test rig.
        var live = new WebhookNotifier(plan, NullLogger<WebhookNotifier>.Instance);
        _open.Add(live);
        live.FireAsync("this one is a real run");
        await endpoint.WaitForAsync(1, TimeSpan.FromSeconds(10));
        Assert.Equal(1, endpoint.Hits);
    }

    // ───────────────────────────────── the dry-run hot loop is closed

    /// <summary>The spin itself. A dry run is a preview and never waits: walking into a park it says
    /// what it found and stops, so a parked state cannot turn the loop at full speed. Bounded by the
    /// clock AND by the work done — the pre-KS2.6 loop would have re-read the tracker, re-parked and
    /// re-notified thousands of times inside this window.</summary>
    [Theory]
    [InlineData(RunStatus.NeedsHuman)]
    [InlineData(RunStatus.Paused)]
    [InlineData(RunStatus.AwaitingOwner)]
    public async Task ADryRunThatWalksIntoAParkStopsInsteadOfSpinning(RunStatus parked)
    {
        var rig = Rig(dryRun: true, handoff: "last: nothing.", notifier: new ParkNotifier(dryRun: false));
        rig.Ctx.State.Status = parked;
        rig.Ctx.State.SetAttention("parked on purpose, by this test");

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var exit = await rig.Loop.RunAsync(cts.Token);
        sw.Stop();

        Assert.Equal(0, exit);
        Assert.False(cts.IsCancellationRequested, "the loop returned on its own, not because the test gave up");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Contains(rig.Sink.Lines, l => l.Contains("DRY RUN: this run is parked at", StringComparison.Ordinal));
        Assert.Contains(rig.Sink.Lines, l => l.Contains("parked on purpose, by this test", StringComparison.Ordinal));
        // Only the run-start push; the park was already set, so nothing new was raised.
        Assert.Equal(0, rig.Telegram.Count(m => m.Contains("needs attention", StringComparison.Ordinal)));
    }

    // ───────────────────────────────── a backoff park says it is parked

    /// <summary>A DNS/preflight park used to only LOG. The observed cost was a fourteen-hour silent
    /// park from a transient network cut, because the backoff doubles to an hour and every re-check
    /// after the first wrote a line nobody was watching. It now pushes per ESCALATION: the incident
    /// key carries the consecutive-failure count, so each longer park is news and each repeat inside
    /// one is suppressed.</summary>
    [Fact]
    public async Task APreflightBackoffParkSaysItIsParkedAndForHowLong()
    {
        // The git check against a directory that is not a repository: a preflight failure that is
        // deterministic and fast, with no network in it.
        var rig = Rig(dryRun: false, handoff: "last: nothing.", limits: l => l.DnsHealthCheck = new DnsHealthCheckConfig
        {
            Enabled = true,
            Hosts = [],
            MinFreeDiskMb = 0,
            EnableGitCheck = true,
            IntervalSeconds = 1,
            BackoffMultiplier = 2.0,
            MaxBackoffSeconds = 2,
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await rig.Loop.RunAsync(cts.Token);

        var parkPushes = rig.Telegram.Sent
            .Where(m => m.Contains("PARKED, backing off", StringComparison.Ordinal)).ToList();
        Assert.True(parkPushes.Count >= 2,
            "the park pushed on the first failure and again on the escalation: " + string.Join(" || ", rig.Telegram.Sent));

        // One push per escalation, never two for the same count.
        var escalations = parkPushes
            .Select(m => m[(m.IndexOf("(×", StringComparison.Ordinal) + 1)..m.IndexOf(')', StringComparison.Ordinal)])
            .ToList();
        Assert.Equal(escalations.Distinct(StringComparer.Ordinal).Count(), escalations.Count);
        Assert.Contains("×1", escalations, StringComparer.Ordinal);
        Assert.All(parkPushes, m => Assert.True(
            m.Contains("seconds", StringComparison.Ordinal) || m.Contains("minutes", StringComparison.Ordinal),
            "a park push says for how long: " + m));
        Assert.NotEmpty(rig.Telegram.Keyboards);
    }

    // ───────────────────────────────── rig

    private sealed record LoopRig(
        RunContext Ctx, RunLoop Loop, CountingTelegram Telegram, CapturingSink Sink, string NotifyLog)
    {
        public int NotifyCommandRuns()
            => File.Exists(NotifyLog) ? File.ReadAllLines(NotifyLog).Count(l => l.Trim().Length > 0) : 0;
    }

    /// <summary>A real <see cref="RunLoop"/> over a real <see cref="VerdictEngine"/>, with the three
    /// notification legs counted: a fake Telegram service, and a notify COMMAND that appends a line
    /// to a file every time it is invoked. No store and no session runner — every path under test
    /// parks before a session could spawn, and passing them null makes that an assertion rather than
    /// a claim.</summary>
    private LoopRig Rig(bool dryRun, string handoff, Action<LimitsConfig>? limits = null,
                        ParkNotifier? notifier = null)
    {
        var repo = Path.Combine(_tmp, "repo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"),
            "# Plan\n\n## Handoff\n" + handoff + "\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| S1.1 | one | TODO | | |\n", new UTF8Encoding(false));

        // The notify COMMAND leg, counted by making the command itself write a line. Argument-wise
        // this is deliberately boring — no absolute path, no quoting, cwd is the repo — because a
        // shell-quoting accident here would read exactly like the guard under test working.
        var notifyLog = Path.Combine(repo, "notify-command.log");
        var plan = new PlanConfig
        {
            Name = "ks26",
            Repo = repo.Replace('\\', '/'),
            Tracker = "TRACKER.md",
            Stages = [new StageConfig { Id = "S1", Title = "one", Sessions = 1 }],
            Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", "echo", "{prompt}"], Provider = "opencode" },
            Notify = new NotifyConfig
            {
                Command = "cmd.exe",
                Args = ["/c", "echo", "notified", ">>", "notify-command.log"],
            },
        };
        limits?.Invoke(plan.Limits);

        var state = new RunState { RunId = "run-ks26-" + Guid.NewGuid().ToString("N")[..6], PlanName = plan.Name };
        var sink = new CapturingSink();
        var telegram = new CountingTelegram();
        var lessons = new LessonsManager(plan.StateDir);
        var qa = new DefaultQaPolicy();
        var webhooks = new WebhookNotifier(plan, NullLogger<WebhookNotifier>.Instance, dryRun: dryRun);
        _open.Add(webhooks);

        var ctx = new RunContext(
            plan, state, new RunOptions(DryRun: dryRun, Once: false, MaxSessions: 0),
            sink, NullEventSink.Instance, new PromptBuilder(plan, new PersonaRegistry(plan), lessons, qa),
            lessons, new CheckpointPlanner(), ProgressProviderFactory.Create(plan),
            AgentProviderFactory.Create(plan.Agent), store: null,
            processSupervisor: null, controlInbox: null,
            telegram, webhooks,
            workflowResolver: null, NullLogger<KS2_6ParkHygieneTests>.Instance,
            assignmentPolicy: null, qaPolicy: qa, notifier: notifier);

        var gates = new GateOrchestrator(plan, state, NullEventSink.Instance, store: null);
        var lanes = new LaneCoordinator(plan, state, sink, NullEventSink.Instance, _ => { });
        var verdicts = new VerdictEngine(ctx, gates, lanes, ctx.Telegram, webhooks,
            saveAndReport: () => { }, pushIdleSnapshot: () => { });
        var dispatcher = new ControlDispatcher(plan, state, sink, NullEventSink.Instance, log: _ => { },
            save: () => { }, deleteControlFile: () => { }, skipStage: (_, _) => { },
            approveAwaitingOwner: (_, _) => Task.CompletedTask);
        var loop = new RunLoop(ctx, sessions: null!, verdicts, gates, lanes, dispatcher, saveAndReport: () => { });

        return new LoopRig(ctx, loop, telegram, sink, notifyLog);
    }

    // ───────────────────────────────── stubs

    private sealed class CountingTelegram : ITelegramService
    {
        private readonly Lock _gate = new();
        public List<string> Sent { get; } = new();
        public List<string> Keyboards { get; } = new();
        public List<string> Other { get; } = new();

        public string? DeliveryBlocker => null;

        public int Count(Func<string, bool> predicate)
        {
            lock (_gate) return Sent.Count(predicate);
        }

        public Task PushAsync(string message, PushSeverity severity = PushSeverity.Quiet, CancellationToken ct = default)
        {
            lock (_gate) Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task PushWithKeyboardAsync(string message,
            IReadOnlyList<(string Text, string CallbackData)> buttons, CancellationToken ct = default)
        {
            lock (_gate) Keyboards.Add(message);
            return Task.CompletedTask;
        }

        public Task PushSessionEndAsync(SessionEndPush push, CancellationToken ct = default)
        {
            lock (_gate) Other.Add("session-end");
            return Task.CompletedTask;
        }

        public Task PushRunCompleteAsync(RunCompletePush push, CancellationToken ct = default)
        {
            lock (_gate) Other.Add("run-complete");
            return Task.CompletedTask;
        }

        public Task PushEvidenceAsync(IReadOnlyList<Core.Evidence.EvidenceArtifact> artifacts, CancellationToken ct = default)
        {
            lock (_gate) Other.Add("evidence");
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingSink : IProgressSink
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = new();
        private int _snapshots;

        public IReadOnlyList<string> Lines { get { lock (_gate) return _lines.ToList(); } }
        public int Snapshots => Volatile.Read(ref _snapshots);

        public void Log(string line) { lock (_gate) _lines.Add(line); }
        public void AgentEvent(AgentEvent ev) { }
        public void Snapshot(DashboardSnapshot snap) => Interlocked.Increment(ref _snapshots);
        public ControlCommand? PollControl() => null;
    }

    /// <summary>A loopback endpoint that counts POSTs. Proves the muted webhook sent nothing by
    /// proving a live one sends something to the same address.</summary>
    private sealed class CountingEndpoint : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _hits;

        public string Url { get; }
        public int Hits => Volatile.Read(ref _hits);

        public CountingEndpoint()
        {
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            Url = FormattableString.Invariant($"http://127.0.0.1:{port}/hook");
            _listener.Prefixes.Add(FormattableString.Invariant($"http://127.0.0.1:{port}/"));
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                Interlocked.Increment(ref _hits);
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
        }

        public async Task WaitForAsync(int hits, TimeSpan within)
        {
            var deadline = DateTime.UtcNow + within;
            while (Hits < hits && DateTime.UtcNow < deadline) await Task.Delay(50);
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
            try { _listener.Close(); } catch (ObjectDisposedException) { }
        }
    }
}
