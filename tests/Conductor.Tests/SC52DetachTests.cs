using System.Runtime.InteropServices;
using System.Text.Json;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Http;

namespace Conductor.Tests;

/// <summary>
/// SC5.2 truth gates — the run outlives its launching shell, and a stall says what to do about it.
///
/// <para>devcontext #16: a healthy run died to an unrelated harness cleanup because the engine was
/// a child of the shell that typed <c>conductor run</c>. <c>--detach</c> answers that, and these
/// gates cover the parts a unit test can actually decide: the child's argv, the Windows command-line
/// quoting that argv survives (round-tripped through the same <c>CommandLineToArgvW</c> the child's
/// own runtime uses — not re-stated from the implementation), the discovery handshake that makes the
/// printed URL a measurement rather than a prediction, and a real detached spawn whose output lands
/// in the capture log.</para>
///
/// <para>The one claim no unit test can settle from inside the test host — that killing the
/// LAUNCHING shell leaves the run alive — is proven by the live rig in
/// <c>.conductor/evidence/SC5/SC5.2-detach.md</c>, against a scratch repo and the fresh build.</para>
/// </summary>
public sealed class SC52DetachTests
{
    private static RunCommand.Settings Defaults() => new();

    // ------------------------------------------------------------------ the child's argv

    [Fact]
    public void ChildArgs_ForcesHeadlessAndNoFace_AndNeverRedetaches()
    {
        var args = RunDetach.ChildArgs(Defaults(), @"C:\rig\demo.plan.json");

        Assert.Equal("run", args[0]);
        Assert.Contains("--headless", args, StringComparer.Ordinal);
        Assert.Contains("--no-face", args, StringComparer.Ordinal);
        // A detached process has no console for a TUI to live in, and a child that re-detached
        // would fork forever.
        Assert.DoesNotContain("--detach", args, StringComparer.Ordinal);
        Assert.Equal(@"C:\rig\demo.plan.json", args[args.IndexOf("-p") + 1]);
    }

    [Fact]
    public void ChildArgs_CarriesTheRunShapeItWasAskedFor()
    {
        var args = RunDetach.ChildArgs(
            new RunCommand.Settings { Once = true, MaxSessions = 7, Paused = true, ControlPlanePort = 4399 },
            "p.json");

        Assert.Contains("--once", args, StringComparer.Ordinal);
        Assert.Contains("--paused", args, StringComparer.Ordinal);
        Assert.Equal("7", args[args.IndexOf("--max-sessions") + 1]);
        Assert.Equal("4399", args[args.IndexOf("--port") + 1]);
    }

    [Fact]
    public void ChildArgs_OmitsMaxSessionsWhenUnbounded()
    {
        // 0 means unlimited; passing "--max-sessions 0" through would be harmless but noisy, and a
        // future stricter parser would reject it.
        Assert.DoesNotContain("--max-sessions", RunDetach.ChildArgs(Defaults(), "p.json"), StringComparer.Ordinal);
    }

    // ------------------------------------------------- quoting: measured, not asserted from source

