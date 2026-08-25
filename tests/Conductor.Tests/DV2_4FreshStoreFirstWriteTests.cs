using Conductor.Core.Store;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV2.4, bug #71 (recovered as karvan #27) — a brand-new <c>run.db</c> logged
/// <c>FOREIGN KEY constraint failed</c> on the first <c>run_state</c> write and LOST that write.
/// Measured live on a rig that had never run: one FK line straight after "started paused".
///
/// <para><c>run_state</c> declares <c>FOREIGN KEY (run_id) REFERENCES runs(run_id)</c>
/// (Migrations/v5_events_and_state.sql:19) and the run loop saved state before it initialised the
/// run. KS0.3 closed it by ensuring the row at the FUNNEL — <c>RunContext.Save()</c> calls
/// <c>EnsureRunRow()</c> before <c>SaveRunState</c> — rather than by reordering the one call that
/// happened to be second.</para>
///
/// <para>Two tests, because either alone is vacuous. The first proves the hazard is REAL on this
/// store (the constraint is enforced, the error is swallowed, the write is gone). The second proves
/// the funnel is what closes it, and would fail if a later era moved the ensure below the save.</para>
/// </summary>
public sealed class DV2_4FreshStoreFirstWriteTests : IDisposable
{
    private readonly string _tmp;
    private readonly List<IDisposable> _open = [];

    public DV2_4FreshStoreFirstWriteTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-dv24-fk-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        foreach (var d in _open) { try { d.Dispose(); } catch (ObjectDisposedException) { } }
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private SqliteRunStore NewStore(string name)
    {
        var store = new SqliteRunStore(Path.Combine(_tmp, name), NullLogger<SqliteRunStore>.Instance);
        _open.Add(store);
        return store;
    }

    /// <summary>The mechanism, measured rather than asserted from the schema: with no <c>runs</c> row
    /// the state write is refused, the refusal does not throw, and the state is simply not there.
    /// This is what every fresh run used to do to its own first save.</summary>
    [Fact]
    public void WithNoRunRow_TheFirstRunStateWriteIsRejectedAndLost()
    {
        var store = NewStore("no-run-row.db");

        store.SaveRunState("run-orphan", "plan", "{\"RunId\":\"run-orphan\"}");

        Assert.Null(store.LoadRunStateJson("run-orphan"));
    }

    /// <summary>And with the row ensured first — what <c>RunContext.Save()</c> now does on every one
    /// of its callers' behalf — the same write survives on the same brand-new database.</summary>
    [Fact]
    public void WithTheRunRowEnsuredFirst_TheFirstRunStateWriteSurvives()
    {
        var store = NewStore("with-run-row.db");

        store.InitializeRun("run-ok", "plan", _tmp, "main", Conductor.Core.EngineStamp.Current, "{}");
        store.SaveRunState("run-ok", "plan", "{\"RunId\":\"run-ok\"}");

        Assert.Equal("{\"RunId\":\"run-ok\"}", store.LoadRunStateJson("run-ok"));
    }

    /// <summary>The fix is an ORDERING, and an ordering is what rotted the first time. KS0.3 put the
    /// ensure at the funnel precisely so a future early save could not reintroduce the defect; this
    /// pins that the funnel is still in that order.</summary>
    [Fact]
    public void RunContextSave_EnsuresTheRunRowBeforeItWritesTheState()
    {
        var src = File.ReadAllText(SourceFile("src/Conductor.Core/Orchestration/RunContext.cs"));
        var save = src[src.IndexOf("public void Save()", StringComparison.Ordinal)..];
        save = save[..save.IndexOf("\n    }", StringComparison.Ordinal)];

        var ensure = save.IndexOf("EnsureRunRow()", StringComparison.Ordinal);
        var write = save.IndexOf("SaveRunState(", StringComparison.Ordinal);

        Assert.True(ensure >= 0, "RunContext.Save() no longer ensures the run row — bug #71 is reopened");
        Assert.True(write >= 0, "RunContext.Save() no longer writes run_state — this test needs rewriting");
        Assert.True(ensure < write,
            "RunContext.Save() writes run_state BEFORE ensuring the runs row it references — " +
            "on a fresh database that write is refused by the foreign key and silently lost (bug #71)");
    }

    /// <summary>Repo root from the test binary, the same walk the other source-rule tests use.</summary>
    private static string SourceFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Conductor.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"source file not found: {path}");
        return path;
    }
}
