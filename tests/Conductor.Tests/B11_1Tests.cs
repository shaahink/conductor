using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class B11_1CrossPlatformShellTests
{
    // --- ProcessRunner.RunShell dispatch ---

    [Fact]
    public void RunShell_PowerShell_ExitZero_CapturesExitCode()
    {
        var r = ProcessRunner.RunShell("powershell", "exit 0", Path.GetTempPath(),
            TimeSpan.FromMinutes(1));
        Assert.Equal(0, r.ExitCode);
        Assert.False(r.TimedOut);
    }

    [Fact]
    public void RunShell_PowerShell_ExitNonZero_CapturesExitCodeAndOutput()
    {
        var r = ProcessRunner.RunShell("powershell", "Write-Output 'the-failure'; exit 7",
            Path.GetTempPath(), TimeSpan.FromMinutes(1));
        Assert.Equal(7, r.ExitCode);
        Assert.Contains("the-failure", r.Output);
    }

    [Fact]
    public void RunShell_Bash_ExitZero_CapturesExitCode()
    {
        if (!BashAvailable()) return;

        var r = ProcessRunner.RunShell("bash", "echo hello && exit 0",
            Path.GetTempPath(), TimeSpan.FromMinutes(1));
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("hello", r.Output);
    }

    [Fact]
    public void RunShell_Bash_ExitNonZero_CapturesExitCode()
    {
        if (!BashAvailable()) return;

        var r = ProcessRunner.RunShell("bash", "echo error-msg && exit 42",
            Path.GetTempPath(), TimeSpan.FromMinutes(1));
        Assert.Equal(42, r.ExitCode);
        Assert.Contains("error-msg", r.Output);
    }

    [Fact]
    public void RunShell_Bash_StderrCaptured()
    {
        if (!BashAvailable()) return;

        var r = ProcessRunner.RunShell("bash", "echo stderr-message >&2 && exit 1",
            Path.GetTempPath(), TimeSpan.FromMinutes(1));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("stderr-message", r.StdErr);
    }

    [Fact]
    public void RunShell_Sh_ExitZero_CapturesExitCode()
    {
        if (!ShellAvailable("sh")) return;

        var r = ProcessRunner.RunShell("sh", "echo from-sh && exit 0",
            Path.GetTempPath(), TimeSpan.FromMinutes(1));
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("from-sh", r.Output);
    }

    [Fact]
    public void RunShell_UnknownShell_ReturnsError()
    {
        var r = ProcessRunner.RunShell("zsh", "exit 0", Path.GetTempPath(),
            TimeSpan.FromMinutes(1));
        Assert.Equal(-1, r.ExitCode);
        Assert.Contains("unknown shell", r.Output);
    }

    // --- Default shell detection ---

    [Fact]
    public void DefaultShell_OnWindows_IsPowerShell()
    {
        if (OperatingSystem.IsWindows())
            Assert.Equal("powershell", ProcessRunner.DefaultShell);
        else
            Assert.Equal("bash", ProcessRunner.DefaultShell);
    }

    // --- RunPowerShell delegates to RunShell (regression) ---

    [Fact]
    public void RunPowerShell_StillWorks_AndExitsZero()
    {
        var r = ProcessRunner.RunPowerShell("exit 0", Path.GetTempPath(),
            TimeSpan.FromMinutes(1));
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void RunPowerShell_StillWorks_AndPropagatesExitCode()
    {
        var r = ProcessRunner.RunPowerShell("exit 9", Path.GetTempPath(),
            TimeSpan.FromMinutes(1));
        Assert.Equal(9, r.ExitCode);
    }

    // --- GateConfig Shell property deserialization ---

    [Fact]
    public void GateConfig_DeserializesShellProperty()
    {
        const string json = """
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "e", "args": ["{prompt}"] },
          "stages": [{ "id": "S", "title": "T", "sessions": 1 }],
          "gates": [
            { "name": "powershell-gate", "command": "exit 0" },
            { "name": "bash-gate", "command": "echo hi", "shell": "bash" }
          ]
        }
        """;
        var cfg = System.Text.Json.JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
        Assert.Null(cfg.Gates[0].Shell);   // null → auto-detect
        Assert.Equal("bash", cfg.Gates[1].Shell);
    }

    // --- GateRunner.RunOne uses Shell ---

    [Fact]
    public void GateRunner_RunOne_DefaultShell_GatePasses()
    {
        var plan = new PlanConfig
        {
            Repo = Path.GetTempPath(),
            Gates = new List<GateConfig>
            {
                new() { Name = "nop", Command = "exit 0", TimeoutMinutes = 1 }
            }
        };
        var results = GateRunner.RunAll(plan);
        Assert.True(results[0].Passed);
        Assert.Equal(0, results[0].ExitCode);
    }

    [Fact]
    public void GateRunner_RunOne_BashShell_GateCapturesExitCode()
    {
        if (!BashAvailable()) return;

        var plan = new PlanConfig
        {
            Repo = Path.GetTempPath(),
            Gates = new List<GateConfig>
            {
                new()
                {
                    Name = "bash-gate",
                    Command = "echo gate-output && exit 3",
                    Shell = "bash",
                    TimeoutMinutes = 1,
                }
            }
        };
        var results = GateRunner.RunAll(plan);
        Assert.False(results[0].Passed);
        Assert.Equal(3, results[0].ExitCode);
        Assert.Contains("gate-output", results[0].Tail);
    }

    [Fact]
    public void GateRunner_RunOne_SkipsMissingShellGracefully()
    {
        // bash may not be on Windows CI; RunShell returns an error result with exit -1
        // if the shell executable isn't found. SkipIfMissing doesn't apply here,
        // but the gate should still produce a result (not crash).
        var shell = BashAvailable() ? "bash" : "nonexistent-shell-xyz";
        var plan = new PlanConfig
        {
            Repo = Path.GetTempPath(),
            Gates = new List<GateConfig>
            {
                new()
                {
                    Name = "maybe-missing",
                    Command = "exit 0",
                    Shell = shell,
                    TimeoutMinutes = 1,
                }
            }
        };
        var results = GateRunner.RunAll(plan);
        Assert.Single(results);
        // It won't crash — either passes (bash available) or returns a result.
        Assert.NotNull(results[0]);
    }

    [Fact]
    public void GateRunner_BatterySignature_UnchangedByShellProperty()
    {
        var a = new PlanConfig
        {
            Repo = ".",
            Gates = new List<GateConfig>
            {
                new() { Name = "build", Command = "dotnet build" }
            }
        };
        var b = new PlanConfig
        {
            Repo = ".",
            Gates = new List<GateConfig>
            {
                new() { Name = "build", Command = "dotnet build", Shell = "powershell" }
            }
        };
        var sigA = GateRunner.BatterySignature(a, "abc123", null);
        var sigB = GateRunner.BatterySignature(b, "abc123", null);
        Assert.Equal(sigA, sigB); // signatures are based on gate names, not shell
    }

    // --- Helper ---

    private static bool BashAvailable()
    {
        try
        {
            var r = ProcessRunner.Run("bash", new[] { "--version" },
                Path.GetTempPath(), TimeSpan.FromSeconds(5));
            return r.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ShellAvailable(string shell)
    {
        if (shell is "bash") return BashAvailable();
        try
        {
            var r = ProcessRunner.Run(shell, new[] { "-c", "exit 0" },
                Path.GetTempPath(), TimeSpan.FromSeconds(5));
            return r.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