    [Theory]
    [InlineData(@"C:\Program Files\conductor\conductor.exe")]
    [InlineData(@"C:\rig\a plan with spaces.plan.json")]
    [InlineData(@"trailing\backslash\")]
    [InlineData(@"C:\dir with space\")]
    [InlineData("has\"a quote")]
    [InlineData("plain")]
    public void CommandLine_RoundTripsThroughTheParserTheChildActuallyUses(string tricky)
    {
        if (!OperatingSystem.IsWindows()) return;

        var line = DetachedProcess.CommandLine(@"C:\Program Files\x\conductor.exe",
            ["run", "-p", tricky, "--headless"]);

        var back = Win32Argv(line);
        Assert.Equal(@"C:\Program Files\x\conductor.exe", back[0]);
        Assert.Equal("run", back[1]);
        Assert.Equal("-p", back[2]);
        Assert.Equal(tricky, back[3]);
        Assert.Equal("--headless", back[4]);
    }

    // ------------------------------------------------------------------ a real detached spawn

    [Fact]
    public void Start_SpawnsALiveProcessAndCapturesItsOutput()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = NewTempDir();
        try
        {
            var log = Path.Combine(dir, "detach.log");
            var spawn = DetachedProcess.Start(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["/c", "echo", "detached-child-lives"],
                dir,
                log);

            Assert.True(spawn.Ok, spawn.Error ?? "spawn reported failure");
            Assert.True(spawn.Pid > 0);

            // A detached process has NO console: without the inherited handle this file stays empty
            // and a child that dies at startup dies in silence.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            string text = "";
            while (DateTime.UtcNow < deadline)
            {
                text = ReadShared(log);
                if (text.Contains("detached-child-lives", StringComparison.Ordinal)) break;
                Thread.Sleep(100);
            }
            Assert.Contains("detached-child-lives", text, StringComparison.Ordinal);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Start_RefusesAnEmptyProgramInsteadOfThrowing()
    {
        var spawn = DetachedProcess.Start("", [], Path.GetTempPath());
        Assert.False(spawn.Ok);
        Assert.NotNull(spawn.Error);
    }

    // ------------------------------------------------------------------ the handshake

    [Fact]
    public void ReadDiscovery_YieldsTheChildsOwnBoundUrl()
    {
        var dir = NewTempDir();
        try
        {
            var path = ControlPlaneDiscovery.PathFor(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(
                new ControlPlaneInfo(4322, "http://127.0.0.1:4322", 4242, "rig", DateTime.UtcNow, "tok"),
                ControlPlaneJsonContext.Default.ControlPlaneInfo));

            var info = RunDetach.ReadDiscovery(path);
            Assert.NotNull(info);
            Assert.Equal("http://127.0.0.1:4322", info!.BaseUrl);
            // The port the engine BOUND, which is not necessarily the port it preferred.
            Assert.True(RunDetach.IsOurs(info, 4242));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void IsOurs_RejectsAStaleFileFromAPreviousRunOfTheSamePlan()
    {
        // The whole hazard: it parses, it holds a plausible URL, and it belongs to a dead engine.
        var stale = new ControlPlaneInfo(4317, "http://127.0.0.1:4317", 1111, "rig", DateTime.UtcNow.AddDays(-1));
        Assert.False(RunDetach.IsOurs(stale, childPid: 2222));
        Assert.False(RunDetach.IsOurs(null, childPid: 2222));
    }

    [Fact]
    public void ReadDiscovery_SurvivesAMissingOrHalfWrittenFile()
    {
        var dir = NewTempDir();
        try
        {
            var path = ControlPlaneDiscovery.PathFor(dir);
            Assert.Null(RunDetach.ReadDiscovery(path));
            File.WriteAllText(path, "{\"port\":43");   // the child, caught mid-write
            Assert.Null(RunDetach.ReadDiscovery(path));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void RunLogPath_IsTheFileTheEngineActuallyWrites()
    {
        // Found by the live rig, not by a test: the first draft pointed the banner at
        // <stateDir>/logs/conductor.log, which does not exist — logs/ holds the dated rotation and
        // the per-session streams. RunContext is where the run log's path is decided, and this pins
        // the banner to it so the two cannot drift back apart.
        Assert.Equal(Path.Combine(@"X:\rig\.conductor", "conductor.log"), RunDetach.RunLogPath(@"X:\rig\.conductor"));

        var runContext = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Conductor.Core", "Orchestration", "RunContext.cs"));
        Assert.Contains("LogPath = Path.Combine(plan.StateDir, \"conductor.log\")", runContext, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSelf_PointsAtSomethingThatExists()
    {
        var (exe, prefix, error) = RunDetach.ResolveSelf();
        Assert.Null(error);
        Assert.True(File.Exists(exe), $"resolved engine path does not exist: {exe}");
        foreach (var p in prefix) Assert.True(File.Exists(p), $"resolved prefix arg does not exist: {p}");
    }

    // ------------------------------------------------------------------ the stall warning

    [Fact]
    public void StallWarning_NamesTheLikelyCauseAndTheRemedy()
    {
        var now = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
        var quiet = now.AddHours(-1);
        var mono = TimeSpan.Zero;
        var lines = new List<string>();
        using var watchdog = new SessionWatchdog(
            hardTimeout: TimeSpan.FromHours(9),
            stallThreshold: TimeSpan.FromMinutes(10),
            stallGrace: TimeSpan.FromMinutes(2),
            sample: () => new WatchdogSignals(quiet, quiet, AnyBgProcessAlive: false),
            onAction: (_, m) => lines.Add(m),
            monotonic: () => mono,
            wallClock: () => now);

        var (first, graceMsg) = watchdog.Tick();
        now = now.AddMinutes(5);       // wall and monotonic move together: no clock jump
        mono += TimeSpan.FromMinutes(5);
        var (second, killMsg) = watchdog.Tick();

        Assert.Equal(WatchdogAction.StallGraceStarted, first);
        Assert.Equal(WatchdogAction.StallKill, second);

        // Both ends of the rail must be actionable — the grace line is the one an operator can still
        // act on, and the kill line is what the NEXT attempt's prompt carries.
        foreach (var msg in new[] { graceMsg, killMsg })
        {
            Assert.Contains("FOREGROUND", msg, StringComparison.Ordinal);
            Assert.Contains("conductor bg start", msg, StringComparison.Ordinal);
        }
        // The pre-existing contract other gates read stays intact.
        Assert.Contains("soft-kill grace window started", graceMsg, StringComparison.Ordinal);
        Assert.StartsWith("stall: grace window expired", killMsg, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc52-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch (IOException) { return ""; }
    }

    /// <summary>Split a command line with the Win32 parser — the same one the child's own runtime
    /// calls to build its argv. Asserting against our own quoter would prove nothing.</summary>
    private static string[] Win32Argv(string commandLine)
    {
        var ptr = CommandLineToArgvW(commandLine, out var count);
        Assert.NotEqual(IntPtr.Zero, ptr);
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(ptr); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
