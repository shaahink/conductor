using Conductor.Core.Store;

using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS0.3, bug #27 — what the foreign key on <c>run_state</c> actually costs when the run row is late.
///
/// <para>Every brand-new run.db opened with <c>SQLite Error 19: FOREIGN KEY constraint failed</c> in
/// its log. The interesting part is not the log line: <c>TryExecute</c> swallows the failure, so the
/// first state write was simply LOST, and nobody noticed because the second save (after the run row
/// existed) put it back. On a run that died between the two, it did not come back.</para>
///
/// <para>These tests characterise the constraint rather than the ordering — the ordering fix lives at
/// the funnel (<c>RunContext.EnsureRunRow</c>, called from every <c>Save</c>), and the red-to-green
/// proof is a live rig: <c>tools/ks0/ks0-3-bug27-fresh-db-fk.ps1</c>.</para>
/// </summary>
public sealed class KS0_3FreshStoreFkTests : IDisposable
{
    private readonly string _tmp;

    public KS0_3FreshStoreFkTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks03-fk-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
    }

    private SqliteRunStore FreshStore()
        => new(Path.Combine(_tmp, Guid.NewGuid().ToString("N")[..8], "run.db"),
               NullLogger<SqliteRunStore>.Instance);

    [Fact]
    public void StateWrittenBeforeTheRunRowIsSilentlyLost()
    {
        using var store = FreshStore();

        store.SaveRunState("run-1", "a plan", """{"Status":"Paused"}""");

        // Not an exception, not a return value anyone checks — just gone.
        Assert.Null(store.LoadRunStateJson("run-1"));
    }

    [Fact]
    public void WithTheRunRowFirst_TheVeryFirstStateWriteSurvives()
    {
        using var store = FreshStore();

        store.InitializeRun("run-1", "a plan", @"C:\repo", "main", default);
        store.SaveRunState("run-1", "a plan", """{"Status":"Paused"}""");

        Assert.Equal("""{"Status":"Paused"}""", store.LoadRunStateJson("run-1"));
    }

    [Fact]
    public void TheRunRowIsAnUpsert_SoEnsuringItTwiceIsHarmless()
    {
        using var store = FreshStore();

        store.InitializeRun("run-1", "a plan", @"C:\repo", "main", default);
        var started = store.Query("SELECT started_utc FROM runs WHERE run_id = 'run-1'")[0]["started_utc"];
        store.InitializeRun("run-1", "a plan", @"C:\repo", "main", default);

        Assert.Single(store.Query("SELECT run_id FROM runs"));
        Assert.Equal(started, store.Query("SELECT started_utc FROM runs WHERE run_id = 'run-1'")[0]["started_utc"]);
    }
}
