using Conductor.Core;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SF0.4 — an open bug outlives the RUN that found it, not just the session.
///
/// <para>M7.2 gave the project a bug ledger and promised a row that "outlives the session that found
/// it". It did — but every read was scoped <c>WHERE run_id = @runId</c>, so the ledger reset to empty
/// the moment a new plan started a run in the same repo. Measured 2026-07-31: the Sarban core run
/// finished with eleven open bugs, the face plan started, and <c>conductor bug list</c> answered with
/// one row. No error and no warning — an empty ledger reads as a clean one, which is why this went
/// unnoticed until the bugs were being transcribed into a markdown file by hand.</para>
///
/// <para>The gate at the bottom is the one that matters: a session in a NEW run compiles its prompt
/// through the same path <c>SessionRunner</c> uses, and the file on disk contains a bug the PREVIOUS
/// run filed. Showing the row in a CLI table is not enough — the ledger exists to reach the agent.</para>
/// </summary>
public sealed class SF04BugsOutliveTheirRunTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteRunStore _db;
    private const string OldRun = "run-core";
    private const string NewRun = "run-face";
    private const string OldPlan = "Sarban core - the engine says what it knows";
    private const string NewPlan = "Sarban face - the watcher and the surfaces";

    public SF04BugsOutliveTheirRunTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"conductor-sf04-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _db = new SqliteRunStore(Path.Combine(_dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        // One repo, one run.db, two runs — the exact shape that lost eleven bugs.
        _db.InitializeRun(OldRun, OldPlan, _dir, "feat/sarban", Conductor.Core.EngineStamp.Parse("test"));
        _db.InitializeRun(NewRun, NewPlan, _dir, "feat/sarban", Conductor.Core.EngineStamp.Parse("test"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { TestTemp.DeleteTree(_dir); } catch { }
    }

    // ---------------------------------------------------------------- the store

    [Fact]
    public void QueryCarriedBugs_returns_the_previous_runs_open_bugs_with_the_plan_that_filed_them()
    {
        var carriedId = _db.WriteBug(OldRun, "bg status crashes on an uninspectable pid", "Win32 access denied", "high", "SC5", 12);
        _db.WriteBug(NewRun, "this run's own bug", null, "low", "SF0", 1);

        // The old run's row is invisible to a per-run read — that IS the defect, pinned here so a
        // future "simplification" back to one query cannot quietly restore it.
        Assert.DoesNotContain(_db.QueryBugs(NewRun, "open"), b => b.Id == carriedId);

        var carried = Assert.Single(_db.QueryCarriedBugs(NewRun));
        Assert.Equal(carriedId, carried.Bug.Id);
        Assert.Equal("bg status crashes on an uninspectable pid", carried.Bug.Title);
        Assert.Equal("high", carried.Bug.Severity);
        Assert.Equal(OldPlan, carried.PlanName);
    }

    [Fact]
    public void QueryCarriedBugs_carries_only_what_is_still_open()
    {
        var closed = _db.WriteBug(OldRun, "already fixed in flight", null, "medium", "SC3", 5);
        var open = _db.WriteBug(OldRun, "still outstanding", null, "medium", "SC4", 6);
        _db.UpdateBugStatus(OldRun, closed, "fixed", 5);

        var carried = Assert.Single(_db.QueryCarriedBugs(NewRun));
        Assert.Equal(open, carried.Bug.Id);
    }

    [Fact]
    public void QueryCarriedBugs_never_carries_the_current_runs_own_rows()
    {
        _db.WriteBug(NewRun, "mine", null, "medium", "SF0", 1);
        Assert.Empty(_db.QueryCarriedBugs(NewRun));
    }

    /// <summary>Showing a row no command can close would be a worse ledger than hiding it: the operator
    /// sees outstanding work and has no way to mark it done, so the list only ever grows.</summary>
    [Fact]
    public void A_carried_bug_can_be_closed_by_the_run_that_actually_fixes_it()
    {
        var id = _db.WriteBug(OldRun, "verifyEachDelivery is read by nothing", null, "medium", "SC4", 20);

        Assert.True(_db.UpdateBugStatus(NewRun, id, "fixed", fixedSession: 3));

        Assert.Empty(_db.QueryCarriedBugs(NewRun));
        var row = Assert.Single(_db.QueryBugs(OldRun, "fixed"));
        Assert.Equal(id, row.Id);
        // Session 3 belongs to the FACE run; stamping it here would point at the wrong run's history.
        Assert.Null(row.FixedSession);
    }

    [Fact]
    public void Closing_a_bug_the_current_run_filed_still_records_its_session()
    {
        var id = _db.WriteBug(NewRun, "prompt over 8191 chars silently drops the agent", null, "medium", "SF0", 3);
        Assert.True(_db.UpdateBugStatus(NewRun, id, "fixed", fixedSession: 4));
        Assert.Equal(4, Assert.Single(_db.QueryBugs(NewRun, "fixed")).FixedSession);
    }

    [Fact]
    public void UpdateBugStatus_still_returns_false_for_an_id_that_does_not_exist()
    {
        Assert.False(_db.UpdateBugStatus(NewRun, 9999, "fixed", 1));
    }

    // ---------------------------------------------------------------- what a finished run says

    [Fact]
    public void The_run_end_line_counts_both_ledgers_and_says_where_to_read_them()
    {
        _db.WriteBug(OldRun, "carried one", null, "medium", "SC1", 1);
        _db.WriteBug(OldRun, "carried two", null, "medium", "SC2", 2);
        _db.WriteBug(NewRun, "filed here", null, "medium", "SF0", 1);

        var counts = OpenBugsReport.Count(_db, NewRun);
        Assert.Equal(1, counts.ThisRun);
        Assert.Equal(2, counts.Carried);

        var line = OpenBugsReport.EpilogueLine(counts, "plans/face.plan.json");
        Assert.NotNull(line);
        Assert.Contains("3 open bug(s)", line, StringComparison.Ordinal);
        Assert.Contains("2 carried from an earlier run", line, StringComparison.Ordinal);
        // A count with no way to read the rows is a number nobody can act on.
        Assert.Contains("conductor bug list -p plans/face.plan.json", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_ends_with_nothing_outstanding_prints_no_bug_line()
    {
        Assert.Null(OpenBugsReport.EpilogueLine(OpenBugsReport.Count(_db, NewRun), "plans/face.plan.json"));
    }

    [Fact]
    public void RunSummary_carries_the_open_bug_ledger_including_the_carried_rows()
    {
        _db.WriteBug(OldRun, "bg logs cannot read a live log", null, "medium", "SC7", 25);
        _db.WriteBug(NewRun, "an 8191-char prompt | with a pipe in it", null, "high", "SF0", 1);
        _db.RecordRunEnd(NewRun, "Completed");

        var plan = new PlanConfig { Name = NewPlan, Repo = _dir };
        var state = new RunState { PlanName = NewPlan, RunId = NewRun, Status = RunStatus.Completed, SessionCounter = 4 };

        var md = RunSummary.Build(plan, state, new TrackerSnapshot(), _db, DateTime.UtcNow);

        Assert.Contains("## Open bugs at run end", md, StringComparison.Ordinal);
        Assert.Contains("bg logs cannot read a live log", md, StringComparison.Ordinal);
        Assert.Contains(OldPlan, md, StringComparison.Ordinal);
        // A pipe in an agent-typed title would silently break the markdown table.
        Assert.Contains(@"an 8191-char prompt \| with a pipe in it", md, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSummary_says_so_plainly_when_the_ledger_is_clean()
    {
        var plan = new PlanConfig { Name = NewPlan, Repo = _dir };
        var state = new RunState { PlanName = NewPlan, RunId = NewRun, Status = RunStatus.Completed };
        var md = RunSummary.Build(plan, state, new TrackerSnapshot(), _db, DateTime.UtcNow);
        Assert.Contains("None — every tracked bug filed in this repo is closed.", md, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- SF0.4 TRUTH GATE

    /// <summary>
    /// The gate. A session of the FACE run compiles its prompt through the same path
    /// <c>SessionRunner</c> uses (<c>PromptBuilder.Deliver</c> + <c>BatterySection(state, store)</c>),
    /// writes it to a real prompt.md, and the file on disk contains a bug the CORE run filed. Asserted
    /// against the file, not memory, so it cannot be faked — and it fails if the ledger is ever
    /// re-scoped to one run, because then the prompt would be the first place the row disappears from.
    /// </summary>
    [Fact]
    public void A_new_runs_prompt_on_disk_contains_a_bug_the_previous_run_filed()
    {
        const string carriedTitle = "McpTaskServer reports an uninspectable pid as DEAD";
        _db.WriteBug(OldRun, carriedTitle, "inverts the PidLiveness policy SC4.1 set", "medium", "SC4", 40);

        var plan = new PlanConfig { Name = NewPlan, Repo = _dir, Tracker = "TOY-TRACKER.md", PlanDoc = "docs/toy.md" };
        var stage = new StageConfig { Id = "SF0", Title = "The ledger closes", Sessions = 2 };
        var state = new RunState { PlanName = NewPlan, RunId = NewRun, CurrentStage = "SF0", SessionCounter = 1 };

        var builder = new PromptBuilder(plan);
        var prompt = builder.Deliver(stage, 1, 1, 1);
        var battery = builder.BatterySection(state, _db);
        if (battery.Length > 0) prompt = prompt.TrimEnd() + "\n\n" + battery;

        var promptPath = Path.Combine(_dir, "logs", "session-001.prompt.md");
        Directory.CreateDirectory(Path.GetDirectoryName(promptPath)!);
        File.WriteAllText(promptPath, prompt);

        var onDisk = File.ReadAllText(promptPath);
        Assert.Contains(carriedTitle, onDisk, StringComparison.Ordinal);
        // And the agent is told it is not this run's own row, so "do NOT re-file" still makes sense.
        Assert.Contains("carried from an earlier run", onDisk, StringComparison.Ordinal);
    }

    /// <summary>This run's own bugs keep the battery's slots; carried rows fill only what is left, so
    /// carrying a ledger forward cannot push the composed prompt past what one run's ledger already
    /// could. (Bug #15: past ~8191 chars a cmd.exe-based agent silently never runs.)</summary>
    [Fact]
    public void Carried_rows_share_the_batterys_entry_cap_rather_than_extending_it()
    {
        for (var i = 0; i < 6; i++) _db.WriteBug(NewRun, $"mine {i}", null, "medium", "SF0", 1);
        for (var i = 0; i < 6; i++) _db.WriteBug(OldRun, $"leftover {i}", null, "medium", "SC1", 1);

        var section = new BugsBattery(_db, NewRun, maxEntries: 8).Section;

        Assert.Equal(6, CountOccurrences(section, "mine "));
        Assert.Equal(2, CountOccurrences(section, "leftover "));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
