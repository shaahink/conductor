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
/// KS2.5 — <c>conductor status</c> in a directory that names no plan.
///
/// <para>The verb people type when they do not know what is going on used to answer them with an
/// exception. An empty directory got <i>No plan found</i>; a directory with several plan files — this
/// repo has eleven under <c>plans/</c> — got <i>Multiple plan files found and output is not interactive
/// to prompt</i> the moment output was redirected, and a picker when it was not. Both refuse, and one of
/// them refuses through the crash handler, so asking the question in the wrong directory also left a
/// <c>crash-*.log</c> in it.</para>
///
/// <para>Four things are pinned here, and "it does not throw" is only the first:</para>
/// <list type="number">
/// <item>THE SENTENCE IS UNREACHABLE. Not caught, not reworded — the board branch is taken before the
/// resolver is ever called, so there is nothing to throw.</item>
/// <item>THE BRANCH IS CONSOLE-BLIND. Whether output is redirected cannot change which branch runs, or
/// the acceptance would be untestable on a terminal — which is where the prompt lives.</item>
/// <item>THE OTHER THIRTY VERBS DID NOT MOVE. KS0.3's CWD-over-<c>CONDUCTOR_PLAN</c> precedence is
/// borrowed, not re-stated, and a plan that resolves still resolves to exactly the same file.</item>
/// <item>THE BOARD IS TRUTHFUL. A run whose engine is dead lists as ended (KS1.3), because the board is
/// composed from the same reconciled rows the hub and the picker read.</item>
/// </list>
/// </summary>
public sealed class KS2_5StatusBoardTests : IDisposable
{
    /// <summary>The refusal this checkpoint deletes, quoted exactly as <c>PlanSettings</c> throws it.</summary>
    private const string TheRefusal = "Multiple plan files found and output is not interactive to prompt";

    private readonly string _tmp;
    private readonly string _root;

    public KS2_5StatusBoardTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks25-" + Guid.NewGuid().ToString("N")[..10]);
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

    // ── 1. which branch, decided purely ──────────────────────────────────────────────────────────

    private static IReadOnlyList<PlanDiscovery.Candidate> Found(params string[] paths)
        => paths.Select(p => new PlanDiscovery.Candidate(Path.GetFileNameWithoutExtension(p), p)).ToList();

    private static string? Choose(string? explicitPlan, string? env, IReadOnlyList<PlanDiscovery.Candidate> here,
                                  Func<string, bool>? exists = null)
        => StatusBoard.PlanForStatus(explicitPlan, env, here, exists ?? (_ => true));

    [Fact]
    public void No_plan_anywhere_is_the_board_not_an_error()
    {
        Assert.Null(Choose(null, null, Found()));
    }

    /// <summary>The headline case. Several plan files and nothing choosing between them is the
    /// directory this repo is checked out in, and the answer was an exception.</summary>
    [Fact]
    public void Several_plans_and_nothing_choosing_between_them_is_the_board()
    {
        Assert.Null(Choose(null, null, Found(
            @"C:\code\conductor\plans\karvansara\core.plan.json",
            @"C:\code\conductor\plans\karvansara\face.plan.json")));
    }

    /// <summary>A stale <c>CONDUCTOR_PLAN</c> naming a file that has since been deleted or renamed
    /// resolves to a path and then fails to load — which is the same "no plan resolves here" the empty
    /// directory has, and deserves the same answer rather than a stack trace about a missing file.</summary>
    [Fact]
    public void A_variable_pointing_at_a_file_that_is_gone_is_the_board()
    {
        Assert.Null(Choose(null, @"C:\gone\was-here.plan.json", Found(), exists: _ => false));
    }

    /// <summary>The other half of that rule: a path someone TYPED and got wrong is an error to be told
    /// about. Silently changing the subject to "here is the machine" would hide the typo.</summary>
    [Fact]
    public void An_explicit_dash_p_is_never_swallowed_by_the_board()
    {
        Assert.Equal(@"C:\typo\nope.plan.json",
            Choose(@"C:\typo\nope.plan.json", null, Found(), exists: _ => false));
    }

