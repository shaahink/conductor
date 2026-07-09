using Conductor.Core;
using Conductor.Core.Planning;
using Conductor.Models;
using Xunit;

namespace Conductor.Tests;

public sealed class C2AsyncEngineTests
{
    // ---------------------------------------------------------------- CancellationToken plumbing

    [Fact]
    public void ScriptProvider_RespectsCancellationToken_CancelsProcess()
    {
        var config = new ScriptProviderConfig
        {
            Command = "Start-Sleep -Seconds 30; Write-Output '[{\"id\":\"X\",\"status\":\"DONE\"}]'",
            TimeoutMinutes = 5,
        };
        var provider = new ScriptProvider(config);
        var plan = new PlanConfig { Repo = Path.GetTempPath() };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var ex = Assert.Throws<OperationCanceledException>(() => provider.Read(plan, cts.Token));
        Assert.Contains("cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public void ScriptProvider_CancelledBeforeStart_ThrowsOperationCanceled()
    {
        var config = new ScriptProviderConfig
        {
            Command = "echo never-runs",
            TimeoutMinutes = 1,
        };
        var provider = new ScriptProvider(config);
        var plan = new PlanConfig { Repo = Path.GetTempPath() };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => provider.Read(plan, cts.Token));
    }

    // ---------------------------------------------------------------- stdout/stderr split

    [Fact]
    public void ProcessRunner_SplitsStdoutAndStderr()
    {
        var script = "Write-Output 'hello stdout'; Write-Error 'hello stderr'; exit 0";
        var result = ProcessRunner.RunPowerShell(script, Path.GetTempPath(), TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello stdout", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hello stderr", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessRunner_StderrDoesNotCorruptStdout()
    {
        // A process that writes JSON to stdout and warnings to stderr
        var script = """
            Write-Error "stderr warning: this should not be in JSON"
            Write-Output '[{"id":"test-1","status":"DONE"}]'
            exit 0
            """;

        var result = ProcessRunner.RunPowerShell(script, Path.GetTempPath(), TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        // Stdout should contain only the JSON, not the stderr warning
        Assert.Contains("[{\"id\":\"test-1\"", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr warning", result.Output, StringComparison.Ordinal);
        // Stderr should have the warning
        Assert.Contains("stderr warning", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptProvider_JsonInStdoutNotCorruptedByStderr()
    {
        var script = """
            Write-Error "this is a warning on stderr"
            Write-Output '[{"id":"S-1","title":"Check stderr resilience","status":"DONE"}]'
            exit 0
            """;

        var config = new ScriptProviderConfig
        {
            Command = script,
            TimeoutMinutes = 1,
        };
        var provider = new ScriptProvider(config);
        var plan = new PlanConfig { Repo = Path.GetTempPath() };

        var snap = provider.Read(plan);
        Assert.Single(snap.Checkpoints);
        Assert.Equal("S-1", snap.Checkpoints[0].Id);
        Assert.True(snap.Checkpoints[0].IsDone);
    }

    // ---------------------------------------------------------------- ProcResult backward compat

    [Fact]
    public void ProcResult_DefaultStderrIsEmpty()
    {
        var result = new ProcResult(0, "ok", "", false, TimeSpan.Zero);
        Assert.Equal("", result.StdErr);
    }

    [Fact]
    public void ProcResult_ConstructorPreservesAllFields()
    {
        var dur = TimeSpan.FromMilliseconds(1500);
        var result = new ProcResult(42, "main output", "error output", true, dur);

        Assert.Equal(42, result.ExitCode);
        Assert.Equal("main output", result.Output);
        Assert.Equal("error output", result.StdErr);
        Assert.True(result.TimedOut);
        Assert.Equal(dur, result.Duration);
    }

    // ---------------------------------------------------------------- CancellationToken in providers

    [Fact]
    public void MarkdownTableProvider_AcceptsCancellationToken()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                ## Handoff
                all clear

                | # | Checkpoint | Status | Commit | Evidence |
                |---|-----------|--------|--------|----------|
                | T1 | Test checkpoint | DONE | abc | log.txt |
                """);

            var plan = new PlanConfig { Repo = Path.GetTempPath(), Tracker = path };
            var provider = new MarkdownTableProvider();

            using var cts = new CancellationTokenSource();
            var snap = provider.Read(plan, cts.Token);
            Assert.Single(snap.Checkpoints);
            Assert.Equal("T1", snap.Checkpoints[0].Id);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void PlanCheckpointProvider_AcceptsCancellationToken()
    {
        var checkpoints = new List<PlanCheckpoint>
        {
            new() { Id = "C1", Status = "DONE" },
        };
        var provider = new PlanCheckpointProvider(checkpoints);
        var plan = new PlanConfig { Repo = Path.GetTempPath() };

        using var cts = new CancellationTokenSource();
        var snap = provider.Read(plan, cts.Token);
        Assert.Single(snap.Checkpoints);
    }

    // ---------------------------------------------------------------- IProgressProvider contract

    [Fact]
    public void IProgressProvider_DefaultCancellationToken_IsNone()
    {
        // Verify that the default parameter works — providers with no arg should compile and run
        var provider = new PlanCheckpointProvider(new List<PlanCheckpoint>());
        var plan = new PlanConfig { Repo = Path.GetTempPath() };
        var snap = provider.Read(plan); // no CT arg — uses default (None)
        Assert.Empty(snap.Checkpoints);
    }
}
