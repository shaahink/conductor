using Conductor.Commands;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K7.2 / bug #32 — the board carried seven checkpoints (F0.1, F1.1, F1.2, F2.1, F3.1, R0.1, R0.2)
/// whose stages left the plan eras ago, and nothing on this machine could retire them: deleting the
/// stash file they came from changed nothing, and `conductor doctor` exited 1 on them every time.
/// <para>The loop: the tracker is GENERATED from the work graph and READ BACK as the declared-work
/// list. <c>TrackerGenerator</c> re-emitted stages that are in the db but not in the plan as
/// checkpoint TABLE ROWS, and <see cref="WorkGraphSync"/> will not retire anything the declared
/// source still declares (<c>WorkGraphSync.cs:84</c>) — so the generated file kept re-declaring them
/// to the reader that fed the generator. Self-feeding; the out-of-plan branch at
/// <c>WorkGraphSync.cs:85</c> was unreachable.</para>
/// <para>These tests pin it shut from the generator end, which is where the false declaration was
/// manufactured: out-of-plan work is LISTED (a bullet cannot match the row regex, which anchors on
/// '|'), never re-declared. Then W1.2's stated contract — "their stage left the plan" — can finally
/// fire.</para>
/// </summary>
public sealed class K7OrphanBoardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-k7orphan-{Guid.NewGuid():N}");
    private readonly SqliteRunStore _db;

    public K7OrphanBoardTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new SqliteRunStore(Path.Combine(_dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _db.InitializeRun("r1", "p", _dir, "b", Conductor.Core.EngineStamp.Parse("v"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { }
    }

    /// <summary>The state this repo was actually in: the plan has K7, the tracker declares K7.1 AND a
    /// row for F0.1 whose stage F0 no plan stage matches, and the graph carries both.</summary>
    private PlanConfig PlanWithOrphanDeclaredInTheTracker()
    {
        // The handoff block runs to the next '## ' heading and its lines are parsed for rows like any
        // other, so the checkpoint table gets its own heading — otherwise the rows below land inside
        // the handoff and are carried into the regenerated file verbatim.
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"),
            "# Plan\n\n## Handoff\nnone.\n\n## Checkpoints\n\n"
            + "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + "| K7.1 | Ship the docs | DONE | abc1234 | e.md |\n"
            + "| F0.1 | Test scroll behavior and exit mechanism | TODO | - | - |\n");

        return new PlanConfig
        {
            Name = "orphan-test",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Stages = [new StageConfig { Id = "K7", Title = "Ship the plan" }],
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
        };
    }

    private static List<string> DeclaredIds(PlanConfig plan) =>
        [.. ProgressProviderFactory.Create(plan).Read(plan).Checkpoints.Select(c => c.Id)];

    [Fact]
    public void Regenerated_tracker_lists_out_of_plan_work_without_re_declaring_it()
    {
        var plan = PlanWithOrphanDeclaredInTheTracker();

        var first = WorkGraphSync.Sync(plan, _db, "r1");
        Assert.Equal(2, first.Added);          // both rows land in the graph, orphan included
        Assert.Equal(0, first.Archived);       // still declared by the tracker it was read from

        var regenerated = File.ReadAllText(Path.Combine(_dir, "TRACKER.md"));

        // Visibility is kept — losing sight of imported work was the point of the old section.
        Assert.Contains("F0.1", regenerated);
        Assert.Contains("Not in the plan", regenerated);

        // ...but it is no longer a DECLARATION. This is the whole fix: the reader that feeds the
        // sync must not see the generator's own output as declared work.
        Assert.DoesNotContain("F0.1", DeclaredIds(plan));
        Assert.Contains("K7.1", DeclaredIds(plan));
    }

    [Fact]
    public void Orphan_whose_stage_left_the_plan_is_retired_on_the_next_sync_and_doctor_goes_green()
    {
        var plan = PlanWithOrphanDeclaredInTheTracker();

        // Before: doctor fails exactly as it did on this repo — G13, the orphan named.
        var before = DoctorCommand.CheckWorkCoverage(plan);
        Assert.Equal("fail", before.State);
        Assert.Contains("F0.1", before.Message);

        WorkGraphSync.Sync(plan, _db, "r1");            // seeds the graph, regenerates the view
        var second = WorkGraphSync.Sync(plan, _db, "r1"); // declaration gone -> stage left the plan

        Assert.Equal(1, second.Archived);
        Assert.Equal(0, second.Revived);
        Assert.DoesNotContain("F0.1", _db.GetCheckpoints("r1").Select(c => c.Id));

        // After: the view no longer mentions it at all, and doctor exits ok.
        Assert.DoesNotContain("F0.1", File.ReadAllText(Path.Combine(_dir, "TRACKER.md")));
        Assert.Equal("ok", DoctorCommand.CheckWorkCoverage(plan).State);

        // And it stays retired: a third sync is a no-op, not an archive/revive oscillation.
        var third = WorkGraphSync.Sync(plan, _db, "r1");
        Assert.False(third.Changed);
        Assert.Equal(0, third.Revived);
    }
}