    [Fact]
    public void One_plan_here_still_resolves_to_that_plan()
    {
        Assert.Equal(@"C:\rig\rig.plan.json", Choose(null, null, Found(@"C:\rig\rig.plan.json")));
    }

    /// <summary>KS0.3's precedence, borrowed rather than re-stated: the directory beats an inherited
    /// variable when it is unambiguous, and the variable is the tie-breaker when it is not. Status
    /// having its own copy of this rule is how the two would come to disagree.</summary>
    [Fact]
    public void The_KS0_3_precedence_is_the_same_one_every_other_verb_uses()
    {
        const string env = @"C:\code\conductor\plans\karvansara\core.plan.json";
        const string rig = @"C:\temp\rig\rig.plan.json";

        // cwd wins when it is unambiguous (bug #20)...
        Assert.Equal(rig, Choose(null, env, Found(rig)));
        // ...and the variable breaks the tie when it is not.
        Assert.Equal(env, Choose(null, env, Found(
            @"C:\code\conductor\plans\a.plan.json", @"C:\code\conductor\plans\b.plan.json")));
        // ...and -p outranks both.
        Assert.Equal(@"C:\said\out\loud.plan.json", Choose(@"C:\said\out\loud.plan.json", env, Found(rig)));
    }

    /// <summary>Module intent, not style. The whole acceptance is that the ambiguity is answered before
    /// anything can prompt — <c>ResolvePlanPath</c> prompts when candidates &gt; 1 and output is a
    /// terminal, so a board branch that consulted the console could only ever be proved through a pipe,
    /// and the TTY case would keep the picker nobody asked for.</summary>
    [Fact]
    public void The_board_branch_cannot_consult_the_console_and_cannot_catch_the_throw()
    {
        var status = Source("StatusCommand.cs");
        var board = Source("StatusBoard.cs");

        foreach (var (name, code) in new[] { ("StatusCommand.cs", status), ("StatusBoard.cs", board) })
        {
            Assert.DoesNotContain("IsOutputRedirected", code, StringComparison.Ordinal);
            Assert.DoesNotContain("IsInputRedirected", code, StringComparison.Ordinal);
            Assert.DoesNotContain("AnsiConsole.Prompt", code, StringComparison.Ordinal);
            Assert.DoesNotContain("SelectionPrompt", code, StringComparison.Ordinal);
            // Caught, the refusal is still a thing that happened: the prompt would already have fired on
            // a terminal. The branch must be chosen instead.
            Assert.DoesNotContain("catch (InvalidOperationException", code, StringComparison.Ordinal);
            Assert.False(code.Contains("ResolvePlanPath", StringComparison.Ordinal) && name == "StatusBoard.cs",
                "the fallback must never be able to reach the resolver it exists to avoid");
        }

        // The probe is on the fallback's side of the branch only: `status` with a plan opens one
        // database and must not start paying for twenty sockets to do it.
        Assert.DoesNotContain("FleetScan", status, StringComparison.Ordinal);
        Assert.DoesNotContain("GatherAsync", status, StringComparison.Ordinal);
    }

    /// <summary>The probe's budget is bounded, and bounded by the fleet's own constant rather than a
    /// number typed here — on the no-plan branch that budget is the floor of this verb's latency.</summary>
    [Fact]
    public void The_probe_budget_is_the_fleets_own_bounded_default()
    {
        Assert.Equal(FleetScan.DefaultProbeTimeout, StatusBoard.ProbeTimeout);
        Assert.True(StatusBoard.ProbeTimeout > TimeSpan.Zero && StatusBoard.ProbeTimeout <= TimeSpan.FromSeconds(5));
    }

    // ── 2. the note says why, and says it on stderr ──────────────────────────────────────────────

