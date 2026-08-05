using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Http;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Conductor.Tests;

/// <summary>
/// K5.3 — <c>GET /evidence</c> at the curl level, against a real HttpListener on loopback: the same
/// bar the rest of the control plane is held to, because a golden frame in the Face cannot catch a
/// wire mismatch and a DTO asserted from source reading is not a contract.
///
/// <para>The load-bearing property is that the endpoint serves a FOLD of the event log. A run whose
/// evidence file was deleted after registration still reports it, and the reply can say which
/// session produced what — neither of which a scan-at-read-time endpoint could do.</para>
/// </summary>
public sealed class ControlPlaneServerEvidenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-cpsev-{Guid.NewGuid():N}");
    private readonly SqliteRunStore _store;
    private readonly PlanConfig _plan;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();

    private const string RunId = "run-cpsev";

    public ControlPlaneServerEvidenceTests()
    {
        Directory.CreateDirectory(_dir);
        var stateDir = Path.Combine(_dir, ".conductor");
        Directory.CreateDirectory(stateDir);
        _store = new SqliteRunStore(Path.Combine(stateDir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId(RunId);
        _plan = new PlanConfig
        {
            Name = "cpsev-test",
            Repo = _dir,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "K5", Title = "Evidence", Sessions = 1 } },
        };
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), "# T\n\n## Handoff\nlast: none.\n");
    }

    public void Dispose()
    {
        _http.Dispose();
        _store.Dispose();
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { /* best effort */ }
    }

    private void WriteEvents(params ConductorEvent[] events)
    {
        var target = _store.ReadAllEvents(RunId).Count + events.Length;
        foreach (var e in events) _store.Emit(e);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_store.ReadAllEvents(RunId).Count < target && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
    }

    private static EvidenceRegistered Registered(string path, string kind, string? checkpoint,
        int? session, string source, string sha) => new()
        {
            Path = path, Kind = kind, CheckpointId = checkpoint, StageId = "K5",
            SessionNumber = session, Source = source, Sha256 = sha, Bytes = 4096,
            Ts = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero),
        };

    private (ControlPlaneServer server, int port) StartServer()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var server = new ControlPlaneServer(_plan, new RunState { RunId = RunId }, _store, _inbox,
            new NoOpTelegramService(), NullLogger.Instance, port);
        Assert.True(server.Start(), "control plane failed to bind — cannot run contract tests");
        _http.DefaultRequestHeaders.Remove("X-Conductor-Token");
        _http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);
        return (server, server.Port);
    }

    private async Task<JsonElement> GetAsync(int port, string query)
    {
        var resp = await _http.GetAsync($"http://127.0.0.1:{port}/evidence{query}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task GetEvidence_ServesTheFoldNewestFirst_WithTheScreenshotMarkedVisual()
    {
        WriteEvents(
            new RunStarted { Plan = "cpsev-test", Repo = _dir },
            Registered(".conductor/evidence/K5/K5.3-notes.md", EvidenceKinds.Text, "K5.3", 18, "claim", "aaa"),
            Registered(".conductor/evidence/K5/K5.3-shot.png", EvidenceKinds.Image, "K5.3", 19, "watcher", "bbb"));

        var (server, port) = StartServer();
        try
        {
            var root = await GetAsync(port, "");
            Assert.Equal(2, root.GetProperty("count").GetInt32());
            var artifacts = root.GetProperty("artifacts");
            Assert.Equal(2, artifacts.GetArrayLength());

            // Newest first — what a feed shows.
            var first = artifacts[0];
            Assert.Equal(".conductor/evidence/K5/K5.3-shot.png", first.GetProperty("path").GetString());
            Assert.Equal("image", first.GetProperty("kind").GetString());
            Assert.True(first.GetProperty("visual").GetBoolean());
            Assert.Equal("watcher", first.GetProperty("source").GetString());
            Assert.Equal(19, first.GetProperty("sessionNumber").GetInt32());
            Assert.Equal("K5.3", first.GetProperty("checkpointId").GetString());
            Assert.Equal("K5", first.GetProperty("stageId").GetString());
            Assert.Equal(4096, first.GetProperty("bytes").GetInt64());
            Assert.Equal("bbb", first.GetProperty("sha256").GetString());
            // Round-trippable ISO-8601, and the value is the log's own stamp: EventLog owns Ts (the
            // envelope), so "when the engine first saw it" cannot be back-dated by a caller.
            Assert.True(DateTimeOffset.TryParse(first.GetProperty("createdAt").GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var createdAt));
            Assert.Equal(TimeSpan.Zero, createdAt.Offset);

            Assert.False(artifacts[1].GetProperty("visual").GetBoolean());
            Assert.Equal("claim", artifacts[1].GetProperty("source").GetString());
        }
        finally { server.Dispose(); }
    }

    /// <summary>The registry is a fold, so the answer does not depend on what is still on disk. This
    /// is the difference between an evidence endpoint and a directory listing: nothing was ever
    /// written to this repo's evidence directory, and the run still reports two artifacts.</summary>
    [Fact]
    public async Task GetEvidence_AnswersFromTheLog_NotFromTheDisk()
    {
        WriteEvents(
            Registered("docs/evidence/K5/deleted-since.png", EvidenceKinds.Image, "K5.3", 19, "watcher", "ccc"),
            Registered("docs/evidence/K5/also-gone.md", EvidenceKinds.Text, null, null, "claim", "ddd"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "docs", "evidence")));

        var (server, port) = StartServer();
        try
        {
            var root = await GetAsync(port, "");
            Assert.Equal(2, root.GetProperty("count").GetInt32());
            // A null checkpoint / session is omitted rather than rendered as a lie. Newest first, so
            // the unattributed one — registered second — is at the head.
            var unattributed = root.GetProperty("artifacts")[0];
            Assert.Equal("docs/evidence/K5/also-gone.md", unattributed.GetProperty("path").GetString());
            Assert.False(unattributed.TryGetProperty("checkpointId", out _));
            Assert.False(unattributed.TryGetProperty("sessionNumber", out _));
            Assert.Equal("K5.3", root.GetProperty("artifacts")[1].GetProperty("checkpointId").GetString());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetEvidence_FiltersByCheckpointAndPages_AndSaysHowManyThereReallyAre()
    {
        WriteEvents(
            Registered("e/a.md", EvidenceKinds.Text, "K5.1", 17, "claim", "1"),
            Registered("e/b.png", EvidenceKinds.Image, "K5.3", 19, "watcher", "2"),
            Registered("e/c.md", EvidenceKinds.Text, "k5.3", 19, "claim", "3"));

        var (server, port) = StartServer();
        try
        {
            var filtered = await GetAsync(port, "?checkpoint=K5.3");
            Assert.Equal(["e/c.md", "e/b.png"],
                filtered.GetProperty("artifacts").EnumerateArray().Select(a => a.GetProperty("path").GetString()));
            // Count stays the size of the whole registry, so a surface showing 2 of 3 can say so.
            Assert.Equal(3, filtered.GetProperty("count").GetInt32());

            var paged = await GetAsync(port, "?limit=1");
            Assert.Equal(1, paged.GetProperty("artifacts").GetArrayLength());
            Assert.Equal(3, paged.GetProperty("count").GetInt32());

            // Junk in the query string is ignored, not a 500.
            var junk = await GetAsync(port, "?limit=abc&checkpoint=");
            Assert.Equal(3, junk.GetProperty("artifacts").GetArrayLength());
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task GetEvidence_IsAnEmptyListOnARunThatProducedNone()
    {
        WriteEvents(new RunStarted { Plan = "cpsev-test", Repo = _dir });
        var (server, port) = StartServer();
        try
        {
            var root = await GetAsync(port, "");
            Assert.Equal(0, root.GetProperty("count").GetInt32());
            Assert.Empty(root.GetProperty("artifacts").EnumerateArray());
        }
        finally { server.Dispose(); }
    }
}
