using System.Text.Json;

using Conductor.Commands;
using Conductor.Core.Fleet;
using Conductor.Core.Http;

namespace Conductor.Tests;

/// <summary>
/// SF5.4 — <c>conductor ps</c>: the fleet, seen from outside.
///
/// <para>What is worth being sure of here is not "an HTTP GET happens". It is the four judgements that
/// decide whether a fleet listing can be trusted at a glance:</para>
/// <list type="number">
/// <item>A STRANGER ON THE PORT IS NOT A RUN — the window 4317-4336 is not reserved, and this very
/// machine has two non-conductor listeners inside it. Anything that answers with JSON of another shape
/// deserializes into an all-default record, so "did it parse" is not the test; identity is.</item>
/// <item>THE PID IS THE ENGINE'S, OR IT IS ABSENT — never a stale one. A discovery file naming a
/// DIFFERENT port was written by a plane that is gone; lending its pid to the live row would point a
/// worried operator at the wrong process, which is how the wrong process gets killed.</item>
/// <item>A MISSING DISCOVERY FILE IS NORMAL — measured on the run that drove this session: live engine,
/// serving 4317, no <c>control-plane.json</c>. The lock file is the fallback and the row still lists.</item>
/// <item>ONE RUN IS ONE ROW — the local plan and the wire name the same state dir in different spellings
/// (separator, case), and a fleet listing that shows the same engine twice is worse than one that shows
/// it once.</item>
/// </list>
/// </summary>
public sealed class SF5_4FleetTests
{
    private const string StateJson = """
        {"planName":"NINE STREETS","status":"Running","attentionReason":null,"stageId":"E",
         "stageTitle":"The three that mean it is not a game yet","doneCount":28,"totalCount":46,
         "totalCostUsd":272.02,"runId":"7951c3ca149a4c12a5a7fb973bbea1bf","repo":"C:/Code/sk-studio",
         "planDir":"C:/Code/sk-studio","stateDir":"C:/Code/sk-studio/.conductor","stages":[],"gates":[]}
        """;

    // ── 1. A stranger on the port is not a run ──────────────────────────────────────────────────

    [Fact]
    public void FromStateJson_reads_the_identity_a_fleet_listing_needs()
    {
        var run = FleetScan.FromStateJson(4318, StateJson);

        Assert.NotNull(run);
        Assert.Equal(4318, run!.Port);
        Assert.Equal("http://127.0.0.1:4318", run.BaseUrl);
        Assert.Equal("NINE STREETS", run.PlanName);
        Assert.Equal("7951c3ca149a4c12a5a7fb973bbea1bf", run.RunId);
        Assert.Equal("7951c3ca", run.ShortRunId);
        Assert.Equal("C:/Code/sk-studio", run.Repo);
        Assert.Equal("sk-studio", run.RepoLabel);
        Assert.Equal("C:/Code/sk-studio/.conductor", run.StateDir);
        Assert.Equal("Running", run.Status);
        Assert.Equal("E", run.StageId);
        Assert.Equal(28, run.Done);
        Assert.Equal(46, run.Total);
    }

    [Theory]
    [InlineData("""{"ok":true,"service":"otel-collector"}""")]   // valid JSON, wrong shape
    [InlineData("""{"planName":"","runId":""}""")]               // conductor-shaped, no identity
    [InlineData("[1,2,3]")]                                       // valid JSON, not an object
    [InlineData("<html>404</html>")]                              // a web server
    [InlineData("")]
    [InlineData(null)]
    public void FromStateJson_refuses_anything_that_is_not_a_conductor(string? body)
    {
        Assert.Null(FleetScan.FromStateJson(4321, body));
    }

