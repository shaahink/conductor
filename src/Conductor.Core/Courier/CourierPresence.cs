using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Conductor.Core.Inbox;

namespace Conductor.Core.Courier;

/// <summary>DV4.2 / findings §6.4 — what the running courier says about itself, on disk.
///
/// <para>Three different things need this answer and none of them can ask the daemon directly yet:
/// <c>courier status</c> ("is one running, and which build?"), <c>tools/install.ps1</c> ("must I
/// stop it before I overwrite the exe it is holding open?"), and a run checking for version skew
/// (<see cref="CourierProtocol.RefuseStale"/>). DV4.3 adds the loopback hello that serves the same
/// record over a socket; this file is that hello written down, and it is what makes the handshake
/// real before the listener exists rather than a constant nobody reads.</para>
///
/// <para><b>A record on disk is a claim, not a fact.</b> A courier killed with the machine leaves
/// its file behind, so <see cref="Live"/> checks the pid is actually a running process AND that its
/// start time matches what was recorded — a recycled pid is otherwise indistinguishable from the
/// daemon, and "something else is running, do not overwrite the engine" is a stall nobody can
/// diagnose.</para></summary>
/// <param name="Protocol">The run↔courier protocol this courier speaks (<see cref="CourierProtocol.Version"/>).</param>
/// <param name="Pid">The daemon's process id.</param>
/// <param name="Engine">The engine version it is running — the thing a reinstall changes.</param>
/// <param name="Exe">The binary it is holding open, which is what a reinstall collides with.</param>
/// <param name="TaskName">The scheduled task it was started by, or null when started by hand.</param>
/// <param name="StartedUtc">When it started — also the guard against a recycled pid.</param>
/// <param name="Port">DV4.3 — the loopback port its listener actually bound, or null when it has
/// none (a courier from before the listener existed, or one whose named port was taken). A run
/// reads the port from the SAME record it reads the protocol from rather than assuming the
/// constant, so a rig's port override cannot make a run dial a stranger.</param>
public sealed record CourierPresence(
    int Protocol,
    int Pid,
    string? Engine,
    string? Exe,
    string? TaskName,
    DateTimeOffset StartedUtc,
    int? Port = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The presence of THIS process — what <c>courier run</c> writes as it starts.</summary>
    /// <param name="taskName">The scheduled task that started it, or null when started by hand.</param>
    /// <param name="port">The loopback port its listener bound, or null when it has no listener.</param>
    public static CourierPresence Current(string? taskName = null, int? port = null)
    {
        using var self = Process.GetCurrentProcess();
        return new CourierPresence(
            Protocol: CourierProtocol.Version,
            Pid: Environment.ProcessId,
            Engine: typeof(CourierPresence).Assembly.GetName().Version?.ToString(),
            Exe: Environment.ProcessPath,
            TaskName: string.IsNullOrWhiteSpace(taskName) ? null : taskName,
            StartedUtc: StartTimeOf(self) ?? DateTimeOffset.UtcNow,
            Port: port is > 0 ? port : null);
    }

    /// <summary>Writes the record. Atomic, like everything else the courier writes: install.ps1 reads
    /// this file at a moment nobody coordinates with, and half a JSON document reads as "no courier".</summary>
    public void Write(string? stateHomeRoot = null)
    {
        var path = CourierHome.PresencePathFor(stateHomeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        InboxStore.WriteAtomic(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>The record as written, alive or not. Null when there is no file or it is unreadable —
    /// an unreadable presence file must not stop a reinstall, so it is treated as absent.</summary>
    public static CourierPresence? Read(string? stateHomeRoot = null)
    {
        var path = CourierHome.PresencePathFor(stateHomeRoot);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CourierPresence>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The record ONLY if the process it names is genuinely still running. See the type
    /// remarks for why the pid alone is not enough.</summary>
    /// <param name="probe">The start time of a pid, or null when no such process runs. For tests.</param>
    public static CourierPresence? Live(string? stateHomeRoot = null, Func<int, DateTimeOffset?>? probe = null)
    {
        var claimed = Read(stateHomeRoot);
        if (claimed is null) return null;

        var started = (probe ?? StartTimeOf)(claimed.Pid);
        if (started is null) return null;

        // Within a couple of seconds: the recorded time comes from the same API on the same process,
        // so a genuine match is exact; the tolerance is for a record written by a shell wrapper.
        return Math.Abs((started.Value - claimed.StartedUtc).TotalSeconds) <= 2 ? claimed : null;
    }

    /// <summary>Removes the record — what a courier does on its way out, so the next reader sees the
    /// truth rather than a pid it has to probe.</summary>
    public static void Clear(string? stateHomeRoot = null)
    {
        try
        {
            File.Delete(CourierHome.PresencePathFor(stateHomeRoot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A presence file we cannot delete is a stale claim, and Live() already survives one.
        }
    }

    /// <summary>One line for a terminal: what is running, or that nothing is.</summary>
    public string Describe() =>
        $"pid {Pid.ToString(CultureInfo.InvariantCulture)}"
      + $" · protocol {Protocol.ToString(CultureInfo.InvariantCulture)}"
      + (string.IsNullOrWhiteSpace(Engine) ? "" : $" · engine {Engine}")
      + (Port is > 0 ? $" · port {Port.Value.ToString(CultureInfo.InvariantCulture)}" : " · no listener")
      + (string.IsNullOrWhiteSpace(TaskName) ? " · started by hand" : $" · task {TaskName}")
      + $" · since {StartedUtc.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)}";

    private static DateTimeOffset? StartTimeOf(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.HasExited ? null : StartTimeOf(proc);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static DateTimeOffset? StartTimeOf(Process proc)
    {
        try
        {
            return new DateTimeOffset(proc.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
