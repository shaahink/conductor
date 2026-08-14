using System.Diagnostics;
using System.Text.RegularExpressions;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Fleet;
using Conductor.Core.Planning;
using Conductor.Core.Store;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS2.1 — bare <c>conductor</c> is the app.
///
/// <para>Typing the program's name with no arguments is a question, and the answer used to be
/// forty-one verbs: a table of contents handed to someone who asked to come in. The hub answers what
/// was actually asked — what is running on this machine, what it remembers, what plans are here, and
/// the four things worth doing about any of it.</para>
///
/// <para>Four things are worth pinning, and "a menu appeared" is not one of them:</para>
/// <list type="number">
/// <item>THE DOOR DID NOT MOVE ANY WALL. <c>--help</c>'s verb list, <c>--version</c>, and the error on
/// an unknown verb are exactly what they were. This is the whole risk of the change: Spectre's
/// <c>SetDefaultCommand</c> would have made <c>conductor nosuchverb</c> parse as the hub with a stray
/// argument, and every script on this machine calls a verb.</item>
/// <item>ZERO PLANS AND ELEVEN PLANS ARE BOTH NORMAL. The front door may never prompt and may never
/// throw about which plan it is standing in — which is exactly what
/// <c>PlanSettings.ResolvePlanPath</c> does, so the hub must not be able to reach it.</item>
/// <item>A PIPE IS NOT A PERSON. Redirected output gets the board and exit 0, never a picker.</item>
/// <item>THE STATUS IS THE RECONCILED WORD. A run whose engine died in July is not "running" because a
/// column says so (KS1.3), and the hub reads that answer rather than forming a second one.</item>
/// </list>
/// </summary>
public sealed class KS2_1HubTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _root;

    public KS2_1HubTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks21-" + Guid.NewGuid().ToString("N")[..10]);
        _root = Path.Combine(_tmp, "home");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    private static FleetRun Live(int port, string repo, string status = "Running", string? attention = null) =>
        new(Port: port,
            BaseUrl: port > 0 ? $"http://127.0.0.1:{port}" : "",
            PlanName: $"{repo} plan",
            RunId: $"{port}951c3ca149a4c12a5a7fb973bbea1bf",
            Repo: repo,
            StateDir: $"{repo}/.conductor",
            Status: status,
            StageId: "KS2",
            StageTitle: "The open door",
            AttentionReason: attention,
            Done: 18, Total: 24, CostUsd: 12.34m)
        { Pid = 1000 + port, StartedUtc = Now.AddHours(-2) };

    private static FacePastRun Past(string repo, string status = "completed") =>
        new(repo, $"{repo} plan", "2b1f9c0155f24b0f9a1a9d3c9e4f7a11", status,
            18, 24, 42.75m, "2026-08-01T09:00:00Z", $"{repo}/run.db");

    private static HubModel Compose(
        IReadOnlyList<FleetRun>? live = null,
        IReadOnlyList<FacePastRun>? past = null,
        IReadOnlyList<PlanDiscovery.Candidate>? plans = null) =>
        HubModel.Compose("C:/home", "C:/cwd", live ?? [], past ?? [], plans ?? [], Now);

    private static string Text(HubModel model) => string.Join("\n", HubView.Board(model));

    // ── 1. one list, live and remembered ─────────────────────────────────────────────────────────

    [Fact]
    public void The_hub_lists_live_and_past_runs_in_one_list()
    {
        var model = Compose(
            live: [Live(4318, "C:/code/sk-studio"), Live(4317, "C:/code/conductor")],
            past: [Past("C:/code/blog")]);

        // Port order, not arrival order: the fleet probe answers concurrently.
        Assert.Equal(new[] { 4317, 4318 }, model.LiveRuns.Select(r => r.Port));
        Assert.Equal(new[] { "conductor", "sk-studio" }, model.LiveRuns.Select(r => r.Label));
        Assert.Equal(new[] { "blog" }, model.PastRuns.Select(r => r.Label));
        Assert.Equal(3, model.Runs.Count);

        var board = Text(model);
        Assert.Contains("live runs", board, StringComparison.Ordinal);
        Assert.Contains("past runs", board, StringComparison.Ordinal);
        Assert.Contains("sk-studio", board, StringComparison.Ordinal);
        Assert.Contains("blog", board, StringComparison.Ordinal);
    }

    /// <summary>An engine holding a lock with no control plane is a row you can see and cannot talk
    /// to. It must list — "nothing here" and "something here I cannot reach" are different facts — and
    /// it must not be offered as an attach target, because there is nothing to attach to.</summary>
    [Fact]
    public void A_run_with_no_control_plane_lists_but_is_not_attachable()
    {
        var model = Compose(live: [Live(4317, "C:/code/conductor"), Live(0, "C:/code/blog", "no control plane")]);

        Assert.Equal(2, model.LiveRuns.Count);
        Assert.Equal(new[] { 4317 }, model.Attachable.Select(r => r.Port));
        Assert.Contains("no plane", Text(model), StringComparison.Ordinal);
    }

    [Fact]
    public void The_board_names_the_state_home_and_where_you_are_standing()
    {
        var board = Text(Compose());

        // "Which database am I looking at" is the first question a wrong answer raises, so the board
        // answers it before anything else.
        Assert.Contains("C:/home", board, StringComparison.Ordinal);
        Assert.Contains("C:/cwd", board, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_machine_says_so_rather_than_showing_an_empty_table()
    {
        var board = Text(Compose());

        Assert.Contains($"nothing answering on ports {HubView.Ports}", board, StringComparison.Ordinal);
        Assert.Contains("remembers no finished runs yet", board, StringComparison.Ordinal);
        Assert.Contains("no plans here", board, StringComparison.Ordinal);
    }

    /// <summary>A parked run must not read like a healthy one on the one screen whose job is spotting
    /// the run that stopped needing electricity and started needing a human.</summary>
    [Fact]
    public void A_parked_run_says_what_it_is_waiting_for()
    {
        var model = Compose(live: [Live(4317, "C:/code/conductor", "NeedsHuman", "budget cap")]);

        Assert.Contains("NeedsHuman (budget cap)", Text(model), StringComparison.Ordinal);
    }

    /// <summary>A cell wider than its column is clipped, never allowed to push every column after it
    /// sideways — a board whose grid moves per row is a board nobody can scan.</summary>
    [Fact]
    public void A_long_reason_is_clipped_rather_than_shoving_the_grid()
    {
        var reason = new string('x', 200);
        var model = Compose(live:
        [
            Live(4317, "C:/code/conductor", "NeedsHuman", reason),
            Live(4318, "C:/code/sk-studio"),
        ]);

        var rows = HubView.Board(model).Where(l => l.Contains(":431", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(reason, rows[0], StringComparison.Ordinal);
        Assert.Contains("…", rows[0], StringComparison.Ordinal);
        // Same grid: the port cell starts at the same column in both rows.
        Assert.Equal(rows[0].IndexOf(":4317", StringComparison.Ordinal), rows[1].IndexOf(":4318", StringComparison.Ordinal));
    }

    // ── 2. zero plans and many plans are both normal ─────────────────────────────────────────────

    [Fact]
    public void A_directory_with_no_plan_is_a_normal_outcome()
    {
        var model = Compose(plans: []);

        Assert.Empty(model.Plans);
        Assert.Contains("conductor init", Text(model), StringComparison.Ordinal);
    }

    /// <summary>The directory this repo is checked out in holds eleven plans. That is the case
    /// <c>ResolvePlanPath</c> answers with a prompt (interactive) or a throw (redirected), and the
    /// front door may do neither — it lists them.</summary>
    [Fact]
    public void A_directory_with_many_plans_lists_them_all_without_choosing()
    {
        var model = Compose(plans:
        [
            new PlanDiscovery.Candidate("core", "C:/cwd/plans/core.plan.json"),
            new PlanDiscovery.Candidate("face", "C:/cwd/plans/face.plan.json"),
            new PlanDiscovery.Candidate("docs", "C:/cwd/plans/docs.plan.json"),
        ]);

        Assert.Equal(3, model.Plans.Count);
        var board = Text(model);
        foreach (var name in new[] { "core", "face", "docs" })
            Assert.Contains(name, board, StringComparison.Ordinal);
    }

    /// <summary>Module intent, not a style rule. <c>ResolvePlanPath</c> PROMPTS on an ambiguous
    /// directory and THROWS on an empty one; a front door that can reach it is a front door that can
    /// interrogate or refuse the person who just typed the program's name.</summary>
    [Fact]
    public void The_hub_can_never_reach_the_plan_resolver()
    {
        foreach (var file in new[] { "HubCommand.cs", "HubModel.cs", "HubView.cs", "HubActions.cs", "HubLaunch.cs" })
        {
            var path = Path.Combine(RepoRoot(), "src", "Conductor", "Commands", file);
            Assert.True(File.Exists(path), $"{file} is gone — the hub moved without this test moving with it");
            var code = Regex.Replace(File.ReadAllText(path), @"///.*$|//.*$", "",
                RegexOptions.Multiline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
            Assert.DoesNotContain("ResolvePlanPath", code, StringComparison.Ordinal);
        }
    }

    // ── 3. a pipe is not a person ────────────────────────────────────────────────────────────────

    [Fact]
    public void Redirected_either_way_gets_the_board_and_a_terminal_gets_the_hub()
    {
        Assert.False(HubCommand.PrefersBoard(outputRedirected: false, inputRedirected: false));
        Assert.True(HubCommand.PrefersBoard(outputRedirected: true, inputRedirected: false));
        // Input redirected matters on its own: the board would print to a real terminal and then the
        // prompt would read EOF from a pipe and never be answerable.
        Assert.True(HubCommand.PrefersBoard(outputRedirected: false, inputRedirected: true));
        Assert.True(HubCommand.PrefersBoard(outputRedirected: true, inputRedirected: true));
    }

    [Fact]
    public void The_hub_offers_exactly_four_actions()
    {
        Assert.Equal(4, HubActions.All.Count);
        Assert.Equal(
            new[] { HubActionKind.Attach, HubActionKind.Start, HubActionKind.PlanNew, HubActionKind.History },
            HubActions.All.Select(a => a.Kind));

        var board = Text(Compose());
        foreach (var action in HubActions.All)
        {
            Assert.Contains(action.Label, board, StringComparison.Ordinal);
            Assert.Contains(action.Hint, board, StringComparison.Ordinal);
        }

        // Quitting is the way out, not a fifth thing to do — and it is not on the board's list.
        Assert.DoesNotContain(HubActions.QuitLabel, board, StringComparison.Ordinal);
    }

    // ── 4. the reconciled word, end to end ───────────────────────────────────────────────────────

    /// <summary>Through the real catalogue path, not a stub: a store whose row still says
    /// <c>running</c> for an engine nothing is holding must reach the hub already reconciled. The hub
    /// forms no second opinion — that is what KS1.3 exists to prevent — so this proves the wiring.</summary>
    [Fact]
    public void A_run_whose_engine_is_dead_never_renders_as_running()
    {
        var repo = Path.Combine(_tmp, "killed");
        Directory.CreateDirectory(repo);
        SeedRun(repo, "core", "run-killed-ks21");

        var past = FacePastRuns.Read(_root);
        var model = HubModel.Compose(_root, repo, [], past, [], Now);

        var row = Assert.Single(model.PastRuns);
        Assert.Equal(RunLiveness.Orphaned, row.Status);
        Assert.DoesNotContain("running", Text(model), StringComparison.OrdinalIgnoreCase);
    }

    private void SeedRun(string repo, string plan, string runId)
    {
        var db = Path.Combine(_root, "runs", StateHome.SlugFor(repo, plan), StateHome.RunDbFileName);
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun(runId, plan, repo, "master", EngineStamp.Parse("0.4.1-alpha+test"));
            store.SetRunId(runId);
            store.InitializeStage(runId, "S1", "First stage");
            store.Emit(new StageEntered { StageId = "S1", Title = "First stage" });
            store.SeedCheckpoints(runId, [("C1", "S1", "First checkpoint", "DONE", "abc1234", "e.md")]);
        }
        StateCatalogue.Upsert(_root, repo, plan, db);
        SqliteConnection.ClearAllPools();
    }

    // ── 5. the door did not move any wall ────────────────────────────────────────────────────────

    /// <summary>The whole risk of this checkpoint. Drives the real binary, because the thing under
    /// test is what the PARSER does with an argv it has never seen.</summary>
    [Fact]
    public void An_unknown_verb_is_still_an_unknown_verb()
    {
        var (exit, output) = RunCli("nosuchverb");

        Assert.True(exit != 0,
            "`conductor nosuchverb` exited 0 — an unknown first token was swallowed as a default " +
            "command's argument. The hub must be reached by rewriting an EMPTY argv, never by " +
            $"SetDefaultCommand. Output was:\n{output}");
        Assert.Contains("Unknown command", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_help_verb_list_is_exactly_what_it_was()
    {
        var (exit, output) = RunCli("--help");
        Assert.Equal(0, exit);

        var plain = Ansi().Replace(output, "");
        foreach (var verb in RegisteredVerbs())
            Assert.True(ListsCommand(plain, verb), $"`--help` no longer lists `{verb}`");

        // The hub is registered and hidden, exactly so this list does not move. A visible entry here
        // would change the first page every reader and every doc line sees.
        foreach (var hidden in new[] { "hub", "run-record", "fake-agent", "hook-budget" })
            Assert.False(ListsCommand(plain, hidden), $"`{hidden}` is showing in `--help` — it is meant to be hidden");
    }

    [Fact]
    public void The_version_flag_still_answers_the_build()
    {
        var (exit, output) = RunCli("--version");

        Assert.Equal(0, exit);
        Assert.Contains(BuildInfo.Current.Full, output, StringComparison.Ordinal);
    }

    /// <summary>The acceptance, end to end: no plan anywhere, redirected output, exit 0, the
    /// caravanserai rather than the verb list.</summary>
    [Fact]
    public void Bare_conductor_in_an_empty_directory_prints_the_board_and_exits_zero()
    {
        var cwd = Path.Combine(_tmp, "empty");
        Directory.CreateDirectory(cwd);

        var (exit, output) = RunCli(Array.Empty<string>(), cwd);

        Assert.Equal(0, exit);
        Assert.Contains("caravanserai", output, StringComparison.Ordinal);
        Assert.Contains("no plans here", output, StringComparison.Ordinal);
        Assert.Contains(_root, output, StringComparison.OrdinalIgnoreCase);
        // Not the forty-one verbs.
        Assert.DoesNotContain("USAGE:", output, StringComparison.Ordinal);
        foreach (var action in HubActions.All)
            Assert.Contains(action.Label, output, StringComparison.Ordinal);
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────

    private static Regex Ansi() => new(@"\x1b\[[0-9;]*m", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));

    /// <summary>Is this verb a COMMAND entry in the help page — an indented word at the head of a
    /// line — rather than a word that happens to appear in some description?</summary>
    private static bool ListsCommand(string plainHelp, string verb) =>
        Regex.IsMatch(plainHelp, @"(?m)^\s{2,}" + Regex.Escape(verb) + @"(\s|$)",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));

    /// <summary>Every verb <c>Program.cs</c> registers and does not hide — the same scan
    /// <c>K7_2DocsVerbCoverageTests</c> and <c>B11_2DoctorAndCompletionTests</c> run, deliberately
    /// copied rather than shared: three bars that can only fall together are no bars at all.</summary>
    private static HashSet<string> RegisteredVerbs()
    {
        var program = Path.Combine(RepoRoot(), "src", "Conductor", "Program.cs");
        var verbs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(program))
        {
            var m = Regex.Match(line, @"AddCommand<\w+>\(""(?<verb>[a-z][a-z0-9-]*)""\)",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
            if (!m.Success) continue;
            if (line.Contains(".IsHidden()", StringComparison.Ordinal)) continue;
            verbs.Add(m.Groups["verb"].Value);
        }
        Assert.True(verbs.Count > 30, $"only {verbs.Count} verbs parsed out of Program.cs — the scan is broken");
        return verbs;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate repo root (Conductor.slnx)");
    }

    private (int Exit, string Output) RunCli(params string[] args) => RunCli(args, AppContext.BaseDirectory);

    /// <summary>Drives the real app assembly, in a directory of this test's choosing, against a state
    /// home of this test's choosing — the operator's real catalogue is never opened and never
    /// touched.</summary>
    private (int Exit, string Output) RunCli(string[] args, string cwd)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "conductor.dll");
        Assert.True(File.Exists(dll), $"app assembly not next to the tests: {dll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = cwd,
        };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment[StateHome.HomeEnvVar] = _root;
        // Trap 4: an inherited CONDUCTOR_PLAN beats the directory the child is standing in.
        psi.Environment.Remove("CONDUCTOR_PLAN");

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(120_000), "the CLI did not exit within 120s");
        return (p.ExitCode, stdout + stderr);
    }
}
