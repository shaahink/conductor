using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SF3.3 — git awareness as <c>GET /state</c> and <c>GET /sessions</c> actually serve them, over a
/// real socket, against a real git repo.
/// <para>Asserting the projection alone is not enough here and the endpoint's own doc comment says
/// why: <c>WriteStateAsync</c> folds the event log and then re-stamps a hand-maintained list of live
/// fields. A block added to <c>StateDto</c> but never stamped compiles, unit-tests green, and
/// arrives on the wire as nothing — which is the exact shape of every "the Face is showing
/// something stale" defect this era was convened to end.</para>
/// </summary>
public sealed class SF3_3GitWireTests : IDisposable
{
    private const string RunId = "run-sf33-wire";
    private readonly string _dir;
    private readonly SqliteRunStore _store;
    private readonly PlanConfig _plan;
    private readonly System.Collections.Concurrent.ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();

    public SF3_3GitWireTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "conductor-sf33w-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_dir, ".conductor"));
        _store = new SqliteRunStore(Path.Combine(_dir, ".conductor", "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId(RunId);
        _store.InitializeRun(RunId, "sf33-wire", _dir, null, null);
        _plan = new PlanConfig
        {
            Name = "sf33-wire",
            Repo = _dir,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "S1", Title = "Stage One", Sessions = 1 } },
        };
        InitRepo(_dir);
        GitSnapshotCache.Clear();
    }

    public void Dispose()
    {
        GitSnapshotCache.Clear();
        _http.Dispose();
        _store.Dispose();
        try { DeleteTree(_dir); } catch (IOException) { /* git pack files; the temp dir is disposable */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>The whole block, end to end: branch, dirtiness, HEAD, subjects — and the build
    /// identity FU-OWNER-10 asked for, in the same payload.</summary>
    [Fact]
    public async Task GetState_ServesTheGitBlockAndTheBuildIdentity()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "dirty.txt"), "uncommitted work");
        _store.Emit(new RunStarted { Plan = _plan.Name, Repo = _dir });
        _store.Emit(new StageEntered { StageId = "S1", Title = "Stage One" });
        _store.FlushEvents();

        using var doc = await GetJson("/state");
        var root = doc.RootElement;

        var git = root.GetProperty("git");
        Assert.True(git.GetProperty("isRepo").GetBoolean());
        Assert.Equal("sf33-main", git.GetProperty("branch").GetString());
        Assert.False(git.GetProperty("detached").GetBoolean());
        Assert.True(git.GetProperty("dirty").GetBoolean());
        // Two, not one: `.conductor/` is untracked in this rig exactly as it is in a real workspace
        // that has not gitignored it yet, and porcelain reports it as its own row. The count is what
        // the status strip renders, so it is asserted as git actually reports it.
        Assert.Equal(2, git.GetProperty("dirtyCount").GetInt32());
        Assert.Contains("dirty.txt", git.GetProperty("dirtySummary").GetString()!, StringComparison.Ordinal);
        Assert.Equal(40, git.GetProperty("headSha").GetString()!.Length);
        Assert.Equal(7, git.GetProperty("headShortSha").GetString()!.Length);
        Assert.Equal("second commit", git.GetProperty("headSubject").GetString());
        var subjects = git.GetProperty("recentCommits").EnumerateArray()
            .Select(c => c.GetProperty("subject").GetString()).ToList();
        Assert.Equal(["second commit", "init"], subjects);

        // FU-OWNER-10. The engine's version and commit are its OWN stamp, not a re-derivation: the
        // point of the field is that "did my reinstall take?" stops needing Get-CimInstance.
        Assert.Equal(BuildInfo.Current.Version, root.GetProperty("engineVersion").GetString());
        Assert.StartsWith(BuildInfo.Current.CommitSha, root.GetProperty("engineCommit").GetString()!,
            StringComparison.Ordinal);
        // faceBuild is present as a field even when no Face is built on this machine — an absent
        // field and "no Face here" would otherwise read the same to a Face parsing this payload.
        Assert.True(root.TryGetProperty("faceBuild", out _) || root.GetProperty("faceBuild").ValueKind == JsonValueKind.String);
    }

    /// <summary>Null, not zero. A branch that was never pushed must not serve the same
    /// ahead/behind as a branch that is level with its upstream — the temp repo here has no remote,
    /// so the wire must simply omit both counters.</summary>
    [Fact]
    public async Task GetState_OmitsAheadBehind_WhenThereIsNoUpstream()
    {
        _store.Emit(new RunStarted { Plan = _plan.Name, Repo = _dir });
        _store.FlushEvents();

        using var doc = await GetJson("/state");
        var git = doc.RootElement.GetProperty("git");

        Assert.False(git.TryGetProperty("ahead", out _));
        Assert.False(git.TryGetProperty("behind", out _));
        Assert.False(git.TryGetProperty("upstream", out _));
    }

    /// <summary>Session history carries the SUBJECTS. The sessions table only ever persisted a
    /// count, so this is the assertion that the endpoint reaches into the event log for them rather
    /// than serving the count twice.</summary>
    [Fact]
    public async Task GetSessions_CarriesCommitSubjects_NotJustACount()
    {
        _store.Emit(new RunStarted { Plan = _plan.Name, Repo = _dir });
        _store.Emit(new StageEntered { StageId = "S1", Title = "Stage One" });
        _store.Emit(new SessionStarted { Number = 1, StageId = "S1", Kind = "Deliver" });
        _store.Emit(new SessionFinished
        {
            Number = 1,
            StageId = "S1",
            Outcome = "Delivered",
            NewCommits = ["3dd1b2b docs(tracker): SF3.2 claimed complete", "88a966a test(face): rebaseline"],
        });
        _store.FlushEvents();
        _store.RecordSession(RunId, "S1", 1, "Deliver", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow,
            "Delivered", agentSessionId: null, resumeCount: 0, attempt: 1,
            gateSummary: "gates ok", resultSummary: "did the thing", commitCount: 2, newlyDone: null);

        using var doc = await GetJson("/sessions");
        var row = doc.RootElement.GetProperty("sessions").EnumerateArray().Single();

        Assert.Equal(2, row.GetProperty("commitCount").GetInt32());
        var commits = row.GetProperty("commits").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Equal(2, commits.Count);
        Assert.Contains("docs(tracker): SF3.2 claimed complete", commits[0], StringComparison.Ordinal);
        Assert.Contains("test(face): rebaseline", commits[1], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<JsonDocument> GetJson(string path)
    {
        var server = new ControlPlaneServer(_plan, new RunState { RunId = RunId, CurrentStage = "S1" },
            _store, _inbox, new NoOpTelegramService(), NullLogger.Instance, FreeLoopbackPort());
        Assert.True(server.Start(), "control plane failed to bind");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}{path}");
            req.Headers.Add("X-Conductor-Token", server.Token);
            using var resp = await _http.SendAsync(req);
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        }
        finally { server.Dispose(); }
    }

    private static int FreeLoopbackPort()
    {
        using var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private static void InitRepo(string dir)
    {
        Git(dir, "init", "-b", "sf33-main");
        Git(dir, "config", "user.email", "sf33@test");
        Git(dir, "config", "user.name", "SF33 Test");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# t");
        Git(dir, "add", "README.md");
        Git(dir, "commit", "-m", "init", "--no-gpg-sign");
        File.WriteAllText(Path.Combine(dir, "second.md"), "# two");
        Git(dir, "add", "second.md");
        Git(dir, "commit", "-m", "second commit", "--no-gpg-sign");
    }

    private static void Git(string dir, params string[] args)
    {
        var r = ProcessRunner.Run("git", args, dir, TimeSpan.FromSeconds(60));
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)} failed ({r.ExitCode}): {r.Output}");
    }

    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }
}
