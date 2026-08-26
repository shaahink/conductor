using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Conductor.Core.Courier;

/// <summary>What a child process said. The courier's lifecycle talks to exactly one program —
/// <c>schtasks.exe</c> — and this is the seam a test replaces so the suite never registers anything
/// on the machine running it.</summary>
public sealed record ShellResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>The line to show a person when it failed: whatever the tool actually said.</summary>
    public string Complaint()
    {
        var said = string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr;
        said = said.Replace("\r", " ", StringComparison.Ordinal)
                   .Replace("\n", " ", StringComparison.Ordinal)
                   .Trim();
        return said.Length > 0 ? said : $"exit code {ExitCode.ToString(CultureInfo.InvariantCulture)}";
    }
}

/// <summary>Where the lifecycle verbs found the courier. Registration is the scheduler's answer;
/// <see cref="Running"/> is the PRESENCE file's, because "is a process alive" is a question the
/// scheduler answers in a localised string and the presence record answers in a pid.</summary>
public sealed record CourierTaskState(string Name, bool Registered, string? SchedulerState, CourierPresence? Running);

/// <summary>DV4.2 / findings §6.4 — the courier's lifecycle, as a per-user Scheduled Task.
///
/// <para>Nobody started the daemon in §1.4-B. This is the answer: a logon-triggered task that
/// restarts on failure, registered as the CURRENT USER with <c>LeastPrivilege</c>. No admin rights,
/// no service ceremony, no elevation prompt — a courier that needs an administrator to install is a
/// courier that does not get installed.</para>
///
/// <para><b>Why XML rather than <c>schtasks /Create /SC ONLOGON</c>.</b> The command-line form cannot
/// express restart-on-failure at all, and restart-on-failure is the entire point: the machine wakes
/// from sleep with no network, the first poll throws, and a daemon that exits there stopped answering
/// the phone weeks ago without saying so. The XML carries <c>RestartOnFailure</c> every minute,
/// <c>ExecutionTimeLimit PT0S</c> (a daemon has no deadline) and <c>IgnoreNew</c> — a second logon
/// must not start a second poller, because Telegram allows ONE getUpdates consumer per token.</para>
///
/// <para>The task NAME is a parameter with a default, not a constant, for one measured reason: the
/// live proof has to register a real task against the real scheduler and remove it again, and it may
/// not touch the owner's courier to do it.</para></summary>
public sealed class CourierTask
{
    /// <summary>The task the owner's machine gets. A scratch proof passes its own name.</summary>
    public const string DefaultName = "Conductor Courier";

    /// <summary>The scheduler binary. Named once, here.</summary>
    public const string Schtasks = "schtasks.exe";

    private readonly Func<string, string, Task<ShellResult>> _run;

    /// <param name="name">The task name, or null for <see cref="DefaultName"/>.</param>
    /// <param name="run">The (exe, args) runner. Null uses the real <c>schtasks.exe</c>.</param>
    public CourierTask(string? name = null, Func<string, string, Task<ShellResult>>? run = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
        _run = run ?? Shell;
    }

    /// <summary>The registered task's name.</summary>
    public string Name { get; }

    public static bool IsDefaultName(string? name) =>
        string.Equals(
            string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim(),
            DefaultName,
            StringComparison.Ordinal);

