using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Http;
using Conductor.Models;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS2.2 — <c>GET /timeline</c>, pinned on BOTH planes.
///
/// <para>The fold moved into <see cref="TimelineProjection"/> so live and archive answer from one set of
/// rules. Moving it also CHANGED the live payload, and this file is the assertion that says so out loud.
/// The switch it came from opened with <c>kind = "unknown", desc = ""</c> and answered a
/// <see cref="TokenDelta"/> with a bare <c>break</c> — which in C# leaves the switch and falls into the
/// <c>entries.Add</c> underneath, so every deduplicated API call shipped a row with kind <c>unknown</c>
/// and nothing written on it. Measured on a live engine before the change: 2262 entries, 2147 of them
/// blank. The Face's spine renders entries with no kind or description filter, so a reader was scrolling
/// through 95% nothing.</para>
///
/// <para>They are gone now — deliberately, on both planes — and nothing in the tree asserted either
/// behaviour before this file, which is how it got as far as a verifier. The tests below seed real token
/// deltas, serve them over a real socket from a real store on the live plane and from a real
/// <c>run.db</c> on the archive plane, and assert the same three things of both: a token delta is no
/// row, no row is ever kind <c>unknown</c> or blank, and the two planes answer the same bytes for the
/// same log.</para>
/// </summary>
public sealed class KS2_2TimelineProjectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "conductor-ks22tl-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly HttpClient _http = new();
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();

    private const string RunId = "run-ks22-timeline";

    public KS2_2TimelineProjectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _http.Dispose();
        SqliteConnection.ClearAllPools();
        try { TestTemp.DeleteTree(_dir); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── fixture ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A log with the shape a real session has: a handful of events worth a row and a crowd of
    /// token deltas around them, which is the ratio that made this matter (one delta per API call).</summary>
    private string SeedLog()
    {
        var db = Path.Combine(_dir, ".conductor", "run.db");
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun(RunId, "tl-plan", _dir, "master", EngineStamp.Parse("0.9.0+abc123"));
            store.SetRunId(RunId);
            store.InitializeStage(RunId, "S1", "Stage One");

            var events = new List<ConductorEvent>
            {
                new StageEntered { StageId = "S1", Title = "Stage One" },
                new SessionStarted { Number = 1, StageId = "S1", Kind = "Deliver" },
            };
            for (var i = 0; i < TokenDeltaCount; i++)
                events.Add(new TokenDelta { SessionId = "1", Input = 100 + i, Output = 20, CacheRead = 5, CostUsd = 0.01m });
            events.Add(new GateFinished { Name = "fast", Passed = true, DurationMs = 1200, Scope = "S1" });
            events.Add(new SessionFinished { Number = 1, StageId = "S1", Outcome = "Advanced", CostUsd = 0.33m });
            events.Add(new AttentionRequested { Reason = "needs a human" });
            events.Add(new StageConfirmed { StageId = "S1" });

            foreach (var e in events) store.Emit(e);
            // Emit drains asynchronously and both planes read the table synchronously — wait for the
            // log to land rather than racing it (the same wait ControlPlaneServerTests does).
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (store.ReadAllEvents(RunId).Count < events.Count && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            Assert.Equal(events.Count, store.ReadAllEvents(RunId).Count);
            store.RecordRunEnd(RunId, "completed");
        }
        SqliteConnection.ClearAllPools();
        return db;
    }

    /// <summary>Enough deltas that a fold which kept them could not pass for one that drops them.</summary>
    private const int TokenDeltaCount = 25;

    /// <summary>The five events above that a timeline row is owed: stage entered, session started, gate,
    /// session finished, attention, stage confirmed.</summary>
    private const int ExpectedRows = 6;

    private static int FreeLoopbackPort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private async Task<TimelineDto> TimelineFromAsync(string baseUrl)
    {
        var body = await _http.GetStringAsync(new Uri(baseUrl + "/timeline"));
        return JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.TimelineDto)!;
    }

    private static void AssertNoBlankRows(TimelineDto timeline)
    {
        Assert.DoesNotContain(timeline.Entries, e => string.Equals(e.Kind, "unknown", StringComparison.Ordinal));
        Assert.DoesNotContain(timeline.Entries, e => string.IsNullOrWhiteSpace(e.Kind));
        Assert.DoesNotContain(timeline.Entries, e => string.IsNullOrWhiteSpace(e.Description));
    }

    // ── the live plane ───────────────────────────────────────────────────────────────────────────

    /// <summary>The plane the engine serves while a run is going. This is the payload KS2.2 changed.</summary>
    [Fact]
    public async Task A_token_delta_is_no_row_on_the_live_plane()
    {
        var db = SeedLog();
        using var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance);
        store.SetRunId(RunId);
        var plan = new PlanConfig
        {
            Name = "tl-plan",
            Repo = _dir,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "S1", Title = "Stage One", Sessions = 1 } },
        };

        using var server = new ControlPlaneServer(plan, new RunState { RunId = RunId }, store, _inbox,
            new NoOpTelegramService(), NullLogger.Instance, FreeLoopbackPort());
        Assert.True(server.Start(), "the live control plane failed to bind");

        var timeline = await TimelineFromAsync($"http://127.0.0.1:{server.Port}");

        // 25 token deltas went in; not one of them is a row, and the rows that ARE there are the six
        // events a spine is made of.
        Assert.Equal(ExpectedRows, timeline.Entries.Count);
        AssertNoBlankRows(timeline);
        Assert.Contains(timeline.Entries, e => e.Kind == "session" && e.SessionNumber == 1);
        Assert.Contains(timeline.Entries, e => e.Kind == "gate");
        Assert.Contains(timeline.Entries, e => e.Kind == "attention");
    }

    // ── the archive plane ────────────────────────────────────────────────────────────────────────

    /// <summary>The plane KS2.2 added. Same rules, or the extraction bought nothing.</summary>
    [Fact]
    public async Task A_token_delta_is_no_row_on_the_archive_plane()
    {
        var db = SeedLog();
        var view = ArchiveView.OpenDb(db, RunId, out var refusal);
        Assert.NotNull(view);
        Assert.Equal("", refusal);

        using var plane = new ArchiveControlPlane(view!, NullLogger.Instance, FreeLoopbackPort());
        Assert.True(plane.Start(), "the archive plane failed to bind");

        var timeline = await TimelineFromAsync(plane.BaseUrl);

        Assert.Equal(ExpectedRows, timeline.Entries.Count);
        AssertNoBlankRows(timeline);
    }

    /// <summary>Why the fold was lifted into core at all: one log, two planes, one answer. A future
    /// event type added to only one of them would land here.</summary>
    [Fact]
    public async Task Both_planes_answer_the_same_timeline_for_the_same_log()
    {
        var db = SeedLog();

        string live;
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.SetRunId(RunId);
            var plan = new PlanConfig { Name = "tl-plan", Repo = _dir, Tracker = "TRACKER.md" };
            using var server = new ControlPlaneServer(plan, new RunState { RunId = RunId }, store, _inbox,
                new NoOpTelegramService(), NullLogger.Instance, FreeLoopbackPort());
            Assert.True(server.Start());
            live = await _http.GetStringAsync(new Uri($"http://127.0.0.1:{server.Port}/timeline"));
        }
        SqliteConnection.ClearAllPools();

        var view = ArchiveView.OpenDb(db, RunId, out _)!;
        using var archivePlane = new ArchiveControlPlane(view, NullLogger.Instance, FreeLoopbackPort());
        Assert.True(archivePlane.Start());
        var archived = await _http.GetStringAsync(new Uri(archivePlane.BaseUrl + "/timeline"));

        Assert.Equal(live, archived, StringComparer.Ordinal);
    }

    // ── the rule, not just this log ───────────────────────────────────────────────────────────────

    /// <summary>The invariant behind both planes, asserted on the fold itself so a new event type cannot
    /// re-open the hole by being added to the switch without a kind or a description. Every event kind
    /// the engine can emit goes in; whatever comes out must say something.</summary>
    [Fact]
    public void No_row_this_fold_emits_is_ever_unknown_or_blank()
    {
        ConductorEvent[] everything =
        [
            new RunStarted { Plan = "p", Repo = "r" },
            new StageEntered { StageId = "S1", Title = "Stage One" },
            new StageConfirmed { StageId = "S1" },
            new SessionStarted { Number = 1, StageId = "S1", Kind = "Deliver" },
            new SessionFinished { Number = 1, StageId = "S1", Outcome = "Advanced", CostUsd = 0.5m },
            new GateFinished { Name = "fast", Passed = false, DurationMs = 9, Scope = "S1" },
            new AttentionRequested { Reason = "needs a human" },
            new PlanReloaded { PlanVersion = 2, Stages = 3, Gates = 4 },
            new TokenDelta { SessionId = "1", Input = 10, Output = 2, CostUsd = 0.01m },
            new McpCallFinished { ToolName = "grep", DurationMs = 3, Success = true },
        ];

        var timeline = TimelineProjection.From(everything);

        AssertNoBlankRows(timeline);
        Assert.DoesNotContain(timeline.Entries, e => e.Description.Contains("TokenDelta", StringComparison.Ordinal));
        // Seven of the ten are worth a row: RunStarted, TokenDelta and McpCallFinished are not.
        Assert.Equal(7, timeline.Entries.Count);
    }
}
