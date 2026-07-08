using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class B10_3HooksTests
{
    // ── Model: StageConfig deserialization ─────────────────────────────

    [Fact]
    public void DeserializesPreHookFromJson()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "e", "args": ["{prompt}"] },
          "stages": [
            { "id": "S1", "title": "S1", "preHook": { "command": "echo setup", "cwd": "src", "timeoutMinutes": 5 } }
          ]
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
        var hook = cfg.Stages[0].PreHook;
        Assert.NotNull(hook);
        Assert.Equal("echo setup", hook.Command);
        Assert.Equal("src", hook.Cwd);
        Assert.Equal(5, hook.TimeoutMinutes);
    }

    [Fact]
    public void DeserializesPostHookFromJson()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "e", "args": ["{prompt}"] },
          "stages": [
            { "id": "S1", "title": "S1", "postHook": { "command": "echo done" } }
          ]
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
        var hook = cfg.Stages[0].PostHook;
        Assert.NotNull(hook);
        Assert.Equal("echo done", hook.Command);
    }

    [Fact]
    public void PreHookIsNullWhenOmitted()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "e", "args": ["{prompt}"] },
          "stages": [
            { "id": "S1", "title": "S1" }
          ]
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Null(cfg.Stages[0].PreHook);
        Assert.Null(cfg.Stages[0].PostHook);
    }

    [Fact]
    public void HookConfigDefaults()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "e", "args": ["{prompt}"] },
          "stages": [
            { "id": "S1", "title": "S1", "preHook": { "command": "echo x" } }
          ]
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
        var hook = cfg.Stages[0].PreHook;
        Assert.NotNull(hook);
        Assert.Equal("echo x", hook.Command);
        Assert.Null(hook.Cwd);              // default null
        Assert.Equal(3, hook.TimeoutMinutes); // default 3
    }

    // ── RunState: PreHookRunStages serialization ───────────────────────

    [Fact]
    public void RunStateSerializesPreHookRunStages()
    {
        var state = new RunState
        {
            PreHookRunStages = new List<string> { "B10", "B11" }
        };
        var json = JsonSerializer.Serialize(state, PlanConfig.JsonOpts);
        Assert.Contains("preHookRunStages", json);
        Assert.Contains("B10", json);
        Assert.Contains("B11", json);
    }

    [Fact]
    public void RunStateDeserializesPreHookRunStages()
    {
        const string json = """
        {"preHookRunStages":["A","B"]}
        """;
        var state = JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts)!;
        Assert.Equal(2, state.PreHookRunStages.Count);
        Assert.Contains("A", state.PreHookRunStages);
        Assert.Contains("B", state.PreHookRunStages);
    }

    [Fact]
    public void RunStatePreHookRunStagesEmptyByDefault()
    {
        var state = new RunState();
        Assert.NotNull(state.PreHookRunStages);
        Assert.Empty(state.PreHookRunStages);
    }

    // ── Hook execution semantics (via ProcessRunner) ───────────────────

    [Fact]
    public void HookRunsSuccessfully()
    {
        var hook = new HookConfig { Command = "echo hello", TimeoutMinutes = 5 };
        var cwd = Path.GetTempPath();
        var r = ProcessRunner.RunPowerShell(hook.Command, cwd, TimeSpan.FromMinutes(1));
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("hello", r.Output);
    }

    [Fact]
    public void HookReturnsNonZeroOnFailure()
    {
        var hook = new HookConfig { Command = "exit 42", TimeoutMinutes = 5 };
        var cwd = Path.GetTempPath();
        var r = ProcessRunner.RunPowerShell(hook.Command, cwd, TimeSpan.FromMinutes(1));
        Assert.Equal(42, r.ExitCode);
    }

    [Fact]
    public void HookTimeoutIsCaptured()
    {
        // A hook that sleeps past its timeout should report timed out.
        var hook = new HookConfig { Command = "Start-Sleep -Seconds 60", TimeoutMinutes = 0 };
        var cwd = Path.GetTempPath();
        // Use a very short timeout; the process will be killed and timedOut=true when
        // ProcessRunner fails to start it inside the timeout. cts-based cancellation
        // is the cleaner path — exit code may be -1 or non-zero.
        var r = ProcessRunner.RunPowerShell(hook.Command, cwd, TimeSpan.FromMilliseconds(500));
        Assert.NotEqual(0, r.ExitCode);
    }

    [Fact]
    public void FailingHookCapturesStdout()
    {
        // A hook that prints to stdout then fails — the output must be captured
        // for diagnostic purposes (the orchestrator's RunStageHook includes it in the error log).
        var hook = new HookConfig { Command = "Write-Host 'setup failed: missing dep'; exit 1", TimeoutMinutes = 5 };
        var cwd = Path.GetTempPath();
        var r = ProcessRunner.RunPowerShell(hook.Command, cwd, TimeSpan.FromMinutes(1));
        Assert.NotEqual(0, r.ExitCode);
        Assert.Contains("setup failed", r.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreHookRunStagesEmptyAfterDeserializingFailureState()
    {
        // Simulates the resume scenario: a RunState was serialized while the pre-hook had NOT
        // succeeded (PreHookRunStages is empty → on resume the hook retries). This must round-trip
        // correctly — an empty list must not materialize as null.
        const string json = """{"preHookRunStages":[]}""";
        var state = JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts)!;
        Assert.NotNull(state.PreHookRunStages);
        Assert.Empty(state.PreHookRunStages);
    }

    [Fact]
    public void PreHookRunStagesContainsOnlySuccessStagesAfterRoundTrip()
    {
        // A RunState with PreHookRunStages containing stage ids — these survived a pre-hook success
        // and must round-trip so resume doesn't re-run them.
        var state = new RunState
        {
            PreHookRunStages = new List<string> { "B10", "B12" }
        };
        var json = JsonSerializer.Serialize(state, PlanConfig.JsonOpts);
        var restored = JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts)!;
        Assert.Equal(2, restored.PreHookRunStages.Count);
        Assert.Contains("B10", restored.PreHookRunStages);
        Assert.Contains("B12", restored.PreHookRunStages);
    }
}
