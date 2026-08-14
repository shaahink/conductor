using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

using Conductor.Core.Events;
using Conductor.Core.Fleet;
using Conductor.Core.History;
using Conductor.Core.Http;
using Conductor.Core.Store;
using Conductor.Http;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS2.2 — the archive serves. A finished run, with no engine process anywhere, answers every read
/// route the Face polls; every write is refused with the reason "this run is finished" rather than the
/// live plane's token hint; and the database the session was served from is byte-identical afterwards.
///
/// <para>The read-only claim is not asserted from source. The run.db's sha256 and last-write time are
/// measured before the plane starts and again after it is disposed, with ten GETs and a POST in
/// between — which is precisely what pointing <see cref="SqliteRunStore"/> at an archived run would
/// have failed (its constructor creates directories, sets WAL and migrates).</para>
/// </summary>
public sealed class KS2_2ArchiveControlPlaneTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _root;
    private readonly HttpClient _http = new();

    private const string RunId = "run-archive-ks22";

    public KS2_2ArchiveControlPlaneTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks22-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_tmp, "home");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _http.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── fixture ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A whole finished run, written by the real writer so what the archive reads is what the
    /// engine actually stores: two sessions, costs, checkpoints, a ledger note, a bug, a score, a gate
    /// and one registered artifact.</summary>
    private string SeedRun(string plan = "archive-plan", string status = "completed")
    {
        var repo = Path.Combine(_tmp, "repo");
        Directory.CreateDirectory(repo);
        var db = Path.Combine(_root, "runs", StateHome.SlugFor(repo, plan), StateHome.RunDbFileName);
        var started = new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun(RunId, plan, repo, "master", Conductor.Core.EngineStamp.Parse("0.9.0+abc123"));
            store.SetRunId(RunId);
            store.InitializeStage(RunId, "S1", "First stage");
            store.Emit(new StageEntered { StageId = "S1", Title = "First stage" });
            for (var i = 1; i <= 2; i++)
            {
                store.Emit(new SessionStarted { Number = i, StageId = "S1", Kind = "deliver", Attempt = 1, MaxAttempts = 3, Model = "opus" });
                store.RecordSession(RunId, "S1", i, "deliver",
                    started.AddHours(i), started.AddHours(i).AddMinutes(20), "advance",
                    agentSessionId: null, resumeCount: 0, attempt: 1,
                    gateSummary: "fast: pass", resultSummary: $"session {i} landed", commitCount: i, newlyDone: null);
                store.RecordCost(RunId, i, "agent", 100, 200, 0, 300, 1.25m, 1000);
                store.Emit(new SessionFinished
                {
                    Number = i, StageId = "S1", Outcome = "advance",
                    NewCommits = [$"abc{i} commit {i}"], CostUsd = 1.25m,
                });
            }
            store.RecordCost(RunId, 2, "gate", 0, 0, 0, 0, 0.10m, 500);
            store.Emit(new GateFinished { Name = "fast", Passed = true, DurationMs = 1200, Scope = "S1" });
            store.Emit(new EvidenceRegistered
            {
                Path = "evidence/one.md", Kind = "text", Sha256 = "deadbeef", Bytes = 42,
                CheckpointId = "C1", StageId = "S1", SessionNumber = 1, Source = "agent",
            });
            store.SeedCheckpoints(RunId,
            [
                ("C1", "S1", "First checkpoint", "DONE", "abc1234", "evidence/one.md"),
                ("C2", "S1", "Second checkpoint", "TODO", "-", "-"),
            ]);
            store.WriteLedger(RunId, 1, "S1", "note", "the archive remembers this");
            store.WriteBug(RunId, "a bug the run filed", "detail", "high", "S1", 1);
            store.WriteScore(RunId, 2, "S1", 91, "PASS", "nothing to add");
            store.Emit(new StageConfirmed { StageId = "S1" });
            if (!string.Equals(status, "running", StringComparison.Ordinal)) store.RecordRunEnd(RunId, status);
        }
        // The writer is disposed, but Microsoft.Data.Sqlite pools the connection and the OS handle
        // outlives it — which is what makes a later File.Delete/OpenRead in these tests fail on
        // Windows. Same reason K3_2HistoryTests clears the pools around its fixtures.
        SqliteConnection.ClearAllPools();
        StateCatalogue.Upsert(_root, repo, plan, db);
        return db;
    }

    private static int FreeLoopbackPort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private ArchiveControlPlane StartPlane(ArchiveView view)
    {
        var plane = new ArchiveControlPlane(view, NullLogger.Instance, FreeLoopbackPort());
        Assert.True(plane.Start());
        return plane;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>The ten routes the acceptance names, in the order it names them.</summary>
    private static readonly string[] ReadRoutes =
        ["/version", "/state", "/tasks", "/sessions", "/timeline", "/ledger", "/bugs", "/evidence", "/scores", "/plan"];

    // ── the routes ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_read_route_answers_200_with_archive_derived_data()
    {
        SeedRun();
        var view = ArchiveView.Open(_root, RunId, out var refusal);
        Assert.NotNull(view);
        Assert.Equal("", refusal);

        using var plane = StartPlane(view!);
        foreach (var route in ReadRoutes)
        {
            using var response = await _http.GetAsync(new Uri(plane.BaseUrl + route));
            Assert.True(response.IsSuccessStatusCode, $"{route} answered {(int)response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(body), $"{route} answered an empty body");
        }
    }

    [Fact]
    public async Task State_carries_the_finished_run_and_never_reports_it_running()
    {
        SeedRun();
        var view = ArchiveView.Open(_root, RunId, out _)!;
        using var plane = StartPlane(view);

        var state = JsonSerializer.Deserialize(
            await _http.GetStringAsync(new Uri(plane.BaseUrl + "/state")),
            ControlPlaneJsonContext.Default.StateDto)!;

        Assert.Equal(RunId, state.RunId);
        Assert.Equal("archive-plan", state.PlanName);
        // KS1.3's reconciled word, and there is no engine, so it can never be "running".
        Assert.NotEqual("running", state.Status, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, state.TotalCount);
        Assert.Equal(1, state.DoneCount);
        Assert.Equal(2.6m, state.TotalCostUsd);      // 2 x 1.25 agent + 0.10 gate
        Assert.Equal(0.10m, state.OverheadCostUsd);  // the gate row alone
        Assert.False(state.AgentActive);
        Assert.Empty(state.Gates);                   // no live battery to report
        Assert.Equal("S1", Assert.Single(state.Stages).Id);
    }

    [Fact]
    public async Task Sessions_money_timeline_and_report_all_render_from_the_archive()
    {
        SeedRun();
        var view = ArchiveView.Open(_root, RunId, out _)!;
        using var plane = StartPlane(view);

        var sessions = JsonSerializer.Deserialize(
            await _http.GetStringAsync(new Uri(plane.BaseUrl + "/sessions")),
            ControlPlaneJsonContext.Default.SessionsDto)!;
        Assert.Equal(2, sessions.Sessions.Count);
        Assert.Equal("deliver", sessions.Sessions[0].Kind);
        Assert.Equal(1.25, sessions.Sessions[0].CostUsd, 3);
        Assert.Equal(300, sessions.Sessions[0].TokensCache);
        // The provider never reported reasoning tokens, so the archive says null, not zero.
        Assert.Null(sessions.Sessions[0].TokensThink);
        Assert.Contains("abc1 commit 1", sessions.Sessions[0].Commits!);

        var timeline = JsonSerializer.Deserialize(
            await _http.GetStringAsync(new Uri(plane.BaseUrl + "/timeline")),
            ControlPlaneJsonContext.Default.TimelineDto)!;
        Assert.Contains(timeline.Entries, e => e.Kind == "stage" && e.Description.Contains("S1 entered", StringComparison.Ordinal));
        Assert.Contains(timeline.Entries, e => e.Kind == "gate" && e.Description.Contains("fast", StringComparison.Ordinal));
        Assert.Contains(timeline.Entries, e => e.Kind == "session" && e.SessionNumber == 2);

        var ledger = JsonSerializer.Deserialize(
            await _http.GetStringAsync(new Uri(plane.BaseUrl + "/ledger")),
            ControlPlaneJsonContext.Default.LedgerDto)!;
        Assert.Contains("the archive remembers this", Assert.Single(ledger.Entries).Content, StringComparison.Ordinal);

        var bugs = JsonSerializer.Deserialize(
            await _http.GetStringAsync(new Uri(plane.BaseUrl + "/bugs")),
            ControlPlaneJsonContext.Default.BugsDto)!;
        Assert.Equal("a bug the run filed", Assert.Single(bugs.Bugs).Title);

        var scores = JsonSerializer.Deserialize(
            await _http.GetStringAsync(new Uri(plane.BaseUrl + "/scores")),
            ControlPlaneJsonContext.Default.ScoresDto)!;
        var score = Assert.Single(scores.Scores);
        Assert.Equal(91, score.Score);
        Assert.True(score.Passed);
        // The bar a score was judged against lives in the plan, which is not in the database — the
        // archive reports the recorded verdict rather than inventing the threshold behind it.
        Assert.Equal(0, score.Threshold);

        var evidence = JsonSerializer.Deserialize(
            await _http.GetStringAsync(new Uri(plane.BaseUrl + "/evidence")),
            ControlPlaneJsonContext.Default.EvidenceDto)!;
        Assert.Equal(1, evidence.Count);
        Assert.Equal("evidence/one.md", Assert.Single(evidence.Artifacts).Path);

        var tasks = JsonSerializer.Deserialize(
            await _http.GetStringAsync(new Uri(plane.BaseUrl + "/tasks")),
            ControlPlaneJsonContext.Default.TasksDto)!;
        Assert.Equal(2, tasks.Tasks.Count);

        var plan = JsonSerializer.Deserialize(
            await _http.GetStringAsync(new Uri(plane.BaseUrl + "/plan")),
            ControlPlaneJsonContext.Default.PlanDto)!;
        Assert.Equal("archive-plan", plan.Name);
        Assert.Equal("S1", Assert.Single(plan.Stages).Id);
        Assert.Equal("fast", Assert.Single(plan.Gates).Name);
    }

    // ── the refusal ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_post_is_refused_with_this_run_is_finished_and_never_a_token_hint()
    {
        SeedRun();
        var view = ArchiveView.Open(_root, RunId, out _)!;
        using var plane = StartPlane(view);

        foreach (var route in new[] { "/control", "/inject", "/note", "/bug", "/plan/edit", "/tasks/update" })
        {
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(new Uri(plane.BaseUrl + route), content);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("this run is finished", body, StringComparison.OrdinalIgnoreCase);
            // The live plane's 401 says "read it from .conductor/control-plane.json", which for a
            // finished run is an instruction that cannot be carried out. It must not appear.
            Assert.DoesNotContain("X-Conductor-Token", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("control-plane.json", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_write_token_does_not_help_because_there_is_none()
    {
        SeedRun();
        var view = ArchiveView.Open(_root, RunId, out _)!;
        using var plane = StartPlane(view);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(plane.BaseUrl + "/control"))
        {
            Content = new StringContent("{\"command\":\"pause\"}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Conductor-Token", "whatever-a-caller-invented");
        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        // The type carries no Token property at all: an archive plane that could mint one would be a
        // live plane with the run loop missing.
        Assert.Null(typeof(ArchiveControlPlane).GetProperty("Token"));
    }

    /// <summary>The Face polls <c>/prompt/blocks</c> for the Kanban card's prompt preview. An archive
    /// cannot compose one — the blocks come from the plan FILE, in a repo this machine may no longer
    /// have. It says so at 200 in the contract's own shape rather than 404ing, which the Face renders
    /// as a dead connection over a card that is otherwise fine.</summary>
    [Fact]
    public async Task The_one_read_an_archive_cannot_do_says_why_instead_of_breaking_the_card()
    {
        SeedRun();
        var view = ArchiveView.Open(_root, RunId, out _)!;
        using var plane = StartPlane(view);

        using var response = await _http.GetAsync(new Uri(plane.BaseUrl + "/prompt/blocks?task=C1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var blocks = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ControlPlaneJsonContext.Default.PromptBlocksDto)!;
        Assert.False(blocks.Ok);
        Assert.Empty(blocks.Blocks);
        Assert.Contains("this run is finished", blocks.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Face hides every write affordance on <c>HasWriteToken() == false</c>, and it answers that
    /// from the token it holds — which a child process INHERITS. A <c>CONDUCTOR_TOKEN</c> exported in
    /// the shell, or left behind by an earlier live attach, would otherwise walk straight into the
    /// archive Face and light up buttons whose every press the plane refuses.
    /// </summary>
    [Fact]
    public void Attaching_to_an_archive_strips_every_inherited_write_credential()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CONDUCTOR_TOKEN"] = "a-token-from-some-other-run",
            [FaceTarget.FleetEnvVar] = "{\"runs\":[{\"token\":\"another\"}]}",
            ["CONDUCTOR_PICK"] = @"C:\temp\pick.txt",
            ["PATH"] = "left alone",
        };

        Conductor.Commands.FaceCommand.StripWriteCredentials(env);

        Assert.DoesNotContain("CONDUCTOR_TOKEN", env.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain(FaceTarget.FleetEnvVar, env.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("CONDUCTOR_PICK", env.Keys, StringComparer.Ordinal);
        // Scrubbing credentials is not the same as emptying the environment.
        Assert.Equal("left alone", env["PATH"]);
    }

    // ── the file ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Serving_a_run_leaves_its_database_byte_identical()
    {
        var db = SeedRun();
        var shaBefore = Sha256Of(db);
        var sizeBefore = new FileInfo(db).Length;
        var writtenBefore = File.GetLastWriteTimeUtc(db);
        var schemaBefore = SchemaOf(db);

        var view = ArchiveView.Open(_root, RunId, out _)!;
        using (var plane = StartPlane(view))
        {
            foreach (var route in ReadRoutes)
                using (await _http.GetAsync(new Uri(plane.BaseUrl + route))) { }
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using (await _http.PostAsync(new Uri(plane.BaseUrl + "/control"), content)) { }
        }
        // The archive's read-only connections are pooled too; releasing them is about the file HANDLE,
        // not the bytes — the assertions below are the bytes.
        SqliteConnection.ClearAllPools();

        Assert.Equal(sizeBefore, new FileInfo(db).Length);
        Assert.Equal(shaBefore, Sha256Of(db));
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(db));
        Assert.Equal(schemaBefore, SchemaOf(db));
    }

    private static string SchemaOf(string db)
    {
        var archive = RunArchive.TryOpen(db)!;
        var rows = archive.Query("SELECT name, sql FROM sqlite_master ORDER BY name");
        return string.Join("\n", rows.Select(r => $"{r["name"]}::{r["sql"]}"));
    }

    // ── discovery, ports, and staying out of `ps` ────────────────────────────────────────────────

    [Fact]
    public void An_archive_plane_binds_outside_the_fleet_window_and_publishes_no_discovery_file()
    {
        // Anything answering /state on 4317-4336 shows up in `conductor ps` and in the hub as a live
        // run. An archive is not a live run, so its default window is deliberately clear of that one.
        Assert.True(ArchiveControlPlane.FirstPort >= FleetScan.FirstPort + FleetScan.PortSpan);

        var db = SeedRun();
        var stateDir = Path.GetDirectoryName(db)!;
        var view = ArchiveView.Open(_root, RunId, out _)!;
        using (StartPlane(view)) { }

        Assert.False(File.Exists(ControlPlaneDiscovery.PathFor(stateDir)),
            "an archive plane wrote a discovery file — FleetScan would list a finished run as live");
    }

    /// <summary>
    /// The constant above is a default; this is the RULE. `--port 4320` was accepted before this, the
    /// plane bound it, and `conductor ps` then listed the archive as a run of its own — the exact lie
    /// the port choice exists to prevent. Every port the probe scans is refused now, by the plane and by
    /// the verb, whoever asked for it.
    /// </summary>
    [Fact]
    public async Task A_port_inside_the_fleet_window_is_refused_by_the_plane_and_by_the_verb()
    {
        SeedRun();
        var view = ArchiveView.Open(_root, RunId, out _)!;

        for (var port = FleetScan.FirstPort; port < FleetScan.FirstPort + FleetScan.PortSpan; port++)
            Assert.True(ArchiveControlPlane.InsideFleetWindow(port), $"{port} is a port `ps` probes");
        Assert.False(ArchiveControlPlane.InsideFleetWindow(ArchiveControlPlane.FirstPort));

        // The plane refuses to bind it at all — nothing listens, so nothing can be discovered.
        using var plane = new ArchiveControlPlane(view, NullLogger.Instance, FleetScan.FirstPort + 3);
        Assert.False(plane.Start(), "an archive plane bound a port inside the fleet window");
        Assert.False(plane.IsRunning);

        // And the door says so before it opens anything: no catalogue read, no plane, exit 1.
        var exit = await Conductor.Commands.FaceCommand.ArchiveAsync(RunId, serveOnly: true, port: FleetScan.FirstPort + 3);
        Assert.Equal(1, exit);

        // A forward scan that starts below the window steps over it rather than wandering in.
        var below = FleetScan.FirstPort - 2;
        using var scanning = new ArchiveControlPlane(view, NullLogger.Instance, below);
        if (scanning.Start()) Assert.False(ArchiveControlPlane.InsideFleetWindow(scanning.Port));
    }

    // ── failing soft ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_catalogued_run_whose_database_is_gone_refuses_by_name_and_never_throws()
    {
        var db = SeedRun(plan: "vanishing");
        File.Delete(db);

        // The listing still shows it — that is KS1.3's contract and the picker depends on it.
        var row = Assert.Single(RunHistory.List(_root));
        Assert.False(row.Readable);

        var view = ArchiveView.Open(_root, row.Slug, out var refusal);
        Assert.Null(view);
        Assert.Contains("gone", refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(db, refusal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half of the same clause, and the half that was missing: the PICKER and the HUB must
    /// list such a row, not just <c>conductor history</c>. They read <see cref="FacePastRuns"/>, which
    /// dropped every row whose store would not open — which is precisely the row this refusal exists
    /// for. The precise sentence was therefore only reachable by typing the slug by hand.
    /// </summary>
    [Fact]
    public void The_picker_and_the_hub_list_a_run_whose_database_is_gone_and_can_still_open_it()
    {
        var readable = SeedRun(plan: "still-here");
        var broken = SeedRun(plan: "vanishing");
        File.Delete(broken);

        var past = FacePastRuns.Read(_root);
        Assert.Equal(2, past.Rows.Count);

        var gone = Assert.Single(past.Rows, p => !p.Readable);
        Assert.Equal("", gone.RunId);                       // nothing could be read to get one
        Assert.NotEqual("", gone.Selector);                 // and the slug is what names it instead
        Assert.Contains(broken, gone.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gone", gone.Problem, StringComparison.OrdinalIgnoreCase);
        var opens = Assert.Single(past.Rows, p => p.Readable);
        Assert.Equal(RunId, opens.RunId);
        Assert.Equal(RunId, opens.Selector);
        Assert.Equal(readable, opens.RunDb, StringComparer.OrdinalIgnoreCase);

        // The picker's envelope carries it, selector and all — that is the id the Face hands back.
        var json = FaceTarget.Serialize([], new Dictionary<string, string>(StringComparer.Ordinal), null, past);
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("past").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        var wire = rows.Single(r => r.GetProperty("runId").GetString() == "");
        Assert.Equal(gone.Selector, wire.GetProperty("selector").GetString());
        Assert.Contains("gone", wire.GetProperty("problem").GetString()!, StringComparison.OrdinalIgnoreCase);

        // The hub's board prints it, with the reason on the row rather than the row simply absent.
        var model = Conductor.Commands.HubModel.Compose(_root, _tmp, [], past.Rows, [], DateTime.UtcNow);
        var board = string.Join("\n", Conductor.Commands.HubView.Board(model));
        Assert.Contains("vanishing", board, StringComparison.Ordinal);
        Assert.Contains("gone", board, StringComparison.OrdinalIgnoreCase);

        // And what the picker hands back reaches the refusal this checkpoint built.
        Assert.Null(ArchiveView.Open(_root, gone.Selector, out var refusal));
        Assert.Equal(gone.Problem, refusal, StringComparer.Ordinal);
    }

    [Fact]
    public void A_catalogued_path_that_is_not_a_run_database_says_so_rather_than_missing()
    {
        var db = SeedRun(plan: "corrupt");
        File.WriteAllText(db, "this is not a database");

        var row = Assert.Single(RunHistory.List(_root));
        var view = ArchiveView.Open(_root, row.Slug, out var refusal);

        Assert.Null(view);
        Assert.Contains("not a conductor run database", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unmatched_selector_names_the_verb_that_lists_runs()
    {
        SeedRun();
        Assert.Null(ArchiveView.Open(_root, "no-such-run", out var refusal));
        Assert.Contains("conductor history", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_that_is_not_there_is_refused_as_a_path_not_as_a_missing_selector()
    {
        SeedRun();
        var absent = Path.Combine(_tmp, "nowhere", "run.db");

        Assert.Null(ArchiveView.Open(_root, absent, out var refusal));
        // "nothing in this machine's history matches C:\...\run.db" is true and useless — it sends
        // the reader to the catalogue for a question about a file.
        Assert.DoesNotContain("conductor history", refusal, StringComparison.Ordinal);
        Assert.Contains(absent, refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Opening_a_database_directly_takes_the_run_in_it()
    {
        var db = SeedRun();
        var view = ArchiveView.OpenDb(db, null, out var refusal);

        Assert.NotNull(view);
        Assert.Equal("", refusal);
        Assert.Equal(RunId, view!.Run.RunId);
        Assert.Equal(db, view.RunDbPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_path_opens_even_when_no_catalogue_knows_it()
    {
        var db = SeedRun();
        var copy = Path.Combine(_tmp, "handed-over.db");
        File.Copy(db, copy);

        // An empty state home: nothing indexes this file, and it opens anyway. The catalogue is an
        // index, and an index that could veto a database plainly sitting there would be pretending
        // to be the truth.
        var elsewhere = Path.Combine(_tmp, "empty-home");
        Directory.CreateDirectory(elsewhere);

        var view = ArchiveView.Open(elsewhere, copy, out var refusal);
        Assert.NotNull(view);
        Assert.Equal("", refusal);
        Assert.Equal(RunId, view!.Run.RunId);
    }

    // ── the run that has not finished ────────────────────────────────────────────────────────────

    [Fact]
    public void A_run_row_still_claiming_to_run_with_no_engine_is_served_reconciled()
    {
        SeedRun(status: "running");
        var view = ArchiveView.Open(_root, RunId, out _)!;

        // The stored word survives on the record; the served word is the reconciled one.
        Assert.Equal("running", view.Run.Status, StringComparer.OrdinalIgnoreCase);
        Assert.False(view.StoreLooksLive);
        Assert.NotEqual("running", view.Status, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("running", view.State().Status, StringComparer.OrdinalIgnoreCase);
    }
}
