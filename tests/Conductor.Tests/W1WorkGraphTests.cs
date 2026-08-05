using Conductor.Core.Events;
using Conductor.Core.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// W1.1 truth gates: checkpoints and tasks are ONE event-sourced graph. The mutable checkpoints
/// table is gone — GetCheckpoints folds the event log — so replaying the log must reproduce
/// checkpoint state byte-for-byte, kind/provenance must materialize, CheckpointConfirmed must
/// fold, and a second writer process (the CLI claim path) must never collide on seq.
/// </summary>
public sealed class W1WorkGraphTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRunStore _db;

    public W1WorkGraphTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"conductor-w1-test-{Guid.NewGuid():N}.db");
        _db = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static readonly (string, string, string, string, string, string)[] Seed =
    [
        ("W1.1", "W1", "unify the graph", "TODO", "-", "-"),
        ("W1.2", "W1", "sync every boundary", "IN PROGRESS", "-", "-"),
        ("W2.1", "W2", "claude-shaped MCP", "DONE", "abc1234", "wire test green"),
        ("W2.2", "W2", "live board", "BLOCKED", "-", "-"),
    ];

    [Fact]
    public void Replaying_the_log_reproduces_checkpoint_state_byte_for_byte()
    {
        _db.InitializeRun("r1", "p", "/r", "b", Conductor.Core.EngineStamp.Parse("v"));
        _db.SeedCheckpoints("r1", Seed);
        _db.UpdateCheckpoint("r1", "W1.1", "DONE", "def5678", "918 tests green", source: "agent");
        _db.MarkCheckpointInProgress("r1", "W1.2"); // no-op: already in progress
        _db.ConfirmCheckpoints("r1", ["W1.1"], sessionNumber: 7);

        var live = _db.GetCheckpoints("r1");

        // A fresh store on the same file has NO in-memory state — everything it reports is a
        // replay of the persisted log. Identical output IS the truth gate.
        using var reopened = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance);
        var replayed = reopened.GetCheckpoints("r1");

        Assert.Equal(live, replayed); // record equality — every column of every row
        Assert.Equal(4, replayed.Count);

        var done = replayed.Single(c => c.Id == "W1.1");
        Assert.Equal("DONE", done.Status);
        Assert.Equal("def5678", done.Commit);
        Assert.Equal("918 tests green", done.Evidence);
        Assert.True(done.Confirmed);

        Assert.Equal("IN PROGRESS", replayed.Single(c => c.Id == "W1.2").Status);
        Assert.Equal("BLOCKED", replayed.Single(c => c.Id == "W2.2").Status);
    }

    [Fact]
    public void Seeded_checkpoints_carry_kind_and_provenance_in_the_graph()
    {
        _db.InitializeRun("r1", "p", "/r", "b", Conductor.Core.EngineStamp.Parse("v"));
        _db.SeedCheckpoints("r1", Seed);

        var graph = new TaskGraph();
        graph.Fold(_db.ReadAllEvents("r1"));

        var item = graph.Find("W1.1");
        Assert.NotNull(item);
        Assert.Equal(WorkItemKinds.Checkpoint, item!.Kind);
        Assert.Equal("tracker", item.Source);
        Assert.Equal("W1", item.StageId);

        // The done seed carried its claim attribution into the graph.
        var claimed = graph.Find("W2.1")!;
        Assert.Equal("done", claimed.Status);
        Assert.Equal("abc1234", claimed.Commit);
        Assert.Equal("wire test green", claimed.Evidence);
    }

    [Fact]
    public void Legacy_events_without_kind_infer_checkpoint_from_id_equality()
    {
        var graph = new TaskGraph();
        graph.Fold(
        [
            new TaskAdded { Seq = 1, TaskId = "F1.1", CheckpointId = "F1.1", Title = "cp", Source = "tracker" },
            new TaskAdded { Seq = 2, TaskId = "F1.1-a1", CheckpointId = "F1.1", Title = "sub", Source = "agent" },
        ]);

        Assert.Equal(WorkItemKinds.Checkpoint, graph.Find("F1.1")!.Kind);
        Assert.Equal("F1", graph.Find("F1.1")!.StageId); // split-on-first-dot fallback
        Assert.Equal(WorkItemKinds.Subtask, graph.Find("F1.1-a1")!.Kind);
    }

    [Fact]
    public void CheckpointConfirmed_folds_into_the_graph_and_survives_replay()
    {
        _db.InitializeRun("r1", "p", "/r", "b", Conductor.Core.EngineStamp.Parse("v"));
        _db.SeedCheckpoints("r1", [("W1.1", "W1", "unify", "DONE", "abc", "green")]);
        _db.ConfirmCheckpoints("r1", ["W1.1"]);

        var row = _db.GetCheckpoints("r1").Single();
        Assert.True(row.Confirmed);

        var confirmedEvt = _db.ReadAllEvents("r1").OfType<CheckpointConfirmed>().Single();
        Assert.Equal("W1.1", confirmedEvt.CheckpointId);
        Assert.Equal("W1", confirmedEvt.StageId);
    }

    [Fact]
    public void Repeated_done_claims_refresh_commit_and_evidence()
    {
        _db.InitializeRun("r1", "p", "/r", "b", Conductor.Core.EngineStamp.Parse("v"));
        _db.SeedCheckpoints("r1", [("W1.1", "W1", "unify", "TODO", "-", "-")]);
        _db.UpdateCheckpoint("r1", "W1.1", "DONE", "aaa1111", "first claim", source: "agent");
        _db.UpdateCheckpoint("r1", "W1.1", "DONE", "bbb2222", "amended evidence", source: "agent");

        var row = _db.GetCheckpoints("r1").Single();
        Assert.Equal("DONE", row.Status);
        Assert.Equal("bbb2222", row.Commit);
        Assert.Equal("amended evidence", row.Evidence);
    }

    [Fact]
    public void Reseed_never_clobbers_runtime_status_but_refreshes_declared_titles()
    {
        _db.InitializeRun("r1", "p", "/r", "b", Conductor.Core.EngineStamp.Parse("v"));
        _db.SeedCheckpoints("r1", [("W1.1", "W1", "old title", "TODO", "-", "-")]);
        _db.UpdateCheckpoint("r1", "W1.1", "DONE", "abc", "green", source: "engine");

        // A restart re-seeds from the tracker view — declared title updates, runtime status stays.
        _db.SeedCheckpoints("r1", [("W1.1", "W1", "new title", "TODO", "-", "-")]);

        var row = _db.GetCheckpoints("r1").Single();
        Assert.Equal("DONE", row.Status);
        Assert.Equal("new title", row.Title);
    }

    [Fact]
    public void A_second_writer_on_the_same_db_never_collides_on_seq()
    {
        _db.InitializeRun("r1", "p", "/r", "b", Conductor.Core.EngineStamp.Parse("v"));
        _db.SeedCheckpoints("r1", Seed);

        // The CLI claim path is a separate process with its own store instance and its own
        // in-memory counter — exactly the two-writer shape that used to be impossible to record.
        using (var cli = new SqliteRunStore(_dbPath, NullLogger<SqliteRunStore>.Instance))
        {
            cli.UpdateCheckpoint("r1", "W1.1", "DONE", "cli1234", "claimed via CLI", source: "agent");
        }

        // The engine's instance keeps writing after the CLI advanced the log underneath it.
        _db.UpdateCheckpoint("r1", "W1.2", "DONE", "eng5678", "engine verdict", source: "engine");

        var events = _db.ReadAllEvents("r1");
        var seqs = events.Select(e => e.Seq).ToList();
        Assert.Equal(seqs.Count, seqs.Distinct().Count()); // no collisions, nothing dropped

        var rows = _db.GetCheckpoints("r1");
        Assert.Equal("DONE", rows.Single(c => c.Id == "W1.1").Status);
        Assert.Equal("cli1234", rows.Single(c => c.Id == "W1.1").Commit);
        Assert.Equal("DONE", rows.Single(c => c.Id == "W1.2").Status);
    }

    [Fact]
    public void Subtask_adds_carry_kind_and_inherit_the_parent_stage()
    {
        _db.InitializeRun("r1", "p", "/r", "b", Conductor.Core.EngineStamp.Parse("v"));
        _db.SeedCheckpoints("r1", Seed);

        var graph = new TaskGraph();
        graph.Fold(_db.ReadAllEvents("r1"));
        var (evt, error) = TaskWrites.BuildAdd(graph, "r1", "W1.1", "a sub-task", 0, source: "human");

        Assert.Null(error);
        Assert.Equal(WorkItemKinds.Subtask, evt!.Kind);
        Assert.Equal("W1", evt.StageId);

        // Subtasks never surface in the checkpoint view.
        graph.Fold([evt]);
        Assert.DoesNotContain(graph.Checkpoints(), t => t.TaskId == evt.TaskId);
    }
}