    /// <summary>The contract that actually breaks: <c>ps</c> parses a body the SERVER wrote. If a field
    /// on <see cref="StateDto"/> is renamed, this fails here rather than silently emptying the fleet.</summary>
    [Fact]
    public void FromStateJson_parses_what_the_server_itself_serializes()
    {
        var served = JsonSerializer.Serialize(
            new StateDto("Sarban", "Paused", "needs human", "SF5", "Supervision", null, 18, 24,
                235.08m, 0m, 0, 0, 0, "SF5.4", "fleet basics", "", [],
                RunId: "8cefa5de8f164848bd42b275e14ba9cf", Repo: "C:/code/conductor", PlanDir: "C:/code/conductor",
                SessionNumber: 30, SessionKind: "session", Attempt: 1, MaxAttempts: 8,
                SessionElapsedSec: 0, AgentActive: true, SessionCostUsd: 0m,
                SessionTokensInput: 0, SessionTokensOutput: 0, SessionTokensReasoning: 0, Gates: [],
                StateDir: "C:/code/conductor/.conductor"),
            ControlPlaneJsonContext.Default.StateDto);

        var run = FleetScan.FromStateJson(4317, served);

        Assert.NotNull(run);
        Assert.Equal("Sarban", run!.PlanName);
        Assert.Equal("8cefa5de", run.ShortRunId);
        Assert.Equal("C:/code/conductor/.conductor", run.StateDir);
        Assert.Equal("needs human", run.AttentionReason);
    }

    // ── 2. The scan itself ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_returns_only_the_ports_that_answered_in_port_order()
    {
        var probed = new List<int>();
        FleetScan.StateProbe probe = (port, _) =>
        {
            lock (probed) probed.Add(port);
            return Task.FromResult<string?>(port is 4318 or 4320 ? StateJson : null);
        };

        var runs = await FleetScan.ScanAsync(probe, [4317, 4318, 4319, 4320, 4321]);

        Assert.Equal([4318, 4320], runs.Select(r => r.Port));
        Assert.Equal(5, probed.Count);
    }

    /// <summary>A port that hangs, resets, or throws is one dead port — never a failed listing. The whole
    /// point is seeing the OTHER runs when one of them is sick.</summary>
    [Fact]
    public async Task ScanAsync_survives_a_probe_that_throws()
    {
        FleetScan.StateProbe probe = (port, _) => port == 4319
            ? throw new HttpRequestException("connection reset")
            : Task.FromResult<string?>(port == 4318 ? StateJson : null);

        var runs = await FleetScan.ScanAsync(probe, [4318, 4319]);

        Assert.Single(runs);
        Assert.Equal(4318, runs[0].Port);
    }

    [Fact]
    public void DefaultPorts_is_exactly_the_window_the_server_binds()
    {
        Assert.Equal(4317, FleetScan.DefaultPorts[0]);
        Assert.Equal(4336, FleetScan.DefaultPorts[^1]);
        Assert.Equal(20, FleetScan.DefaultPorts.Count);
    }

    // ── 3. The pid is the engine's, or it is absent ─────────────────────────────────────────────

    private static FleetRun Row(int port = 4318) => FleetScan.FromStateJson(port, StateJson)!;

    [Fact]
    public void Enrich_takes_pid_and_start_from_the_discovery_file_that_names_this_port()
    {
        var discovery = JsonSerializer.Serialize(
            new ControlPlaneInfo(4318, "http://127.0.0.1:4318", 19056, "NINE STREETS",
                new DateTime(2026, 7, 31, 23, 55, 38, DateTimeKind.Utc), "tok"),
            ControlPlaneJsonContext.Default.ControlPlaneInfo);

        var run = FleetScan.Enrich(Row(), discovery, "99999\n2020-01-01T00:00:00.0000000Z");

        Assert.Equal(19056, run.Pid);
        Assert.True(run.HasDiscoveryFile);
        Assert.Equal(new DateTime(2026, 7, 31, 23, 55, 38, DateTimeKind.Utc), run.StartedUtc);
    }

    [Fact]
    public void Enrich_ignores_a_discovery_file_left_behind_by_a_plane_on_another_port()
    {
        var stale = JsonSerializer.Serialize(
            new ControlPlaneInfo(4399, "http://127.0.0.1:4399", 111, "NINE STREETS", DateTime.UtcNow, null),
            ControlPlaneJsonContext.Default.ControlPlaneInfo);

        var run = FleetScan.Enrich(Row(4318), stale, "222\n2026-07-31T23:55:38.0000000Z");

        Assert.Equal(222, run.Pid);            // the lock, not the stale file
        Assert.False(run.HasDiscoveryFile);
    }

    /// <summary>The case that made the probe the primary source: a live plane whose state dir has no
    /// discovery file at all. Measured on this repo's own run while SF5.4 was being written.</summary>
    [Fact]
    public void Enrich_falls_back_to_the_engine_lock_when_the_discovery_file_is_gone()
    {
        var run = FleetScan.Enrich(Row(), null, "35412\n2026-07-31T23:53:34.5108363Z");

        Assert.Equal(35412, run.Pid);
        Assert.False(run.HasDiscoveryFile);
        Assert.Equal(2026, run.StartedUtc!.Value.Year);
    }