    [Fact]
    public void The_note_says_why_a_reader_who_asked_about_a_plan_is_looking_at_a_machine()
    {
        Assert.Contains("no plan resolves here", StatusBoard.Why(0), StringComparison.Ordinal);
        Assert.Contains("conductor init", StatusBoard.Why(0), StringComparison.Ordinal);

        var many = StatusBoard.Why(11);
        Assert.Contains("11 plans here", many, StringComparison.Ordinal);
        Assert.Contains("-p", many, StringComparison.Ordinal);

        foreach (var n in new[] { 0, 1, 11 })
            Assert.DoesNotContain(TheRefusal, StatusBoard.Why(n), StringComparison.Ordinal);
    }

    // ── 3. the board is truthful ─────────────────────────────────────────────────────────────────

    /// <summary>Through the real catalogue path: a run whose row still says <c>running</c> for an engine
    /// nothing is holding must reach the board already reconciled (KS1.3). The board forms no second
    /// opinion — it reads the same rows the hub and the picker do.</summary>
    [Fact]
    public void A_run_whose_engine_is_dead_never_lists_as_running()
    {
        var repo = Path.Combine(_tmp, "killed");
        Directory.CreateDirectory(repo);
        SeedRun(repo, "core", "run-killed-ks25");

        var page = MachineBoard.Past(_root, []);
        var model = HubModel.Compose(_root, repo, [], page.Rows, [], DateTime.UtcNow, page.Total);

        Assert.Equal(RunLiveness.Orphaned, Assert.Single(model.PastRuns).Status);
        Assert.DoesNotContain("running", string.Join("\n", HubView.Board(model)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A page is not a machine. Beyond the screenful the catalogue keeps counting, and the
    /// board says which of the two it is doing — "that run is not on this machine" is exactly the wrong
    /// conclusion to draw from a list that simply stopped.</summary>
    [Fact]
    public void A_capped_listing_says_how_many_it_is_not_showing()
    {
        var rows = Enumerable.Range(0, FacePastRuns.DefaultMax)
            .Select(i => new FacePastRun("C:/code/blog", "blog", $"run-{i}", "completed", 1, 1, 1m,
                "2026-08-01T09:00:00Z", "C:/code/blog/run.db"))
            .ToArray();

        var capped = HubModel.Compose("C:/home", "C:/cwd", [], rows, [], DateTime.UtcNow, 23);
        Assert.True(capped.PastTruncated);
        Assert.Contains($"showing {FacePastRuns.DefaultMax} of 23", string.Join("\n", HubView.Board(capped)),
            StringComparison.Ordinal);

        // ...and when nothing was capped it must not invent a second page.
        var whole = HubModel.Compose("C:/home", "C:/cwd", [], rows, [], DateTime.UtcNow, rows.Length);
        Assert.False(whole.PastTruncated);
        Assert.DoesNotContain("showing", string.Join("\n", HubView.Board(whole)), StringComparison.Ordinal);
    }

    // ── 4. the four cases, through the real binary ───────────────────────────────────────────────

    /// <summary>Zero plans, redirected: the board and exit 0, where there used to be a throw through the
    /// crash handler.</summary>
    [Fact]
    public void Status_in_a_directory_with_no_plan_prints_the_machine_and_exits_zero()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_tmp, "empty")).FullName;

        var (exit, stdout, stderr) = RunCli(cwd, "status");

        Assert.Equal(0, exit);
        Assert.Contains("live runs", stdout, StringComparison.Ordinal);
        Assert.Contains("past runs", stdout, StringComparison.Ordinal);
        Assert.Contains("no plan resolves here", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("No plan found", stdout + stderr, StringComparison.Ordinal);
        // The old path threw, and a throw here wrote a forensic dump into whatever directory the
        // question was asked in.
        Assert.False(Directory.Exists(Path.Combine(cwd, ".conductor", "logs")),
            "asking status in a plan-less directory left a crash log behind");
    }

    /// <summary>The acceptance, in the words of the sentence it deletes. Two plan files, output
    /// redirected — the exact shape that produced the refusal.</summary>
    [Fact]
    public void Status_in_a_two_plan_directory_prints_the_machine_and_never_the_refusal()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_tmp, "two")).FullName;
        WritePlan(Path.Combine(cwd, "alpha.plan.json"), "alpha", cwd);
        WritePlan(Path.Combine(cwd, "beta.plan.json"), "beta", cwd);

        var (exit, stdout, stderr) = RunCli(cwd, "status");

        Assert.Equal(0, exit);
        Assert.DoesNotContain(TheRefusal, stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("2 plans here", stderr, StringComparison.Ordinal);
        // Listed, not chosen between.
        Assert.Contains("alpha", stdout, StringComparison.Ordinal);
        Assert.Contains("beta", stdout, StringComparison.Ordinal);
    }

    /// <summary>The other side of the change, and the one that must not move: a directory with one plan
    /// still gets the run's own verdict, not the machine.</summary>
    [Fact]
    public void Status_where_a_plan_does_resolve_is_the_same_verb_it_always_was()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_tmp, "one")).FullName;
        WritePlan(Path.Combine(cwd, "solo.plan.json"), "solo", cwd);

        var (exit, stdout, stderr) = RunCli(cwd, "status");

        Assert.Equal(0, exit);
        Assert.Contains("No run.db yet", stdout, StringComparison.Ordinal);
        // Not the machine: the plan answered, so no probe and no board.
        Assert.DoesNotContain("live runs", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("caravanserai", stdout, StringComparison.Ordinal);
    }

    /// <summary>And <c>-p</c> still reaches across a directory that would otherwise have shown the
    /// machine — the flag is the way to narrow the question, so it has to keep working from anywhere.</summary>
    [Fact]
    public void Status_with_dash_p_reports_that_plan_from_a_directory_with_no_plan_of_its_own()
    {
        var home = Directory.CreateDirectory(Path.Combine(_tmp, "elsewhere")).FullName;
        var planPath = Path.Combine(home, "far.plan.json");
        WritePlan(planPath, "far", home);
        var cwd = Directory.CreateDirectory(Path.Combine(_tmp, "nowhere")).FullName;

        var (exit, stdout, stderr) = RunCli(cwd, "status", "-p", planPath);

        Assert.Equal(0, exit);
        Assert.Contains("No run.db yet", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("live runs", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("no plan resolves here", stderr, StringComparison.Ordinal);
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A plan file that actually loads: <c>PlanConfig</c> validates that the tracker exists,
    /// so a fixture without one fails before the branch this suite is about.</summary>
    private static void WritePlan(string path, string name, string repo)
    {
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"), $"# {name}\n\n## Handoff\nlast: nothing yet.\n");
        File.WriteAllText(path, $$"""
        {
          "name": "{{name}}",
          "repo": {{System.Text.Json.JsonSerializer.Serialize(repo)}},
          "tracker": "TRACKER.md",
          "agent": { "command": "fake-agent", "args": ["run", "{prompt}"] },
          "stages": [ { "id": "S1", "title": "first", "sessions": 1 } ]
        }
        """);
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

    private static string Source(string file)
    {
        var path = Path.Combine(RepoRoot(), "src", "Conductor", "Commands", file);
        Assert.True(File.Exists(path), $"{file} is gone — the fallback moved without this test moving with it");
        return Regex.Replace(File.ReadAllText(path), @"///.*$|//.*$", "",
            RegexOptions.Multiline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate repo root (Conductor.slnx)");
    }

    /// <summary>Drives the real app assembly in a directory of this test's choosing, against a state
    /// home of this test's choosing — the operator's real catalogue is never opened and never touched.
    /// stdout and stderr come back apart, because which stream the note lands on is an acceptance.</summary>
    private (int Exit, string Stdout, string Stderr) RunCli(string cwd, params string[] args)
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
        // Trap 4: an inherited CONDUCTOR_PLAN beats the directory the child is standing in, and this
        // suite is about what a directory says.
        psi.Environment.Remove("CONDUCTOR_PLAN");

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(120_000), "the CLI did not exit within 120s");
        return (p.ExitCode, stdout, stderr);
    }
}
