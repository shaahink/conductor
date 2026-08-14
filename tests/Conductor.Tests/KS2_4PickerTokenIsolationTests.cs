using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Fleet;
using Conductor.Core.History;
using Conductor.Core.Http;
using Conductor.Core.Store;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS2.4 — one list of every run on this machine, and a write token that never crosses between them.
///
/// <para>The Face can now SWITCH runs without restarting, which turns a quiet mismatch into a live
/// one: the same process holds one run's screen and then another's, and the only thing standing
/// between "I paused the wrong website" and "I paused this one" is which token travelled with the
/// switch. The token is matched to a run by its STATE DIR, and state dirs arrive here from two
/// sources that spell them differently on Windows — off the wire from <c>/state</c>, and off a local
/// plan file. Compare them as strings and every run silently gets a null token (reads need none, so
/// nobody notices until a write is refused); compare them too loosely and a token reaches a run it
/// was not minted for.</para>
///
/// <para>What is pinned here, none of it "JSON round-trips":</para>
/// <list type="number">
/// <item>A TOKEN REACHES EXACTLY ITS OWN RUN. Across separators and case, and not at all when the
/// dictionary holds a directory no run in the fleet lives in.</item>
/// <item>A PAST ROW CARRIES NO TOKEN. There is nothing to authorise behind a finished run, and the
/// archive plane mints none — so the envelope must not have anywhere to put one.</item>
/// <item>A STALE DISCOVERY FILE HANDS OVER NOTHING. A token left by an engine that has since
/// restarted on another port would 401 every write, silently.</item>
/// <item>THE TWO WIRE SHAPES STAY APART. <c>ps --json</c> goes to stdout for anyone to read;
/// the Face envelope goes through the environment. Different types, on purpose.</item>
/// <item>ONE RUN IS ONE ROW, AND ITS STATUS IS RECONCILED. A run answering on a port is never also
/// listed as past, and a run whose engine is dead reaches the Face as ended, not running.</item>
/// </list>
/// </summary>
public sealed class KS2_4PickerTokenIsolationTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _root;

    public KS2_4PickerTokenIsolationTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks24-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_tmp, "home");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static FleetRun Run(int port, string repo, string stateDir, string status = "Running") =>
        new(Port: port, BaseUrl: $"http://127.0.0.1:{port}", PlanName: $"{repo} plan",
            RunId: $"run-{port}", Repo: repo, StateDir: stateDir, Status: status,
            StageId: "S1", StageTitle: "First", AttentionReason: null,
            Done: 1, Total: 2, CostUsd: 1m) { Pid = 1000 + port };

    // ── 1. A token reaches exactly its own run ──────────────────────────────────────────────────

    /// <summary>The switch is what makes this sharp: the Face holds run A's token, the user picks run
    /// B, and the envelope is the only thing that decides which token goes with which url. A token
    /// keyed on a directory NOT in the fleet must reach nobody at all — a stale entry that leaked into
    /// the nearest row would be a credential attached to the wrong website.</summary>
    [Fact]
    public void A_token_belongs_to_one_state_dir_and_reaches_no_other_run()
    {
        var fleet = new[]
        {
            Run(4317, "C:/code/conductor", @"C:\code\conductor\.conductor"),
            Run(4318, "C:/Code/sk-studio", "C:/Code/sk-studio/.conductor"),
            Run(4319, "C:/code/blog", "C:/code/blog/.conductor"),
        };
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["C:/code/conductor/.conductor"] = "tok-conductor",   // same dir, the wire's spelling
            [@"C:\code\SK-STUDIO\.conductor\"] = "tok-sk",        // same dir, another case + a slash
            ["C:/code/gone/.conductor"] = "tok-nobody",           // a run that is not in this fleet
        };

        var back = JsonSerializer.Deserialize<FaceFleet>(
            FaceTarget.Serialize(fleet, tokens, localStateDir: null), JsonOpts)!;

        Assert.Equal("tok-conductor", back.Runs[0].Token);
        Assert.Equal("tok-sk", back.Runs[1].Token);
        Assert.Null(back.Runs[2].Token);   // blog has no token; its neighbours' must not fall into it
        Assert.DoesNotContain("tok-nobody", back.Runs.Select(r => r.Token));
    }

    /// <summary>The whole envelope, as one string, must contain each token exactly once. A serializer
    /// that copied a token onto two rows would still pass a per-row assertion if the second row were
    /// the one checked for its own.</summary>
    [Fact]
    public void No_token_appears_twice_in_the_envelope()
    {
        var fleet = new[]
        {
            Run(4317, "C:/code/a", "C:/code/a/.conductor"),
            Run(4318, "C:/code/b", "C:/code/b/.conductor"),
        };
        var json = FaceTarget.Serialize(fleet, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["C:/code/a/.conductor"] = "tok-a",
            ["C:/code/b/.conductor"] = "tok-b",
        }, localStateDir: null);

        Assert.Equal(1, Occurrences(json, "tok-a"));
        Assert.Equal(1, Occurrences(json, "tok-b"));
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // ── 2. A past row carries no token ──────────────────────────────────────────────────────────

    /// <summary>KS2.2 serves a finished run from its <c>run.db</c> through a plane that mints no token
    /// at all, and the Face hides every write affordance when it holds none. That guarantee starts
    /// here: the past half of the envelope has no field a token could travel in.</summary>
    [Fact]
    public void A_finished_run_has_nowhere_in_the_envelope_to_carry_a_token()
    {
        SeedRun(RepoPath("finished"), "core", "run-finished-01", status: "completed");
        var page = FacePastRuns.Read(_root);

        var json = FaceTarget.Serialize([], new Dictionary<string, string>(StringComparer.Ordinal), null, page);

        using var doc = JsonDocument.Parse(json);
        var past = doc.RootElement.GetProperty("past").EnumerateArray().ToList();
        var row = Assert.Single(past);
        Assert.False(row.TryGetProperty("token", out _),
            "a past run acquired a token field — there is no plane behind it to authorise against");
        Assert.False(row.TryGetProperty("baseUrl", out _),
            "a past run acquired a base url — it has no control plane to attach to");
    }

    // ── 3. A stale discovery file hands over nothing ────────────────────────────────────────────

    /// <summary>Read off a real file, not a string: the port check is the only thing between a
    /// restarted engine's leftover token and a Face that 401s every write without saying why.</summary>
    [Fact]
    public async Task A_discovery_file_from_a_previous_engine_hands_over_no_token()
    {
        var stateDir = Path.Combine(_tmp, "restarted", ".conductor");
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(ControlPlaneDiscovery.PathFor(stateDir), JsonSerializer.Serialize(
            new ControlPlaneInfo(4317, "http://127.0.0.1:4317", 42, "core", DateTime.UtcNow, "tok-old"),
            ControlPlaneJsonContext.Default.ControlPlaneInfo)).ConfigureAwait(true);

        // The engine that wrote that file is gone and a new one answered on 4319.
        Assert.Null(await FleetScan.ReadTokenAsync(Run(4319, "C:/code/restarted", stateDir)).ConfigureAwait(true));
        // The same file, for the plane it actually names.
        Assert.Equal("tok-old",
            await FleetScan.ReadTokenAsync(Run(4317, "C:/code/restarted", stateDir)).ConfigureAwait(true));
    }

    // ── 4. The two wire shapes stay apart ───────────────────────────────────────────────────────

    /// <summary><c>ps --json</c> is world-readable output; the Face envelope is not. They are two types
    /// so that "add the token to the run record" cannot be done once and land in both — asserted over
    /// the TYPES rather than one serialized sample, because the sample only proves today's data.</summary>
    [Fact]
    public void The_ps_record_and_the_face_record_are_different_types_and_only_one_has_a_token()
    {
        static bool HasToken(Type t) =>
            t.GetProperties().Any(p => p.Name.Contains("token", StringComparison.OrdinalIgnoreCase));

        Assert.NotEqual(typeof(FleetRunDto), typeof(FaceFleetRun));
        Assert.False(HasToken(typeof(FleetRunDto)), "ps --json's record grew a token field");
        Assert.True(HasToken(typeof(FaceFleetRun)), "the Face envelope lost its token field");
        Assert.False(HasToken(typeof(FacePastRun)), "a finished run's record grew a token field");
    }

    // ── 5. One run is one row, and its status is reconciled ─────────────────────────────────────

    /// <summary>The picker's whole job is telling a live run from a finished one. A run answering on a
    /// port that ALSO appeared under "past" would read as two runs where there is one, and choosing the
    /// wrong copy would open a read-only archive of a run that is live right now.</summary>
    [Fact]
    public void A_run_answering_on_a_port_is_never_also_listed_as_past()
    {
        var repo = RepoPath("livest");
        SeedRun(repo, "core", "run-live-000001");
        SeedRun(RepoPath("over"), "core", "run-over-000001", status: "completed");

        // The live id arrives from the wire; the catalogue's copy differs only in case, which is how a
        // set that compares ordinally would list the same run twice.
        var page = FacePastRuns.Read(_root, ["RUN-LIVE-000001"]);

        var row = Assert.Single(page.Rows);
        Assert.Equal("run-over-000001", row.RunId);
        Assert.Equal(1, page.Total);
    }

    /// <summary>KS1.3's reconciled word has to survive the trip into the envelope, because the picker
    /// is where a stale <c>running</c> is most likely to be acted on — someone attaches to a run that
    /// died in July and waits for it to move.</summary>
    [Fact]
    public void A_dead_engines_run_reaches_the_face_as_ended_not_running()
    {
        SeedRun(RepoPath("killed"), "core", "run-killed-0001");   // status stays 'running' in the row

        var json = FaceTarget.Serialize([], new Dictionary<string, string>(StringComparer.Ordinal),
            null, FacePastRuns.Read(_root));

        using var doc = JsonDocument.Parse(json);
        var row = Assert.Single(doc.RootElement.GetProperty("past").EnumerateArray());
        Assert.Equal(RunLiveness.Orphaned, row.GetProperty("status").GetString());
    }

    /// <summary>"Across repos" and a silent cap of eight cannot both be true. The envelope carries the
    /// number there were, so the picker can say it is showing a page instead of implying it is showing
    /// the machine.</summary>
    [Fact]
    public void The_envelope_says_how_many_past_runs_it_is_a_page_of()
    {
        for (var i = 0; i < FacePastRuns.DefaultMax + 2; i++)
            SeedRun(RepoPath("r" + i), "core", $"run-many-{i:D6}", status: "completed");

        var page = FacePastRuns.Read(_root);
        Assert.Equal(FacePastRuns.DefaultMax, page.Rows.Count);
        Assert.Equal(FacePastRuns.DefaultMax + 2, page.Total);

        var json = FaceTarget.Serialize([], new Dictionary<string, string>(StringComparer.Ordinal), null, page);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(FacePastRuns.DefaultMax, doc.RootElement.GetProperty("past").GetArrayLength());
        Assert.Equal(FacePastRuns.DefaultMax + 2, doc.RootElement.GetProperty("pastTotal").GetInt32());
    }

    /// <summary>A machine that has finished nothing says nothing, rather than "showing 0 of 0".</summary>
    [Fact]
    public void An_empty_history_reports_a_total_of_zero_and_is_not_truncated()
    {
        var page = FacePastRuns.Read(_root);

        Assert.Empty(page.Rows);
        Assert.Equal(0, page.Total);
        Assert.False(page.Truncated);
        Assert.False(FacePastRunPage.Empty.Truncated);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private string RepoPath(string name)
    {
        var p = Path.Combine(_tmp, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private void SeedRun(string repo, string plan, string runId, string status = "running")
    {
        var db = Path.Combine(_root, "runs", StateHome.SlugFor(repo, plan), StateHome.RunDbFileName);
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun(runId, plan, repo, "master", EngineStamp.Parse("0.3.1-alpha+test"));
            store.SetRunId(runId);
            store.InitializeStage(runId, "S1", "First stage");
            store.Emit(new StageEntered { StageId = "S1", Title = "First stage" });
            store.SeedCheckpoints(runId, [("C1", "S1", "First checkpoint", "DONE", "abc1234", "e.md")]);
            if (!string.Equals(status, "running", StringComparison.Ordinal)) store.RecordRunEnd(runId, status);
        }
        StateCatalogue.Upsert(_root, repo, plan, db);
        SqliteConnection.ClearAllPools();
    }
}