    /// <summary>Registers (or replaces) the task. <paramref name="exe"/> is the engine binary the
    /// task will run — <c>Environment.ProcessPath</c> of whatever performed the install, so a courier
    /// installed from the published engine runs the published engine.</summary>
    public async Task<ShellResult> InstallAsync(string exe, string? arguments = null, string? workingDirectory = null)
    {
        var xml = BuildXml(exe, arguments ?? "courier run", workingDirectory);
        var file = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "conductor-courier-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            // UTF-16 with a BOM: schtasks /XML rejects a file whose encoding does not match the
            // declaration, and the declaration the scheduler itself writes is UTF-16.
            await File.WriteAllTextAsync(file, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true))
                .ConfigureAwait(false);
            return await _run(Schtasks, $"/Create /TN \"{Name}\" /XML \"{file}\" /F").ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(file); }
            catch (IOException) { /* a temp file we could not remove is not a failed install */ }
        }
    }

    /// <summary>Stops the task if it is running, then removes the registration.</summary>
    public async Task<ShellResult> UninstallAsync()
    {
        await StopAsync().ConfigureAwait(false);
        return await _run(Schtasks, $"/Delete /TN \"{Name}\" /F").ConfigureAwait(false);
    }

    /// <summary>Starts the task now, without waiting for a logon.</summary>
    public Task<ShellResult> StartAsync() => _run(Schtasks, $"/Run /TN \"{Name}\"");

    /// <summary>Ends the task's running instance. Not an error when nothing is running — the caller
    /// asked for the courier stopped, and it is.</summary>
    public Task<ShellResult> StopAsync() => _run(Schtasks, $"/End /TN \"{Name}\"");

    /// <summary>What the scheduler knows about the task, and what the presence file knows about the
    /// process. Both, because either alone lies: a registered task may not be running, and a courier
    /// started by hand is running with no task at all.</summary>
    public async Task<CourierTaskState> StateAsync(string? stateHomeRoot = null, Func<int, DateTimeOffset?>? probe = null)
    {
        var query = await _run(Schtasks, $"/Query /TN \"{Name}\" /FO CSV /NH").ConfigureAwait(false);
        var live = CourierPresence.Live(stateHomeRoot, probe);
        return new CourierTaskState(Name, query.Ok, query.Ok ? CsvStatus(query.StdOut) : null, live);
    }

    /// <summary>The fourth CSV column of <c>schtasks /Query /FO CSV /NH</c> — Status. Read by
    /// POSITION, never by header: the headers are localised and the positions are not. The value
    /// itself is localised too, so it is shown to a person and never branched on.</summary>
    internal static string? CsvStatus(string csv)
    {
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = SplitCsv(line.Trim());
            if (fields.Count >= 4 && fields[3].Length > 0) return fields[3];
        }
        return null;
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { fields.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }

        fields.Add(cell.ToString());
        return fields;
    }

    /// <summary>The task definition. Element order follows what the Task Scheduler itself exports,
    /// because that is the order its schema validator accepts without argument.</summary>
    internal string BuildXml(string exe, string arguments, string? workingDirectory)
    {
        var user = Escape(Environment.UserDomainName + "\\" + Environment.UserName);
        var cwd = string.IsNullOrWhiteSpace(workingDirectory)
            ? ""
            : "      <WorkingDirectory>" + Escape(workingDirectory) + "</WorkingDirectory>\n";

        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n"
          + "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n"
          + "  <RegistrationInfo>\n"
          + "    <Description>" + Escape(Description) + "</Description>\n"
          + "    <Author>conductor</Author>\n"
          + "  </RegistrationInfo>\n"
          + "  <Triggers>\n"
          + "    <LogonTrigger>\n"
          + "      <Enabled>true</Enabled>\n"
          + "      <UserId>" + user + "</UserId>\n"
          + "    </LogonTrigger>\n"
          + "  </Triggers>\n"
          + "  <Principals>\n"
          + "    <Principal id=\"Author\">\n"
          + "      <UserId>" + user + "</UserId>\n"
          + "      <LogonType>InteractiveToken</LogonType>\n"
          + "      <RunLevel>LeastPrivilege</RunLevel>\n"
          + "    </Principal>\n"
          + "  </Principals>\n"
          + "  <Settings>\n"
          + "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n"
          + "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n"
          + "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n"
          + "    <AllowHardTerminate>true</AllowHardTerminate>\n"
          + "    <StartWhenAvailable>true</StartWhenAvailable>\n"
          + "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n"
          + "    <IdleSettings>\n"
          + "      <StopOnIdleEnd>false</StopOnIdleEnd>\n"
          + "      <RestartOnIdle>false</RestartOnIdle>\n"
          + "    </IdleSettings>\n"
          + "    <AllowStartOnDemand>true</AllowStartOnDemand>\n"
          + "    <Enabled>true</Enabled>\n"
          + "    <Hidden>false</Hidden>\n"
          + "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n"
          + "    <WakeToRun>false</WakeToRun>\n"
          + "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n"
          + "    <Priority>7</Priority>\n"
          + "    <RestartOnFailure>\n"
          + "      <Interval>PT1M</Interval>\n"
          + "      <Count>99</Count>\n"
          + "    </RestartOnFailure>\n"
          + "  </Settings>\n"
          + "  <Actions Context=\"Author\">\n"
          + "    <Exec>\n"
          + "      <Command>" + Escape(exe) + "</Command>\n"
          + "      <Arguments>" + Escape(arguments) + "</Arguments>\n"
          + cwd
          + "    </Exec>\n"
          + "  </Actions>\n"
          + "</Task>\n";
    }

    /// <summary>What a person sees in the Task Scheduler. It says what the task IS, in the words
    /// somebody scrolling a list of scheduled tasks needs — and it deliberately does not name the
    /// messenger: <c>TelegramCourierSource</c> is the only file in the courier that does, and the
    /// seam boundary test (KS11.1) holds that line through string literals too.</summary>
    internal const string Description =
        "conductor courier - one bot, always awake, outliving the run. Polls for notes and files "
      + "them into the projects on its allowlist. Started at logon; restarts on failure.";

    private static string Escape(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal)
         .Replace("<", "&lt;", StringComparison.Ordinal)
         .Replace(">", "&gt;", StringComparison.Ordinal);

    private static async Task<ShellResult> Shell(string exe, string arguments)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(exe, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return new ShellResult(proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                      or InvalidOperationException
                                      or PlatformNotSupportedException)
        {
            // No scheduler on this machine (or not Windows) is a refusal with a name, not a crash.
            return new ShellResult(-1, "", $"{exe} could not be run: {ex.Message}");
        }
    }
}
