using System.Text.Json;

using Conductor.Core.Fleet;

namespace Conductor.Tests;

/// <summary>
/// SF5.4 (part 3) — which run does <c>conductor face</c> attach to, and what does it tell the Face?
///
/// <para>The old answer was "whatever <c>.conductor/control-plane.json</c> in this directory says",
/// which failed twice: it could never reach another repo's run, and it was wrong about its own —
/// measured on the run driving session 30, a live engine serving 4317 with no discovery file at all,
/// at which <c>conductor face</c> said "no live run".</para>
///
/// <para>Three things are worth pinning here, and none of them is "HTTP works":</para>
/// <list type="number">
/// <item>THE PRECEDENCE. Standing in a repo with a live run is itself an unambiguous answer and must
/// never raise a prompt; everything else that is ambiguous must, rather than guessing.</item>
/// <item>THE TOKEN REACHES THE RIGHT RUN AND NOTHING ELSE. State dirs arrive from two sources that
/// spell them differently on Windows, so a naive dictionary lookup hands every run a null token on
/// exactly the machine this ships to — and reads work without one, so the failure is silent.</item>
/// <item>THE TWO WIRE SHAPES STAY APART. <c>ps --json</c> goes to stdout for anyone to read and must
/// never grow a token field; the Face envelope goes through the environment and must carry one.</item>
/// </list>
/// </summary>
public sealed class SF5_4FacePickerTests
{
    private static FleetRun Run(int port, string repo, string? stateDir = null, string status = "Running") =>
        new(Port: port,
            BaseUrl: port > 0 ? $"http://127.0.0.1:{port}" : "",
            PlanName: $"{repo} plan",
            RunId: "7951c3ca149a4c12a5a7fb973bbea1bf",
            Repo: repo,
            StateDir: stateDir ?? $"{repo}/.conductor",
            Status: status,
            StageId: "SF5",
            StageTitle: "Supervision without a polling meter",
            AttentionReason: null,
            Done: 18, Total: 24, CostUsd: 12.34m) { Pid = 1000 + port };

    // ── 1. The precedence ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_run_in_this_directory_wins_without_a_prompt()
    {
        var fleet = new[] { Run(4317, "C:/code/conductor"), Run(4318, "C:/code/sk-studio"), Run(4319, "C:/code/blog") };

        var d = FaceTarget.Choose(fleet, "C:/code/sk-studio/.conductor", pick: false);

        Assert.Equal(FaceTarget.Kind.Single, d.Kind);
        Assert.Equal(4318, d.Run!.Port);
    }

    /// <summary>The wire says <c>C:/Code/sk-studio/.conductor</c>, the local plan says
    /// <c>C:\code\sk-studio\.conductor</c>. Same directory. Compare them as raw strings and the Face
    /// silently opens a picker in a repo whose run is sitting right there.</summary>
    [Fact]
    public void This_directory_is_recognised_across_separator_and_case()
    {
        var fleet = new[] { Run(4317, "C:/code/conductor"), Run(4318, "C:/Code/sk-studio") };

        var d = FaceTarget.Choose(fleet, @"C:\code\sk-studio\.conductor\", pick: false);

        Assert.Equal(FaceTarget.Kind.Single, d.Kind);
        Assert.Equal(4318, d.Run!.Port);
    }

    [Fact]
    public void The_only_run_on_the_machine_is_the_answer_even_from_an_unrelated_directory()
    {
        var d = FaceTarget.Choose([Run(4318, "C:/code/sk-studio")], localStateDir: null, pick: false);

        Assert.Equal(FaceTarget.Kind.Single, d.Kind);
        Assert.Equal(4318, d.Run!.Port);
    }

    [Fact]
    public void Several_runs_and_none_of_them_here_asks_rather_than_guessing()
    {
        var fleet = new[] { Run(4317, "C:/code/conductor"), Run(4318, "C:/code/sk-studio") };

        var d = FaceTarget.Choose(fleet, "C:/code/somewhere-else/.conductor", pick: false);

        Assert.Equal(FaceTarget.Kind.Picker, d.Kind);
        Assert.Null(d.Run);
        Assert.Equal(2, d.Fleet.Count);
    }

    /// <summary>The one case the directory rule cannot serve: "I am in repo A and want repo B".</summary>
    [Fact]
    public void Pick_overrides_the_directory_rule()
    {
        var fleet = new[] { Run(4317, "C:/code/conductor"), Run(4318, "C:/code/sk-studio") };

        var d = FaceTarget.Choose(fleet, "C:/code/conductor/.conductor", pick: true);

        Assert.Equal(FaceTarget.Kind.Picker, d.Kind);
        Assert.Equal(2, d.Fleet.Count);
    }

    [Fact]
    public void Pick_with_one_run_still_asks()
    {
        var d = FaceTarget.Choose([Run(4317, "C:/code/conductor")], "C:/code/conductor/.conductor", pick: true);

        Assert.Equal(FaceTarget.Kind.Picker, d.Kind);
        Assert.Single(d.Fleet);
    }

    /// <summary>An engine holding a lock with no control plane is a real row in <c>ps</c> — "a run here
    /// I cannot talk to" — but there is no socket for a Face to attach to, so it must not be offered.
    /// Offering it produces a picker whose only entry connects to nothing.</summary>
    [Fact]
    public void A_run_with_no_control_plane_is_never_offered()
    {
        var unattached = Run(0, "C:/code/headless", status: "no control plane");

        Assert.Equal(FaceTarget.Kind.None, FaceTarget.Choose([unattached], null, pick: false).Kind);
        Assert.Equal(FaceTarget.Kind.None, FaceTarget.Choose([unattached], null, pick: true).Kind);

        var mixed = FaceTarget.Choose([unattached, Run(4317, "C:/code/conductor")], null, pick: false);
        Assert.Equal(FaceTarget.Kind.Single, mixed.Kind);
        Assert.Equal(4317, mixed.Run!.Port);
    }

    [Fact]
    public void Nothing_answering_is_None_not_an_empty_picker()
    {
        var d = FaceTarget.Choose([], "C:/code/conductor/.conductor", pick: true);

        Assert.Equal(FaceTarget.Kind.None, d.Kind);
        Assert.Empty(d.Fleet);
    }

    // ── 2. The token reaches the right run, and only it ─────────────────────────────────────────

    [Fact]
    public void Each_run_gets_its_own_token_matched_across_windows_spellings()
    {
        var fleet = new[] { Run(4317, "C:/code/conductor"), Run(4318, "C:/Code/sk-studio") };
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [@"C:\code\conductor\.conductor"] = "tok-conductor",   // as a local plan spells it
            ["C:/code/sk-studio/.conductor"] = "tok-sk",           // as the wire spells it
        };

        var json = FaceTarget.Serialize(fleet, tokens, "C:/code/conductor/.conductor");
        var back = JsonSerializer.Deserialize<FaceFleet>(json, JsonOpts)!;

        Assert.Equal("tok-conductor", back.Runs[0].Token);
        Assert.Equal("tok-sk", back.Runs[1].Token);
    }