    [Fact]
    public void Enrich_reads_a_legacy_bare_pid_lock_and_reports_no_start_time()
    {
        var run = FleetScan.Enrich(Row(), null, "4242");

        Assert.Equal(4242, run.Pid);
        Assert.Null(run.StartedUtc);
    }

    [Fact]
    public void Enrich_leaves_the_pid_unknown_rather_than_guessing()
    {
        var run = FleetScan.Enrich(Row(), "not json at all", null);

        Assert.Equal(0, run.Pid);
        Assert.False(run.HasDiscoveryFile);
        Assert.Null(run.StartedUtc);
    }

    // ── 4. One run is one row ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\code\conductor\.conductor", "C:/code/conductor/.conductor", true)]
    [InlineData("C:/code/conductor/.conductor/", "C:/code/conductor/.conductor", true)]
    [InlineData("C:/code/conductor/.conductor", "C:/code/other/.conductor", false)]
    [InlineData("", "C:/code/conductor/.conductor", false)]
    [InlineData(null, null, false)]
    public void SameDir_matches_the_same_state_dir_spelled_two_ways(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, FleetScan.SameDir(a, b));
    }

    [Fact]
    public async Task UnattachedRun_is_skipped_when_a_plane_already_claims_that_state_dir()
    {
        var answered = new[] { Row() };

        var orphan = await FleetScan.UnattachedRunAsync(@"C:\Code\sk-studio\.conductor", "NINE STREETS", answered);

        Assert.Null(orphan);   // same dir, different spelling — one run, one row
    }

    [Fact]
    public async Task UnattachedRun_is_null_when_nothing_holds_the_lock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sf54-fleet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(await FleetScan.UnattachedRunAsync(dir, "no engine here", []));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A headless engine (the control plane is opt-in) holds a lock and answers no port. It is a
    /// run, and a listing that omits it tells the reader it is safe to start a second engine.</summary>
    [Fact]
    public async Task UnattachedRun_lists_a_live_engine_that_has_no_control_plane()
    {
        var repo = Path.Combine(Path.GetTempPath(), "sf54-repo-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(repo, ".conductor");
        Directory.CreateDirectory(dir);
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            await File.WriteAllTextAsync(Path.Combine(dir, "conductor.lock"),
                $"{me.Id}\n{me.StartTime.ToUniversalTime():O}");

            var orphan = await FleetScan.UnattachedRunAsync(dir, "headless plan", []);

            Assert.NotNull(orphan);
            Assert.Equal(0, orphan!.Port);
            Assert.Equal(me.Id, orphan.Pid);
            Assert.Equal("headless plan", orphan.PlanName);
            Assert.Equal("no control plane", orphan.Status);
            // The repo is inferred from where the state dir sits, so the row reads like every other one.
            Assert.Equal(Path.GetFileName(repo), orphan.RepoLabel);
        }
        finally { Directory.Delete(repo, recursive: true); }
    }

    // ── The verb's own small decisions ──────────────────────────────────────────────────────────

    [Fact]
    public void ParsePorts_defaults_to_the_control_plane_window()
    {
        Assert.Equal(FleetScan.DefaultPorts, PsCommand.ParsePorts(null));
        Assert.Equal([4317, 4318, 4319], PsCommand.ParsePorts("4317-4319"));
        Assert.Equal([4317], PsCommand.ParsePorts("4317"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("4320-4317")]      // backwards
    [InlineData("4317-9999")]      // wider than any fleet — a port sweep, not a listing
    [InlineData("4317-4318-4319")]
    public void ParsePorts_refuses_a_window_that_is_not_one(string spec)
    {
        Assert.Null(PsCommand.ParsePorts(spec));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(90, "1m")]
    [InlineData(3600, "1h00")]
    [InlineData(46740, "12h59")]
    [InlineData(93600, "1d02")]
    public void Age_reads_at_a_glance(int seconds, string expected)
    {
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, PsCommand.Age(now.AddSeconds(-seconds), now));
    }

    [Fact]
    public void Age_is_a_question_mark_when_nothing_knows_the_start()
    {
        Assert.Equal("?", PsCommand.Age(null, DateTime.UtcNow));
    }
}
