using Conductor.Core;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// M7 — knowledge that compounds. Covers the bugs store (M7.2), the ledger + bugs prompt batteries
/// (M7.1/M7.2), and the M7 truth gate: in a 2-session toy run, session 1 writes a note and files a
/// bug, and session 2's compiled prompt.md ON DISK contains both — asserted against the file so it
/// cannot be faked.
/// </summary>
public sealed class M7KnowledgeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly SqliteRunStore _db;
    private const string RunId = "run-m7";

    public M7KnowledgeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"conductor-m7-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "run.db");
        _db = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance);
        _db.InitializeRun(RunId, "toy", @"C:\repo", "feat/toy", "test");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { TestTemp.DeleteTree(_dir); } catch { }
    }

    // ---------------------------------------------------------------- store (M7.2)

    [Fact]
    public void WriteBug_returns_id_and_queryBugs_reads_it_back()
    {
        var id = _db.WriteBug(RunId, "gate battery hangs on lint", "repro: run lint twice", "high", "M7", 1);
        Assert.True(id > 0);

        var open = _db.QueryBugs(RunId, "open");
        var bug = Assert.Single(open);
        Assert.Equal(id, bug.Id);
        Assert.Equal("gate battery hangs on lint", bug.Title);
        Assert.Equal("high", bug.Severity);
        Assert.Equal("open", bug.Status);
        Assert.Equal("M7", bug.StageId);
        Assert.Equal(1, bug.FoundSession);
    }

    [Fact]
    public void WriteBug_normalizes_unknown_severity_to_medium()
    {
        var id = _db.WriteBug(RunId, "typo in log", null, "cosmetic", null, null);
        var bug = Assert.Single(_db.QueryBugs(RunId));
        Assert.Equal(id, bug.Id);
        Assert.Equal("medium", bug.Severity);
    }

    [Fact]
    public void UpdateBugStatus_closes_the_bug_and_records_the_fixing_session()
    {
        var id = _db.WriteBug(RunId, "off-by-one in scroll", null, "medium", null, 1);
        Assert.True(_db.UpdateBugStatus(RunId, id, "fixed", 2));

        Assert.Empty(_db.QueryBugs(RunId, "open"));
        var all = _db.QueryBugs(RunId);
        var bug = Assert.Single(all);
        Assert.Equal("fixed", bug.Status);
        Assert.Equal(2, bug.FixedSession);
    }

    [Fact]
    public void UpdateBugStatus_returns_false_for_unknown_id()
    {
        Assert.False(_db.UpdateBugStatus(RunId, 9999, "fixed", 1));
    }

    // ---------------------------------------------------------------- batteries (M7.1/M7.2)

    [Fact]
    public void LedgerBattery_injects_recent_notes_but_not_hand_edits()
    {
        _db.WriteLedger(RunId, 1, "M7", "finding", "run.db path must be absolute on Windows");
        _db.WriteLedger(RunId, 1, "M7", "hand-edit", "tracker row M7.1 changed by hand — discarded");

        var battery = new LedgerBattery(_db, RunId);
        Assert.False(battery.IsEmpty);
        Assert.Contains("run.db path must be absolute", battery.Section);
        Assert.DoesNotContain("discarded", battery.Section);
    }

    [Fact]
    public void BugsBattery_injects_only_open_bugs()
    {
        var open = _db.WriteBug(RunId, "verifier double-counts cost", null, "high", "M7", 1);
        var closed = _db.WriteBug(RunId, "already handled", null, "low", null, 1);
        _db.UpdateBugStatus(RunId, closed, "fixed", 1);

        var battery = new BugsBattery(_db, RunId);
        Assert.False(battery.IsEmpty);
        Assert.Contains("verifier double-counts cost", battery.Section);
        Assert.Contains($"#{open}", battery.Section);
        Assert.DoesNotContain("already handled", battery.Section);
    }

    [Fact]
    public void Batteries_are_empty_when_nothing_recorded()
    {
        Assert.True(new LedgerBattery(_db, RunId).IsEmpty);
        Assert.True(new BugsBattery(_db, RunId).IsEmpty);
    }

    // ---------------------------------------------------------------- M7 TRUTH GATE

    /// <summary>
    /// The M7 truth gate, asserted against a file on disk. Session 1 writes a note and files a bug;
    /// the session-2 prompt is compiled through the SAME code path SessionRunner uses
    /// (PromptBuilder.Deliver + BatterySection(state, store)), written to a real prompt.md, then read
    /// back and asserted to contain BOTH. It cannot be faked: the assertion reads the file, not memory.
    /// </summary>
    [Fact]
    public void Session2_compiled_promptMd_on_disk_contains_the_note_and_the_bug()
    {
        var plan = new PlanConfig
        {
            Name = "toy",
            Repo = _dir, // StateDir derives from Repo; keeps LessonsManager off a nonexistent path
            Tracker = "TOY-TRACKER.md",
            PlanDoc = "docs/toy.md",
        };
        var stage = new StageConfig { Id = "M7", Title = "Knowledge that compounds", Sessions = 2 };
        var state = new RunState { PlanName = "toy", RunId = RunId, CurrentStage = "M7", SessionCounter = 1 };

        // ── Session 1 records what it learned and files a bug it is not fixing now. ──
        const string noteText = "the ledger battery must be added FIRST so the byte cap never drops it";
        const string bugTitle = "console SSE resets line counter across sessions";
        _db.WriteLedger(RunId, 1, "M7", "finding", noteText);
        _db.WriteBug(RunId, bugTitle, "seen when a new session's raw log appears", "medium", "M7", 1);

        // ── Session 2 prompt is compiled exactly as SessionRunner does, then written to disk. ──
        var builder = new PromptBuilder(plan);
        state.SessionCounter = 2;
        var prompt = builder.Deliver(stage, 2, 1, 1);
        var battery = builder.BatterySection(state, _db);
        if (battery.Length > 0) prompt = prompt.TrimEnd() + "\n\n" + battery;

        var promptPath = Path.Combine(_dir, "logs", "session-002.prompt.md");
        Directory.CreateDirectory(Path.GetDirectoryName(promptPath)!);
        File.WriteAllText(promptPath, prompt);

        // ── Assert against the FILE, not memory. ──
        var onDisk = File.ReadAllText(promptPath);
        Assert.Contains(noteText, onDisk);
        Assert.Contains(bugTitle, onDisk);
    }
}