    [Fact]
    public void A_run_with_no_token_still_travels_it_is_just_read_only()
    {
        var json = FaceTarget.Serialize([Run(4318, "C:/code/sk-studio")], new Dictionary<string, string>(), null);
        var back = JsonSerializer.Deserialize<FaceFleet>(json, JsonOpts)!;

        Assert.Single(back.Runs);
        Assert.Null(back.Runs[0].Token);
    }

    [Fact]
    public void The_envelope_marks_the_run_in_this_directory()
    {
        var fleet = new[] { Run(4317, "C:/code/conductor"), Run(4318, "C:/code/sk-studio") };

        var back = JsonSerializer.Deserialize<FaceFleet>(
            FaceTarget.Serialize(fleet, new Dictionary<string, string>(), @"C:\code\sk-studio\.conductor"), JsonOpts)!;

        Assert.False(back.Runs[0].Self);
        Assert.True(back.Runs[1].Self);
    }

    /// <summary>A discovery file naming a different port belongs to a plane that is gone. Its token
    /// would 401 every write — silently, because reads never need one.</summary>
    [Theory]
    [InlineData("""{"port":4317,"baseUrl":"http://127.0.0.1:4317","pid":1,"planName":"p","startedUtc":"2026-08-01T00:00:00Z","token":"tok"}""", 4317, "tok")]
    [InlineData("""{"port":4319,"baseUrl":"http://127.0.0.1:4319","pid":1,"planName":"p","startedUtc":"2026-08-01T00:00:00Z","token":"tok"}""", 4317, null)]
    [InlineData("""{"port":4317,"baseUrl":"http://127.0.0.1:4317","pid":1,"planName":"p","startedUtc":"2026-08-01T00:00:00Z"}""", 4317, null)]
    [InlineData("not json", 4317, null)]
    [InlineData("", 4317, null)]
    [InlineData(null, 4317, null)]
    public void TokenFrom_only_trusts_a_discovery_file_that_names_this_port(string? discovery, int port, string? expected)
    {
        Assert.Equal(expected, FleetScan.TokenFrom(discovery, port));
    }

    // ── 3. The two wire shapes stay apart ───────────────────────────────────────────────────────

    /// <summary>The Face envelope and the Go picker are two halves of one contract, in two languages
    /// with no shared type. Renaming a property here is a silent break there — the picker would show a
    /// row of blanks and attach to an empty URL — so the field names are pinned.</summary>
    [Fact]
    public void The_envelope_field_names_are_the_contract_face_go_decodes()
    {
        var json = FaceTarget.Serialize([Run(4317, "C:/code/conductor")],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["C:/code/conductor/.conductor"] = "tok" },
            "C:/code/conductor/.conductor");

        using var doc = JsonDocument.Parse(json);
        var run = doc.RootElement.GetProperty("runs")[0];
        foreach (var field in new[]
        {
            "repo", "planName", "runId", "status", "port", "pid", "stageId", "stageTitle",
            "attentionReason", "done", "total", "costUsd", "baseUrl", "stateDir", "token", "self",
        })
            Assert.True(run.TryGetProperty(field, out _), $"CONDUCTOR_FLEET lost the '{field}' field face-go decodes");

        Assert.Equal("http://127.0.0.1:4317", run.GetProperty("baseUrl").GetString());
        Assert.Equal("tok", run.GetProperty("token").GetString());
    }

    /// <summary><c>ps --json</c> goes to stdout, where anyone may read it and a model may quote it. It
    /// has no token field and must never acquire one by someone reusing the Face's record.</summary>
    [Fact]
    public void The_ps_json_shape_carries_no_token()
    {
        var report = new FleetReport(DateTime.UtcNow, "4317-4336",
        [
            new FleetRunDto("C:/code/conductor", "plan", "run", "Running", 4317, 1234, "SF5", "Supervision",
                null, 18, 24, 12.34m, "http://127.0.0.1:4317", "C:/code/conductor/.conductor", null, true, true),
        ]);

        var json = JsonSerializer.Serialize(report, FleetJsonContext.Default.FleetReport);

        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
}
