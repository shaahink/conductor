using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>M8.1: <c>conductor doctor</c> repurposed in place into a &lt;2s health check. These
/// drive the same internal <c>DoctorCommand.Check*</c> methods <c>Execute</c> calls, deliberately
/// breaking the environment per checkpoint the way the design doc's truth gate asks for, and
/// assert the exact failing/warning lines — no Spectre rendering or CLI plumbing involved.</summary>
public sealed class DoctorCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-doctor-{Guid.NewGuid():N}");

    public DoctorCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Broad catch: git leaves some object files read-only on Windows, which surfaces as
        // UnauthorizedAccessException here rather than IOException (matches HarnessTests' cleanup).
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private static PlanConfig Plan(Action<PlanConfig>? configure = null)
    {
        var plan = new PlanConfig { Name = "doctor-test", Repo = Path.GetTempPath(), Tracker = "TRACKER.md" };
        configure?.Invoke(plan);
        return plan;
    }

    // --- agent CLI ---

    [Fact]
    public void CheckAgentCli_Ok_WhenCommandResolvesOnPath()
    {
        // git is a hard dependency of this whole tool, so it's a safe "definitely on PATH" probe.
        var plan = Plan(p => p.Agent = new AgentConfig { Command = "git" });
        var check = DoctorCommand.CheckAgentCli(plan);
        Assert.Equal("ok", check.State);
    }

    [Fact]
    public void CheckAgentCli_Fail_WhenCommandNotFound()
    {
        var plan = Plan(p => p.Agent = new AgentConfig { Command = "definitely-not-a-real-command-xyz123" });
        var check = DoctorCommand.CheckAgentCli(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("not found on PATH", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckAgentCli_Fail_WhenEmpty()
    {
        var plan = Plan(p => p.Agent = new AgentConfig { Command = "" });
        var check = DoctorCommand.CheckAgentCli(plan);
        Assert.Equal("fail", check.State);
    }

    [Fact]
    public void CheckAgentCli_Ok_WhenAbsolutePathExists()
    {
        var realFile = typeof(DoctorCommandTests).Assembly.Location;
        var plan = Plan(p => p.Agent = new AgentConfig { Command = realFile });
        var check = DoctorCommand.CheckAgentCli(plan);
        Assert.Equal("ok", check.State);
    }

    [Fact]
    public void CheckAgentCli_Fail_WhenAbsolutePathMissing()
    {
        var plan = Plan(p => p.Agent = new AgentConfig { Command = Path.Combine(_dir, "nope.exe") });
        var check = DoctorCommand.CheckAgentCli(plan);
        Assert.Equal("fail", check.State);
    }

    // --- git ---

    [Fact]
    public void CheckGit_Fail_WhenRepoPathDoesNotExist()
    {
        var plan = Plan(p => p.Repo = Path.Combine(_dir, "does-not-exist"));
        var check = DoctorCommand.CheckGit(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("does not exist", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckGit_Ok_WhenRepoCleanOnBranch()
    {
        InitRepo(_dir);
        var plan = Plan(p => p.Repo = _dir);
        var check = DoctorCommand.CheckGit(plan);
        Assert.Equal("ok", check.State);
        Assert.Contains("main", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckGit_Warn_WhenRepoDirty()
    {
        InitRepo(_dir);
        File.WriteAllText(Path.Combine(_dir, "dirty.txt"), "uncommitted");
        var plan = Plan(p => p.Repo = _dir);
        var check = DoctorCommand.CheckGit(plan);
        Assert.Equal("warn", check.State);
    }

    private static void InitRepo(string dir)
    {
        Run(dir, "init -b main");
        Run(dir, "config user.email doctor@test");
        Run(dir, "config user.name \"Doctor Test\"");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# t");
        Run(dir, "add README.md");
        Run(dir, "commit -m init --no-gpg-sign");
    }

    private static void Run(string dir, string args) =>
        ProcessRunner.Run("git", args.Split(' ', StringSplitOptions.RemoveEmptyEntries), dir, TimeSpan.FromSeconds(30), CancellationToken.None);

    // --- budget ---

    [Fact]
    public void CheckBudget_Ok_WhenNoCapConfigured()
    {
        var check = DoctorCommand.CheckBudget(Plan(), currentCostUsd: 999m, hasRun: true);
        Assert.Equal("ok", check.State);
        Assert.Contains("unbounded", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckBudget_Ok_WhenNoRunYet()
    {
        var plan = Plan(p => p.Limits.MaxRunCostUsd = 10m);
        var check = DoctorCommand.CheckBudget(plan, currentCostUsd: 0m, hasRun: false);
        Assert.Equal("ok", check.State);
    }

    [Fact]
    public void CheckBudget_Warn_WhenApproachingCap()
    {
        var plan = Plan(p => p.Limits.MaxRunCostUsd = 10m);
        var check = DoctorCommand.CheckBudget(plan, currentCostUsd: 9m, hasRun: true); // 90%
        Assert.Equal("warn", check.State);
    }

    [Fact]
    public void CheckBudget_Fail_WhenAtOrOverCap()
    {
        var plan = Plan(p => p.Limits.MaxRunCostUsd = 10m);
        var check = DoctorCommand.CheckBudget(plan, currentCostUsd: 10m, hasRun: true);
        Assert.Equal("fail", check.State);
    }

    // --- telegram ---

    [Fact]
    public void CheckTelegram_Warn_WhenNotConfigured()
    {
        var check = DoctorCommand.CheckTelegram(Plan());
        Assert.Equal("warn", check.State);
        Assert.Contains("not configured", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckTelegram_Warn_WhenConfiguredButNoToken()
    {
        var plan = Plan(p => { p.Repo = _dir; p.Telegram = new TelegramConfig(); });
        var check = DoctorCommand.CheckTelegram(plan);
        Assert.Equal("warn", check.State);
        Assert.Contains("no bot token", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckTelegram_Warn_WhenTokenPresentButNoAllowedChatIds()
    {
        var plan = Plan(p => { p.Repo = _dir; p.Telegram = new TelegramConfig(); });
        SecretsStore.WriteTelegramToken(plan.StateDir, "fake-token");
        var check = DoctorCommand.CheckTelegram(plan);
        Assert.Equal("warn", check.State);
        Assert.Contains("allowedChatIds", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckTelegram_Ok_WhenTokenAndChatIdsPresent()
    {
        var plan = Plan(p =>
        {
            p.Repo = _dir;
            p.Telegram = new TelegramConfig { AllowedChatIds = ["12345"] };
        });
        SecretsStore.WriteTelegramToken(plan.StateDir, "fake-token");
        var check = DoctorCommand.CheckTelegram(plan);
        Assert.Equal("ok", check.State);
    }

    // --- face ---

    [Fact]
    public void CheckFace_NeverThrows_AndReturnsOkOrWarn()
    {
        var check = DoctorCommand.CheckFace();
        Assert.True(check.State is "ok" or "warn");
    }
}
