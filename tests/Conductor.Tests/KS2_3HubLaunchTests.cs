using System.Text.RegularExpressions;

using Conductor.Commands;
using Conductor.Core.Http;

namespace Conductor.Tests;

/// <summary>
/// KS2.3 — the hub's start action launches a DETACHED engine and attaches to what it measured.
///
/// <para>The sarban field log carried a hand-rolled <c>Start-Process … -RedirectStandardError</c>
/// incantation for this exact moment, and every retyping of it forgot one of the hard-won parts: the
/// per-launch capture file, the pid check against the discovery file, the settle window that catches
/// the plan-lock race. This checkpoint moves that shape INTO the engine, and these tests hold the
/// three claims that make it trustworthy:</para>
/// <list type="number">
/// <item>ORDER. The itinerary renders before anything spawns; nothing spawns without a yes; nothing
/// attaches except to a launch that measurably survived.</item>
/// <item>READ BACK, NEVER PREDICTED. The URL and token the Face gets are the ones out of the child's
/// own discovery file (<see cref="DetachOutcome.Info"/>), not a port anyone assumed.</item>
/// <item>ONE PATH. The hub reuses <c>RunDetach.SpawnAsync</c> — the same spawn <c>run --detach</c>
/// performs — and the doc incantation it retires stays retired.</item>
/// </list>
/// <para>The claim no unit test can settle — killing the Face leaves the engine alive and advancing —
/// is measured by the live rig in <c>.conductor/evidence/KS2/ks2-3.md</c>.</para>
/// </summary>
public sealed class KS2_3HubLaunchTests
{
    // ── the flow: a recorder in place of a terminal ──────────────────────────────────────────────

    private sealed class Recorder
    {
        public List<string> Calls { get; } = [];
        public int PreviewExit { get; set; }
        public bool Confirmed { get; set; } = true;
        public HubLaunchResult Launch { get; set; } = new(true, "http://127.0.0.1:4323", "tok", "detached");
        public int AttachExit { get; set; }
        public (string Url, string? Token)? Attached { get; private set; }
        public List<string> Said { get; } = [];

        public Task<int> RunAsync(string planPath = @"C:\rig\demo.plan.json") =>
            HubLaunch.StartFlowAsync(
                planPath,
                _ => { Calls.Add("preview"); return Task.FromResult(PreviewExit); },
                () => { Calls.Add("confirm"); return Confirmed; },
                _ => { Calls.Add("launch"); return Task.FromResult(Launch); },
                (url, token) => { Calls.Add("attach"); Attached = (url, token); return Task.FromResult(AttachExit); },
                Said.Add);
    }

    [Fact]
    public async Task The_itinerary_renders_before_anything_spawns()
    {
        var r = new Recorder();
        await r.RunAsync();

        // The whole acceptance clause in one list: preview, then consent, then spawn, then attach.
        Assert.Equal(new[] { "preview", "confirm", "launch", "attach" }, r.Calls);
    }

    [Fact]
    public async Task A_plan_whose_journey_fails_is_never_offered_a_launch()
    {
        var r = new Recorder { PreviewExit = 3 };
        var exit = await r.RunAsync();

        Assert.Equal(3, exit);
        Assert.Equal(new[] { "preview" }, r.Calls);   // no confirm, no spawn, no attach
    }

