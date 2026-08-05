using Conductor.Core;
using Conductor.Core.Update;

namespace Conductor.Tests;

/// <summary>
/// SC8.3 — the refusal. Swapping the engine while a run is live is the one way this verb destroys
/// work: a session spawns fresh <c>conductor</c> processes throughout its life (every task claim,
/// every note, every bg start), so a mid-run swap means the back half of a session runs on a
/// different engine than the front half, with nothing anywhere saying so.
/// </summary>
public sealed class UpdateSafetyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("conductor-update-safety-").FullName;

    // A path no process can be running from, so these tests measure the LOCK detector in isolation.
    private string AbsentBinary => Path.Combine(_dir, "not-a-real-engine.exe");

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ALiveEngineLockBlocksTheSwap_AndNamesThePid()
    {
        EngineLock.Write(_dir);   // this very process, so the lock is genuinely live

        var blockers = UpdateSafety.Blockers(AbsentBinary, [_dir]);

        Assert.Single(blockers);
        Assert.Contains("a run is live", blockers[0], StringComparison.Ordinal);
        Assert.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), blockers[0], StringComparison.Ordinal);
        Assert.Contains(_dir, blockers[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NoLockMeansNoBlocker()
    {
        Assert.Empty(UpdateSafety.Blockers(AbsentBinary, [_dir]));
        Assert.Empty(UpdateSafety.Blockers(AbsentBinary, null));
    }

    [Fact]
    public void ALockLeftBehindByADeadEngineDoesNotBlockForever()
    {
        // There is deliberately no --force, so a stale lock MUST read as free or the operator is
        // stuck with an un-updatable install. PidLiveness settles it: a pid that is gone is gone.
        File.WriteAllText(EngineLock.PathFor(_dir), "999999\n" + DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Empty(UpdateSafety.Blockers(AbsentBinary, [_dir]));
    }

    [Fact]
    public void ARecycledPidDoesNotBlock()
    {
        // Our own pid, but stamped as having started long before this process did — the lock names an
        // engine that died and whose id the OS handed to something else.
        File.WriteAllText(EngineLock.PathFor(_dir),
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
            DateTime.UtcNow.AddDays(-30).ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Empty(UpdateSafety.Blockers(AbsentBinary, [_dir]));
    }

    [Fact]
    public void EveryLiveRunIsNamed_NotJustTheFirst()
    {
        var second = Path.Combine(_dir, "other");
        Directory.CreateDirectory(second);
        EngineLock.Write(_dir);
        EngineLock.Write(second);

        var blockers = UpdateSafety.Blockers(AbsentBinary, [_dir, second]);

        Assert.Equal(2, blockers.Count);
        Assert.Contains(blockers, b => b.Contains(second, StringComparison.Ordinal));
    }

    [Fact]
    public void TheProcessDetectorNeverCountsThisProcess()
    {
        // The updater is itself running the binary it is about to replace. Counting itself would make
        // `conductor update` refuse every single time — the failure mode that turns a safety check
        // into a thing people disable.
        var me = Environment.ProcessPath;
        Assert.False(string.IsNullOrEmpty(me));

        var others = UpdateSafety.OtherProcessesRunning(me!);

        Assert.DoesNotContain(others, o => o.Pid == Environment.ProcessId);
    }

    [Fact]
    public void TheProcessDetectorIsQuietAboutPathsNothingRunsFrom()
    {
        Assert.Empty(UpdateSafety.OtherProcessesRunning(AbsentBinary));
        Assert.Empty(UpdateSafety.OtherProcessesRunning(""));
    }
}
