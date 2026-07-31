using System.Diagnostics;
using Conductor.Core;

namespace Conductor.Tests;

/// <summary>
/// SC2.1. The lock file stopped being a private detail of the run loop the moment `conductor status`
/// started reading it for liveness, so its parsing and its verdicts are pinned here: an engine that is
/// really running reads live, an engine that died reads dead however its id was later reused, and a
/// file written by an older engine (bare pid, no stamp) still parses.
/// </summary>
public sealed class EngineLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "conductor-lock-" + Guid.NewGuid().ToString("N"));

    public EngineLockTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void NoFile_IsNotHeld()
    {
        Assert.Null(EngineLock.Read(_dir));
        Assert.False(EngineLock.IsHeldByLiveEngine(_dir));
    }

    /// <summary>Write then read: the holder is this process, and it is alive — which is the whole point,
    /// because the engine that writes the lock is the engine status must find.</summary>
    [Fact]
    public void Write_NamesThisProcess_AndReadsLive()
    {
        EngineLock.Write(_dir);

        var holder = EngineLock.Read(_dir);
        Assert.NotNull(holder);
        Assert.Equal(Environment.ProcessId, holder!.Pid);
        Assert.NotNull(holder.StartedUtc);
        using (var me = Process.GetCurrentProcess())
            Assert.Equal(me.StartTime.ToUniversalTime(), holder.StartedUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.True(EngineLock.IsLive(holder));
        Assert.True(EngineLock.IsHeldByLiveEngine(_dir));
    }

    /// <summary>Back-compat: every lock file already on disk holds a bare pid. It must keep working, and
    /// with no stamp to disqualify it the answer degrades to the existence check the old code did.</summary>
    [Fact]
    public void LegacyBarePidFile_StillParses_AndReadsLive()
    {
        File.WriteAllText(EngineLock.PathFor(_dir), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var holder = EngineLock.Read(_dir);
        Assert.NotNull(holder);
        Assert.Equal(Environment.ProcessId, holder!.Pid);
        Assert.Null(holder.StartedUtc);
        Assert.True(EngineLock.IsHeldByLiveEngine(_dir));
    }

    /// <summary>A trailing newline is what `echo` and every text editor leave behind.</summary>
    [Fact]
    public void LegacyBarePidFile_WithTrailingNewline_StillParses()
    {
        File.WriteAllText(EngineLock.PathFor(_dir), Environment.ProcessId + "\r\n");
        Assert.Equal(Environment.ProcessId, EngineLock.Read(_dir)!.Pid);
    }

    /// <summary>The reason the stamp exists: a dead engine's id, handed by the OS to something else, must
    /// not read as a live engine. Here the id is this very process and the stamp predates it by years.</summary>
    [Fact]
    public void RecycledPid_ReadsDead()
    {
        File.WriteAllText(EngineLock.PathFor(_dir),
            Environment.ProcessId + "\n" + new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O"));

        var holder = EngineLock.Read(_dir);
        Assert.NotNull(holder);
        Assert.False(EngineLock.IsLive(holder!));
        Assert.False(EngineLock.IsHeldByLiveEngine(_dir));
    }

    [Fact]
    public void GarbageFile_IsNotHeld()
    {
        File.WriteAllText(EngineLock.PathFor(_dir), "not a pid");
        Assert.Null(EngineLock.Read(_dir));
        Assert.False(EngineLock.IsHeldByLiveEngine(_dir));
    }

    [Fact]
    public void Delete_ReleasesTheLock()
    {
        EngineLock.Write(_dir);
        EngineLock.Delete(_dir);
        Assert.False(File.Exists(EngineLock.PathFor(_dir)));
        Assert.False(EngineLock.IsHeldByLiveEngine(_dir));
    }
}
