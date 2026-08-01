namespace Conductor.Core;

/// <summary>
/// SC2.1: the engine's own liveness, on disk.
///
/// <para><c>.conductor/conductor.lock</c> has always been the engine's declaration that it owns this
/// plan — written when the run loop starts, deleted when it stops. Nothing outside the run loop read
/// it, so every other surface had to infer liveness from the <c>pids</c> table, which tracks only
/// spawned children (agents, bg jobs). Between an agent exiting and its <c>SessionFinished</c> landing
/// the engine runs the whole gate battery with no child of its own, and <c>conductor status</c> read
/// that as a crash — "interrupted mid-session, resume with <c>conductor run</c>", the one command that
/// would start a second engine on a healthy run.</para>
///
/// <para>The file now carries the holder's start time as well as its pid, because a pid alone is not
/// an identity: a lock left behind by an engine that died names an id the OS may since have handed to
/// something else. <see cref="PidLiveness"/> settles that the same way it does for spawned pids. Files
/// written by an older engine hold a bare pid and still parse — they simply fall back to an existence
/// check, which is exactly what the old code did.</para>
/// </summary>
public static class EngineLock
{
    public const string FileName = "conductor.lock";

    /// <summary>Who the lock file says is running this plan. <paramref name="StartedUtc"/> is null for a
    /// file written by an engine that predates the stamp.</summary>
    public sealed record Holder(int Pid, DateTime? StartedUtc);

    public static string PathFor(string stateDir) => Path.Combine(stateDir, FileName);

    /// <summary>Claim the lock for this process. Overwrites whatever is there — callers check
    /// <see cref="IsHeldByLiveEngine"/> first.</summary>
    public static void Write(string stateDir)
    {
        var pid = Environment.ProcessId;
        DateTime? started = null;
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            started = me.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Unreadable own start time: write the legacy bare-pid shape rather than a wrong stamp.
        }
        var body = started is { } s
            ? pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + s.ToString("O")
            : pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(PathFor(stateDir), body);
    }

    public static void Delete(string stateDir)
    {
        try { if (File.Exists(PathFor(stateDir))) File.Delete(PathFor(stateDir)); }
        catch (IOException) { /* reclaimed on the next start via pid liveness */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>Null when there is no lock file, or its first line is not a pid.</summary>
    public static Holder? Read(string stateDir)
    {
        string text;
        try
        {
            var path = PathFor(stateDir);
            if (!File.Exists(path)) return null;
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        return Parse(text);
    }

    /// <summary>The lock file's contents, parsed. Split out for SF5.4: <c>conductor ps</c> reads other
    /// runs' lock files and needs the parse without a second file-existence dance.</summary>
    public static Holder? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        if (!int.TryParse(lines[0].Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            return null;

        DateTime? started = null;
        if (lines.Length > 1 && DateTime.TryParse(lines[1].Trim(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
            started = parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();

        return new Holder(pid, started);
    }

    /// <summary>True only while the process named by the lock is still the engine that wrote it. A
    /// recycled id reads false — that engine is gone, whoever owns the id now.</summary>
    public static bool IsLive(Holder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        // No stamp (legacy file) → the tracked instant cannot disqualify anything, so this degrades to
        // the bare existence check the old lock logic used.
        return PidLiveness.LooksAlive(holder.Pid, holder.StartedUtc ?? DateTime.UtcNow);
    }

    /// <summary>Is an engine running this plan right now?</summary>
    public static bool IsHeldByLiveEngine(string stateDir)
    {
        var holder = Read(stateDir);
        return holder != null && IsLive(holder);
    }
}
