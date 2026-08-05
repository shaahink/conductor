using Conductor.Core.Store;

namespace Conductor.Tests;

/// <summary>
/// K7.2 / bug #33 — the reinstall path. The legacy import fired exactly once and an existing target
/// was never reconsidered, so a snapshot taken by one exploratory run of a new build became the live
/// database at the next install, while the engine that was actually driving the run kept writing the
/// legacy file for hours afterwards. Nothing said a word: the run simply resumed from where it stood
/// when that snapshot was taken.
/// <para>The rule these tests pin: refresh the copy when it is provably still the copy (nothing has
/// been written to it or its sidecars since the receipt) and the source has moved on; never touch a
/// target that carries work of its own, but say so out loud instead of resuming from it in silence.</para>
/// </summary>
[Collection(StateSinkCollection.Name)]
public sealed class K7_2StaleSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-k72stale-{Guid.NewGuid():N}");
    private readonly string _legacy;
    private readonly string _target;

    public K7_2StaleSnapshotTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "repo", ".conductor"));
        Directory.CreateDirectory(Path.Combine(_dir, "home"));
        _legacy = Path.Combine(_dir, "repo", ".conductor", "run.db");
        _target = Path.Combine(_dir, "home", "run.db");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { }
    }

    /// <summary>Captures the warning sink only for the duration of one call — the sink is static and
    /// the suite runs classes in parallel.</summary>
    private static (StateImport? Import, List<string> Warnings) ImportCapturing(string legacy, string target)
    {
        var warnings = new List<string>();
        StateMigration.Warn = warnings.Add;
        try { return (StateMigration.ImportLegacy(legacy, target), warnings); }
        finally { StateMigration.Warn = null; }
    }

    [Fact]
    public void A_copy_the_source_has_outrun_is_refreshed_and_says_so()
    {
        File.WriteAllText(_legacy, "sessions 1-40");
        Assert.NotNull(StateMigration.ImportLegacy(_legacy, _target));

        // The published engine keeps running the same run against the legacy file for hours.
        File.WriteAllText(_legacy, "sessions 1-40, and 41-94 besides");

        var (refresh, warnings) = ImportCapturing(_legacy, _target);

        Assert.NotNull(refresh);
        Assert.True(refresh!.Refreshed);
        Assert.Equal("sessions 1-40, and 41-94 besides", File.ReadAllText(_target));
        Assert.Empty(warnings);                                    // refreshed, not a dilemma
        Assert.Contains("refreshed a stale copy", StateMigration.Describe(refresh));

        // The receipt is rewritten, so the next resolution fences against THIS copy.
        Assert.True(StateMigration.ReadReceipt(_target)!.Refreshed);
    }

    [Fact]
    public void A_target_that_holds_its_own_work_is_never_clobbered_but_the_divergence_is_announced()
    {
        File.WriteAllText(_legacy, "sessions 1-40");
        StateMigration.ImportLegacy(_legacy, _target);

        File.WriteAllText(_target, "sessions 1-40, plus work done at the new home");
        File.WriteAllText(_legacy, "sessions 1-40, and 41-94 besides");

        var (second, warnings) = ImportCapturing(_legacy, _target);

        Assert.Null(second);
        Assert.Equal("sessions 1-40, plus work done at the new home", File.ReadAllText(_target));
        Assert.Single(warnings);
        Assert.Contains(_legacy, warnings[0]);
        Assert.Contains("reconcile", warnings[0]);
    }

    [Fact]
    public void An_unmoved_source_changes_nothing_and_warns_about_nothing()
    {
        File.WriteAllText(_legacy, "sessions 1-40");
        StateMigration.ImportLegacy(_legacy, _target);

        var (second, warnings) = ImportCapturing(_legacy, _target);

        Assert.Null(second);                                       // K3.1's idempotence, intact
        Assert.Empty(warnings);
    }

    /// <summary>A database this never imported is somebody's state, not a snapshot of anything. No
    /// receipt, no opinion — whatever the legacy file is doing.</summary>
    [Fact]
    public void A_target_with_no_receipt_is_left_alone_entirely()
    {
        File.WriteAllText(_legacy, "sessions 1-40, and 41-94 besides");
        File.WriteAllText(_target, "a database that arrived some other way");

        var (result, warnings) = ImportCapturing(_legacy, _target);

        Assert.Null(result);
        Assert.Equal("a database that arrived some other way", File.ReadAllText(_target));
        Assert.Empty(warnings);
    }

    /// <summary>A refresh must not leave the replaced copy's sidecars behind: a fresh main file
    /// paired with a stale <c>-wal</c> is a torn database.</summary>
    [Fact]
    public void Refreshing_clears_sidecars_the_new_source_does_not_have()
    {
        File.WriteAllText(_legacy, "sessions 1-40");
        File.WriteAllText(_legacy + "-wal", "uncheckpointed tail");
        StateMigration.ImportLegacy(_legacy, _target);
        Assert.True(File.Exists(_target + "-wal"));

        File.Delete(_legacy + "-wal");                              // checkpointed since
        File.WriteAllText(_legacy, "sessions 1-40, and 41-94 besides");

        Assert.NotNull(StateMigration.ImportLegacy(_legacy, _target));
        Assert.False(File.Exists(_target + "-wal"));
    }


}
