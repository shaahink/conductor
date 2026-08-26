using System.Xml.Linq;

using Conductor.Core.Courier;

using Xunit;

namespace Conductor.Tests;

/// <summary>DV4.2 / findings §6.4 — the courier's lifecycle, and the version skew it makes possible.
///
/// <para>Two things are being pinned here. The first is the TASK DEFINITION: a daemon registered
/// without restart-on-failure, without <c>IgnoreNew</c> or with an execution time limit is a courier
/// that stops answering the phone silently, weeks later, and the XML is the only place those
/// decisions exist. The second is the HANDSHAKE: the courier outlives a reinstall by design, so a
/// run that speaks a newer protocol has to refuse the stale one BY NAME and say which command fixes
/// it — a refusal without the command is how a person ends up killing a pid by hand.</para>
///
/// <para>Nothing here touches the machine's scheduler. <see cref="CourierTask"/> takes its runner as
/// a parameter for exactly this reason; the real <c>schtasks.exe</c> is exercised by
/// <c>tools/dv4/dv4-2-live-proof.ps1</c>, against a scratch-named task it then removes.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class DV4_2CourierLifecycleTests : IDisposable
{
    private const string ScratchTask = "Conductor Courier SCRATCH dv4-2";

    private readonly string _tmp;
    private readonly List<(string Exe, string Args)> _calls = [];
    private string? _xmlSeen;

    public DV4_2CourierLifecycleTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"conductor-dv42-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_tmp); } catch (Exception) { }
    }

    /// <summary>A schtasks that records instead of scheduling. It reads the XML file the install
    /// wrote WHILE the call is in flight, which is the only moment it exists.</summary>
    private async Task<ShellResult> Fake(string exe, string args)
    {
        _calls.Add((exe, args));
        var marker = args.IndexOf("/XML \"", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var start = marker + 6;
            var end = args.IndexOf('"', start);
            _xmlSeen = await File.ReadAllTextAsync(args[start..end]);
        }

        return new ShellResult(0, "", "");
    }

    private Task<ShellResult> Missing(string exe, string args)
    {
        _calls.Add((exe, args));
        return Task.FromResult(new ShellResult(1, "", "ERROR: The system cannot find the file specified."));
    }

    // ── the task definition ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Install_registers_a_logon_task_that_restarts_on_failure_without_admin()
    {
        var task = new CourierTask(ScratchTask, Fake);
        var result = await task.InstallAsync(@"C:\engine\conductor.exe");

        Assert.True(result.Ok);
        Assert.Single(_calls);
        Assert.Equal(CourierTask.Schtasks, _calls[0].Exe);
        Assert.Contains($"/Create /TN \"{ScratchTask}\"", _calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("/F", _calls[0].Args, StringComparison.Ordinal);

        var xml = XDocument.Parse(_xmlSeen!);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        // The daemon starts itself: nobody was starting it in §1.4-B.
        Assert.NotNull(xml.Root!.Element(ns + "Triggers")!.Element(ns + "LogonTrigger"));

        // ...and gets back up on its own. A courier that exits on the first post-sleep poll and
        // stays dead is the failure §6.4 names, and only this element prevents it.
        var restart = xml.Root.Element(ns + "Settings")!.Element(ns + "RestartOnFailure")!;
        Assert.Equal("PT1M", restart.Element(ns + "Interval")!.Value);
        Assert.True(int.Parse(restart.Element(ns + "Count")!.Value, System.Globalization.CultureInfo.InvariantCulture) > 0);

        // No admin rights, no elevation prompt — the install discipline §6.4 asks for.
        var principal = xml.Root.Element(ns + "Principals")!.Element(ns + "Principal")!;
        Assert.Equal("LeastPrivilege", principal.Element(ns + "RunLevel")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(ns + "LogonType")!.Value);

        var settings = xml.Root.Element(ns + "Settings")!;
        // ONE getUpdates consumer per token: a second logon must not start a second poller.
        Assert.Equal("IgnoreNew", settings.Element(ns + "MultipleInstancesPolicy")!.Value);
        // A daemon has no deadline; the default would kill it after three days.
        Assert.Equal("PT0S", settings.Element(ns + "ExecutionTimeLimit")!.Value);

        var exec = xml.Root.Element(ns + "Actions")!.Element(ns + "Exec")!;
        Assert.Equal(@"C:\engine\conductor.exe", exec.Element(ns + "Command")!.Value);
        Assert.Equal("courier run", exec.Element(ns + "Arguments")!.Value);
    }

    [Fact]
    public async Task Install_leaves_no_temp_xml_behind()
    {
        string? path = null;
        var task = new CourierTask(ScratchTask, (_, args) =>
        {
            var start = args.IndexOf("/XML \"", StringComparison.Ordinal) + 6;
            path = args[start..args.IndexOf('"', start)];
            Assert.True(File.Exists(path));
            return Task.FromResult(new ShellResult(0, "", ""));
        });

        await task.InstallAsync(@"C:\engine\conductor.exe");
        Assert.False(File.Exists(path!));
    }

    [Fact]
    public void Xml_escapes_a_path_that_would_otherwise_break_the_document()
    {
        var xml = new CourierTask(ScratchTask).BuildXml(@"C:\a&b\conductor.exe", "courier run", @"C:\a&b");
        Assert.Contains("a&amp;b", xml, StringComparison.Ordinal);
        Assert.NotNull(XDocument.Parse(xml).Root);   // parses at all — the point of the escape
    }

    [Fact]
    public async Task Uninstall_stops_before_it_deletes()
    {
        var task = new CourierTask(ScratchTask, Fake);
        await task.UninstallAsync();

        Assert.Equal(2, _calls.Count);
        Assert.StartsWith("/End", _calls[0].Args, StringComparison.Ordinal);
        Assert.StartsWith("/Delete", _calls[1].Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_is_not_registered_when_the_scheduler_does_not_know_the_name()
    {
        var state = await new CourierTask(ScratchTask, Missing).StateAsync(_tmp);

        Assert.False(state.Registered);
        Assert.Null(state.SchedulerState);
        Assert.Null(state.Running);
        Assert.Equal(ScratchTask, state.Name);
    }

    [Fact]
    public async Task State_reads_the_status_column_by_position_never_by_header()
    {
        // Localised headers, localised values, stable POSITIONS. The fourth field is Status.
        var csv = "\"DESKTOP\",\"\\Conductor Courier\",\"25/08/2026 09:00:00\",\"Wird ausgef\u00fchrt\"\n";
        var task = new CourierTask(ScratchTask, (_, _) => Task.FromResult(new ShellResult(0, csv, "")));

        var state = await task.StateAsync(_tmp);

        Assert.True(state.Registered);
        Assert.Equal("Wird ausgef\u00fchrt", state.SchedulerState);
    }

    [Fact]
    public void Csv_status_survives_a_comma_inside_a_quoted_field()
    {
        var csv = "\"HOST\",\"\\Conductor Courier\",\"26/08/2026, 09:00\",\"Ready\",\"Interactive only\"";
        Assert.Equal("Ready", CourierTask.CsvStatus(csv));
    }

    // ── the presence record ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Presence_round_trips_and_says_what_is_running()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-3);
        new CourierPresence(CourierProtocol.Version, 4242, "0.4.1", @"C:\engine\conductor.exe", ScratchTask, started)
            .Write(_tmp);

        var read = CourierPresence.Read(_tmp);

        Assert.NotNull(read);
        Assert.Equal(4242, read!.Pid);
        Assert.Equal(CourierProtocol.Version, read.Protocol);
        Assert.Equal(ScratchTask, read.TaskName);
        Assert.Contains("pid 4242", read.Describe(), StringComparison.Ordinal);
        Assert.Contains("protocol", read.Describe(), StringComparison.Ordinal);
        Assert.True(File.Exists(CourierHome.PresencePathFor(_tmp)));
    }

    [Fact]
    public void Presence_of_a_dead_courier_is_not_live()
    {
        Write(pid: 4242, started: DateTimeOffset.UtcNow);

        Assert.Null(CourierPresence.Live(_tmp, _ => null));       // no such process
        Assert.NotNull(CourierPresence.Read(_tmp));               // ...but the claim is still there
    }

    [Fact]
    public void A_recycled_pid_is_not_the_courier()
    {
        var started = DateTimeOffset.UtcNow.AddHours(-2);
        Write(pid: 4242, started: started);

        // Same pid, but the process running under it started long after the courier did: Windows
        // handed the number to somebody else, and "a process exists" would have said yes.
        Assert.Null(CourierPresence.Live(_tmp, _ => started.AddMinutes(30)));
        Assert.NotNull(CourierPresence.Live(_tmp, _ => started));
    }

    [Fact]
    public void Clearing_the_presence_leaves_nothing_for_the_next_reader()
    {
        Write(pid: 4242, started: DateTimeOffset.UtcNow);
        CourierPresence.Clear(_tmp);

        Assert.Null(CourierPresence.Read(_tmp));
        Assert.Null(CourierPresence.Live(_tmp, _ => DateTimeOffset.UtcNow));
    }

    [Fact]
    public void An_unreadable_presence_file_reads_as_no_courier_rather_than_a_throw()
    {
        Directory.CreateDirectory(CourierHome.DirFor(_tmp));
        File.WriteAllText(CourierHome.PresencePathFor(_tmp), "{ this is not json");

        Assert.Null(CourierPresence.Read(_tmp));
        Assert.Null(CourierPresence.Live(_tmp));
    }

    // ── the version handshake ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_courier_of_the_same_or_newer_protocol_is_not_refused()
    {
        var same = At(CourierProtocol.Version);
        var newer = At(CourierProtocol.Version + 1);

        Assert.Null(CourierProtocol.RefuseStale(same));
        Assert.Null(CourierProtocol.RefuseStale(newer));
        Assert.Null(CourierProtocol.RefuseStale(null));   // no courier is not a stale courier
    }

    [Fact]
    public void A_stale_courier_is_refused_by_name_and_told_how_to_restart()
    {
        var stale = At(CourierProtocol.Version - 1);

        var refusal = CourierProtocol.RefuseStale(stale, CourierProtocol.Version);

        Assert.NotNull(refusal);
        Assert.Contains(ScratchTask, refusal!, StringComparison.Ordinal);            // BY NAME
        Assert.Contains(CourierProtocol.RestartVerb, refusal, StringComparison.Ordinal);
        Assert.Contains($"--task-name \"{ScratchTask}\"", refusal, StringComparison.Ordinal);
        Assert.Contains("4242", refusal, StringComparison.Ordinal);                  // and by pid
        Assert.Contains("0.4.0", refusal, StringComparison.Ordinal);                 // the old engine
    }

    [Fact]
    public void The_default_courier_is_refused_with_the_bare_restart_command()
    {
        var stale = new CourierPresence(
            CourierProtocol.Version - 1, 7, "0.4.0", null, CourierTask.DefaultName, DateTimeOffset.UtcNow);

        var refusal = CourierProtocol.RefuseStale(stale);

        Assert.Contains(CourierTask.DefaultName, refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain("--task-name", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_courier_started_by_hand_is_still_named_in_the_refusal()
    {
        var stale = new CourierPresence(CourierProtocol.Version - 1, 7, "0.4.0", null, null, DateTimeOffset.UtcNow);

        // No task name recorded — the default is what a person will look for in the scheduler.
        Assert.Contains(CourierTask.DefaultName, CourierProtocol.RefuseStale(stale)!, StringComparison.Ordinal);
    }

    // ── the installer's half of §6.4 ────────────────────────────────────────────────────────

    [Fact]
    public void The_installer_stops_the_courier_before_the_publish_and_starts_it_after()
    {
        var repo = RepoRoot();
        var guard = Path.Combine(repo, "tools", "lib", "courier-guard.ps1");
        var installer = File.ReadAllText(Path.Combine(repo, "tools", "install.ps1"));

        Assert.True(File.Exists(guard), guard + " is what install.ps1 dot-sources");
        Assert.Contains("courier-guard.ps1", installer, StringComparison.Ordinal);

        // Order is the whole point: a courier stopped AFTER the publish has already broken it with a
        // file lock, and one never restarted keeps yesterday's engine running indefinitely.
        var stop = installer.IndexOf("Stop-ConductorCourier", StringComparison.Ordinal);
        var publish = installer.IndexOf("dotnet publish", StringComparison.Ordinal);
        var start = installer.IndexOf("Start-ConductorCourier", StringComparison.Ordinal);
        Assert.True(stop > 0 && publish > stop, "install.ps1 must stop the courier before publishing");
        Assert.True(start > publish, "install.ps1 must start the courier again after publishing");

        // Trap 12: Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI.
        Assert.All(File.ReadAllText(guard), c => Assert.True(c < 128, "courier-guard.ps1 must be ASCII"));
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    private CourierPresence At(int protocol) =>
        new(protocol, 4242, protocol < CourierProtocol.Version ? "0.4.0" : "0.4.1",
            @"C:\engine\conductor.exe", ScratchTask, DateTimeOffset.UtcNow);

    private void Write(int pid, DateTimeOffset started) =>
        new CourierPresence(CourierProtocol.Version, pid, "0.4.1", @"C:\engine\conductor.exe", ScratchTask, started)
            .Write(_tmp);
}
