using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

using Conductor.Core.Courier;
using Conductor.Courier;

namespace Conductor.Tests;

/// <summary>PK1.1 / D1 - the courier is its own executable, and <c>conductor courier run</c> is an alias
/// that starts it. Real processes, a scratch state home and a scratch port: the alias is the engine's
/// apphost from this test's own output, never the <c>conductor</c> on PATH, and no step here holds a real
/// token or dials a real Bot API.</summary>
public sealed class PK1_1CourierExecutableTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "pk11-" + Guid.NewGuid().ToString("N"));

    public PK1_1CourierExecutableTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { /* a log the killed courier still held; the temp dir outlives nothing */ }
        catch (UnauthorizedAccessException) { }
    }

    private static string Apphost(string stem) =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? stem + ".exe" : stem);

    [Fact]
    public void TheCourierTakesTheTwoFlagsRunHasAlwaysPassed()
    {
        Assert.Equal(new CourierProgram.Options(false, null), CourierProgram.Parse([], out var none));
        Assert.Null(none);
        Assert.Equal(new CourierProgram.Options(true, "Scratch Courier"),
            CourierProgram.Parse(["--task-name", "Scratch Courier", "--once"], out _));
    }

    /// <summary>Anything else is refused by name - a task registered with a flag this build does not know
    /// is a stale registration, and running as if it had not asked hides that.</summary>
    [Theory]
    [InlineData("run")]
    [InlineData("--home")]
    [InlineData("--task-name")]
    [InlineData("-once")]
    public void TheCourierRefusesAnArgumentItDoesNotKnow(string arg)
    {
        Assert.Null(CourierProgram.Parse([arg], out var error));
        Assert.Contains(arg, error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuildPutsTheCourierBesideTheEngine()
    {
        Assert.True(File.Exists(Apphost("conductor")), "the engine apphost is not in the test output");
        Assert.True(File.Exists(CourierBinary.Beside(AppContext.BaseDirectory)),
            CourierBinary.FileName + " is not beside the engine - `conductor courier run` would have nothing to start");
    }

    /// <summary>The alias starts a DIFFERENT process: the courier's own log records its pid, and it is not
    /// the alias's. With no token the courier refuses, and the alias hands back the courier's exit code.</summary>
    [Fact]
    public async Task CourierRunStartsTheCourierBinaryAndReturnsItsExitCode()
    {
        using var alias = Start(Apphost("conductor"), ["courier", "run", "--once"], token: null, port: FreePort());
        var stdout = alias.StandardOutput.ReadToEndAsync();
        var output = await alias.StandardError.ReadToEndAsync() + await stdout;
        Assert.True(await Exited(alias, 60), "the alias did not exit");

        Assert.Equal(1, alias.ExitCode);
        Assert.Contains("no bot token", output, StringComparison.Ordinal);

        var log = await File.ReadAllLinesAsync(CourierHome.LogPathFor(_home));
        var starting = Assert.Single(log, l => l.Contains("courier run starting: pid", StringComparison.Ordinal));
        var pid = int.Parse(Regex.Match(starting, @"pid (?<pid>\d+)", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1))
            .Groups["pid"].Value, CultureInfo.InvariantCulture);
        Assert.NotEqual(alias.Id, pid);
        Assert.Contains(log, l => l.Contains("refused to start: no bot token", StringComparison.Ordinal));
    }

    /// <summary>The alias and the courier are one lifetime. A task registered before D1 runs
    /// <c>conductor courier run</c> and <c>schtasks /End</c> terminates that process only; the courier must
    /// not outlive it holding the token while the scheduler reports it stopped.</summary>
    [Fact]
    public async Task KillingTheAliasTakesTheCourierWithIt()
    {
        if (!OperatingSystem.IsWindows()) return; // the job object is the Windows half; elsewhere it is a no-op

        new CourierSettings
        {
            Chats = [new CourierChat("770000001", "admin")],
            Projects = [new CourierProject("pk11-scratch", _home)],
            ApiBaseUrl = "http://127.0.0.1:" + FreePort().ToString(CultureInfo.InvariantCulture), // nothing listens
            PollIntervalSeconds = 1,
        }.Save(_home);

        using var alias = Start(Apphost("conductor"), ["courier", "run", "--task-name", "pk11-scratch"],
            token: "111111:pk11-scratch-token", port: FreePort());
        _ = alias.StandardOutput.ReadToEndAsync();
        _ = alias.StandardError.ReadToEndAsync();

        CourierPresence? presence = null;
        for (var i = 0; i < 120 && presence is null; i++)
        {
            await Task.Delay(250);
            presence = CourierPresence.Live(_home);
            Assert.False(alias.HasExited, "the alias exited before the courier came up: exit " +
                (alias.HasExited ? alias.ExitCode.ToString(CultureInfo.InvariantCulture) : ""));
        }

        Assert.NotNull(presence);
        using var courier = Process.GetProcessById(presence!.Pid);
        Assert.Equal(CourierBinary.Stem, courier.ProcessName);
        Assert.NotEqual(alias.Id, courier.Id);

        alias.Kill(entireProcessTree: false);
        Assert.True(await Exited(courier, 15), "the courier outlived the alias that started it");
    }

    private Process Start(string exe, IEnumerable<string> args, string? token, int port)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment["CONDUCTOR_STATE_HOME"] = _home;
        start.Environment[CourierEndpoint.PortEnvVar] = port.ToString(CultureInfo.InvariantCulture);
        start.Environment.Remove("CONDUCTOR_PLAN");
        if (token is null) start.Environment.Remove("CONDUCTOR_TELEGRAM_TOKEN");
        else start.Environment["CONDUCTOR_TELEGRAM_TOKEN"] = token;
        return Process.Start(start) ?? throw new InvalidOperationException("could not start " + exe);
    }

    private static async Task<bool> Exited(Process process, int seconds)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}