    [Fact]
    public async Task Declining_the_confirm_spawns_nothing()
    {
        var r = new Recorder { Confirmed = false };
        var exit = await r.RunAsync(@"C:\rig\demo.plan.json");

        Assert.Equal(0, exit);
        Assert.Equal(new[] { "preview", "confirm" }, r.Calls);
        // The way out still tells the person how to do it by hand — with the verb, not an incantation.
        Assert.Contains(r.Said, s => s.Contains(@"conductor run -p C:\rig\demo.plan.json --detach", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_attach_url_is_the_one_the_child_published()
    {
        var r = new Recorder
        {
            Launch = new HubLaunchResult(true, "http://127.0.0.1:4329", "tok-b", "detached"),
            AttachExit = 7,
        };
        var exit = await r.RunAsync();

        Assert.Equal(("http://127.0.0.1:4329", "tok-b"), r.Attached);
        Assert.Equal(7, exit);   // once attached, the Face's exit is the flow's exit
    }

    [Fact]
    public async Task A_dead_child_is_an_error_not_an_attach()
    {
        var r = new Recorder { Launch = new HubLaunchResult(false, null, null, "the engine (pid 42) exited") };
        var exit = await r.RunAsync();

        Assert.Equal(1, exit);
        Assert.DoesNotContain("attach", r.Calls);
        Assert.Contains("the engine (pid 42) exited", r.Said);
    }

    [Fact]
    public async Task An_alive_child_with_no_plane_says_so_and_does_not_attach()
    {
        var r = new Recorder { Launch = new HubLaunchResult(true, null, null, "alive, not published — console: x.log") };
        var exit = await r.RunAsync();

        Assert.Equal(0, exit);   // the run is up; that the hub cannot show it yet is not a failure
        Assert.DoesNotContain("attach", r.Calls);
        Assert.Contains("alive, not published — console: x.log", r.Said);
    }

    // ── the mapping from measured outcome to the three honest answers ────────────────────────────

    [Fact]
    public void A_spawn_that_never_started_reports_its_own_error()
    {
        var result = HubLaunch.ResultOf(DetachOutcome.Failed("cannot detach: no exe."));

        Assert.False(result.Ok);
        Assert.Null(result.BaseUrl);
        Assert.Equal("cannot detach: no exe.", result.Detail);
    }

    [Fact]
    public void A_survived_handshake_carries_url_and_token_from_the_discovery_file()
    {
        var info = new ControlPlaneInfo(4331, "http://127.0.0.1:4331", 4242, "rig", DateTime.UtcNow, "tok-x");
        var result = HubLaunch.ResultOf(new DetachOutcome(
            true, 4242, true, true, info, @"C:\rig\.conductor\logs\detach-x.log", @"C:\rig\.conductor", null));

        Assert.True(result.Ok);
        // The port the engine BOUND — 4331, not the 4317 anyone would have predicted.
        Assert.Equal("http://127.0.0.1:4331", result.BaseUrl);
        Assert.Equal("tok-x", result.Token);
        Assert.Contains("4242", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_alive_engine_with_no_plane_points_at_the_capture_log()
    {
        var result = HubLaunch.ResultOf(new DetachOutcome(
            true, 4242, true, EngineAlive: true, null, @"C:\rig\logs\detach-y.log", @"C:\rig", null));

        Assert.True(result.Ok);
        Assert.Null(result.BaseUrl);
        Assert.Contains(@"C:\rig\logs\detach-y.log", result.Detail, StringComparison.Ordinal);
        Assert.Contains("conductor face", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dead_engine_points_at_the_capture_log_and_fails()
    {
        var result = HubLaunch.ResultOf(new DetachOutcome(
            true, 4242, true, EngineAlive: false, null, @"C:\rig\logs\detach-z.log", @"C:\rig", null));

        Assert.False(result.Ok);
        Assert.Contains(@"C:\rig\logs\detach-z.log", result.Detail, StringComparison.Ordinal);
    }

    // ── the child's argv, exactly ────────────────────────────────────────────────────────────────

    /// <summary>The hub launches with default run settings, so this is the argv its child gets —
    /// pinned as a SEQUENCE because the contract names the shape: <c>run -p &lt;abs&gt; --headless
    /// --no-face [--port N]</c>, and never <c>--detach</c> (a child that re-detached would fork
    /// forever) and never a Face (a detached process has no console for one).</summary>
    [Fact]
    public void The_hubs_child_argv_is_the_detach_shape()
    {
        var args = RunDetach.ChildArgs(new RunCommand.Settings(), @"C:\rig\demo.plan.json");

        Assert.Equal(
            new[] { "run", "-p", @"C:\rig\demo.plan.json", "--headless", "--no-face", "--port", "4317" },
            args);
    }

    // ── one path, and the incantation stays retired ──────────────────────────────────────────────

    /// <summary>Module intent: the hub may not compose its own spawn. Exactly one file in Commands/
    /// calls <c>DetachedProcess.Start</c> — the shared detach path — and the hub's launcher names
    /// that path rather than a process API.</summary>
    [Fact]
    public void The_hub_and_the_verb_share_one_spawn()
    {
        var commands = Path.Combine(RepoRoot(), "src", "Conductor", "Commands");
        var spawners = Directory.EnumerateFiles(commands, "*.cs")
            .Where(f => Strip(File.ReadAllText(f)).Contains("DetachedProcess.Start(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["RunDetach.cs"], spawners);
        Assert.Contains("RunDetach.SpawnAsync",
            Strip(File.ReadAllText(Path.Combine(commands, "HubLaunch.cs"))), StringComparison.Ordinal);
    }

    /// <summary>The FIELD-LOG launch block this checkpoint retires must not come back: the sarban
    /// history doc now points at the verb, and no doc in the tree hands anyone a hand-rolled
    /// detached-conductor spawn again.</summary>
    [Fact]
    public void The_field_log_incantation_stays_retired()
    {
        var sarban = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "history", "CONDUCTOR-SARBAN.md"));

        Assert.DoesNotContain("Start-Process conductor", sarban, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run --detach", sarban, StringComparison.Ordinal);
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────

    private static string Strip(string source) =>
        Regex.Replace(source, @"///.*$|//.*$", "", RegexOptions.Multiline | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate repo root (Conductor.slnx)");
    }
}
