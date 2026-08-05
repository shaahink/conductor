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
        try { TestTemp.DeleteTree(_dir); } catch (Exception) { /* best effort */ }
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

    /// <summary>W3.3: an uncapped run used to report "ok — unbounded", which made a default nobody
    /// chose look like a clean bill of health. The U-series run had no cap and spent $139.68.</summary>
    [Fact]
    public void CheckBudget_Warns_WhenNoCapConfigured()
    {
        var check = DoctorCommand.CheckBudget(Plan(), currentCostUsd: 999m, hasRun: true);
        Assert.Equal("warn", check.State);
        Assert.Contains("no spend cap", check.Message, StringComparison.Ordinal);
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

    // --- gates (U0.3) ---

    [Fact]
    public void CheckGates_Warn_WhenNoneConfigured()
    {
        var check = DoctorCommand.CheckGates(Plan()); // Plan() leaves Gates empty
        Assert.Equal("warn", check.State);
        Assert.Contains("none configured", check.Message, StringComparison.Ordinal);
        Assert.Contains("trust commits + tracker only", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckGates_Ok_WhenConfigured()
    {
        var plan = Plan(p => p.Gates.Add(new GateConfig { Name = "build", Command = "echo ok", Tier = "fast" }));
        var check = DoctorCommand.CheckGates(plan);
        Assert.Equal("ok", check.State);
        Assert.Contains("1 configured", check.Message, StringComparison.Ordinal);
    }

    // --- model token (SC3.1) ---
    // The trap: AgentSession.ResolveArgs only substitutes where the template already says {model},
    // so a pinned model with no placeholder runs the CLI's own default while every surface keeps
    // reporting the pinned one. These assert the fail is a FAIL — a warn is not enough for a defect
    // nothing downstream can detect.

    /// <summary>The premise, measured against the substitution the engine actually performs rather
    /// than taken from the field note: with a model pinned and no placeholder in the template, the
    /// model appears NOWHERE in the spawned argv — the CLI picks its own default and the plan file,
    /// journey and /state all keep saying otherwise. This is what the doctor check above exists to
    /// catch, so if this behaviour ever changes the check should be revisited, not the test.</summary>
    [Fact]
    public void ResolveArgs_DropsThePinnedModel_WhenTheTemplateHasNoPlaceholder()
    {
        var withoutToken = AgentSession.ResolveArgs(["-p", "{prompt}"], "PROMPT", "sess-1", null, "claude-opus-5");
        Assert.DoesNotContain(withoutToken, a => a.Contains("opus", StringComparison.OrdinalIgnoreCase));

        var withToken = AgentSession.ResolveArgs(["-p", "{prompt}", "--model", "{model}"], "PROMPT", "sess-1", null, "claude-opus-5");
        Assert.Contains("claude-opus-5", withToken, StringComparer.Ordinal);
    }

    private static PlanConfig ModelPlan(AgentConfig agent, Action<PlanConfig>? configure = null) => Plan(p =>
    {
        p.Agent = agent;
        p.Stages.Add(new StageConfig { Id = "S1", Title = "one", Sessions = 1 });
        configure?.Invoke(p);
    });

    [Fact]
    public void CheckModelToken_Fail_WhenModelPinnedButArgsHaveNoPlaceholder()
    {
        var plan = ModelPlan(new AgentConfig { Command = "cmd", Args = ["-p", "{prompt}"], Model = "claude-opus-5" });
        var check = DoctorCommand.CheckModelToken(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("plan.agent.model", check.Message, StringComparison.Ordinal);
        Assert.Contains("args for stage(s) [S1]", check.Message, StringComparison.Ordinal);
        Assert.Contains("CLI's default model", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckModelToken_Fail_WhenResumeArgsAloneHaveNoPlaceholder()
    {
        // The harder half: the first session honours the pinned model, and only the RESUMED one
        // silently switches — AgentSession.Start swaps templates on resume.
        var plan = ModelPlan(new AgentConfig
        {
            Command = "cmd",
            Args = ["-p", "{prompt}", "--model", "{model}"],
            ResumeArgs = ["-p", "{prompt}", "--resume", "{claudeSessionId}"],
            Model = "claude-opus-5",
        });
        var check = DoctorCommand.CheckModelToken(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("resumeArgs for stage(s) [S1]", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("args for stage(s)", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckModelToken_Fail_WhenOnlyAStageOverridePinsTheModel()
    {
        var plan = ModelPlan(new AgentConfig { Command = "cmd", Args = ["-p", "{prompt}"] });
        plan.Stages[0].Agent = new AgentConfig { Model = "claude-haiku-4-5-20251001" };
        var check = DoctorCommand.CheckModelToken(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("stage 'S1' agent.model", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckModelToken_Fail_WhenARoleRulePinsTheModel()
    {
        // pipeline.roles.*.model reaches the same substitution via SessionRunner, so it is a pin too.
        var plan = ModelPlan(new AgentConfig { Command = "cmd", Args = ["-p", "{prompt}"] }, p =>
            p.Pipeline = new PipelineRules
            {
                Roles = new Dictionary<string, RoleAgentRule>(StringComparer.Ordinal)
                {
                    ["audit"] = new RoleAgentRule { Model = "claude-opus-5" },
                },
            });
        var check = DoctorCommand.CheckModelToken(plan);
        Assert.Equal("fail", check.State);
        Assert.Contains("pipeline.roles.audit.model", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckModelToken_Ok_WhenBothTemplatesCarryThePlaceholder()
    {
        var plan = ModelPlan(new AgentConfig
        {
            Command = "cmd",
            Args = ["-p", "{prompt}", "--model", "{model}"],
            ResumeArgs = ["--resume", "{claudeSessionId}", "--model", "{model}", "{prompt}"],
            Model = "claude-opus-5",
        });
        var check = DoctorCommand.CheckModelToken(plan);
        Assert.Equal("ok", check.State);
    }

    [Fact]
    public void CheckModelToken_Ok_WhenNoModelIsPinnedAtAll()
    {
        var plan = ModelPlan(new AgentConfig { Command = "cmd", Args = ["-p", "{prompt}"] });
        var check = DoctorCommand.CheckModelToken(plan);
        Assert.Equal("ok", check.State);
        Assert.Contains("no model pinned", check.Message, StringComparison.Ordinal);
    }

    /// <summary>The plans this repo ships are the worked examples; a rule they trip is a rule that
    /// would have to be explained away rather than fixed.</summary>
    [Fact]
    public void ShippedPlans_PassTheModelTokenCheck()
    {
        string? plansDir = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "plans");
            if (Directory.Exists(candidate)) { plansDir = candidate; break; }
        }
        if (plansDir == null) return; // not in a full checkout — soft skip

        foreach (var file in Directory.EnumerateFiles(plansDir, "*.plan.json"))
        {
            var cfg = System.Text.Json.JsonSerializer.Deserialize<PlanConfig>(File.ReadAllText(file), PlanConfig.JsonOpts);
            if (cfg is null) continue;
            var check = DoctorCommand.CheckModelToken(cfg);
            Assert.True(check.State != "fail", $"{Path.GetFileName(file)}: {check.Message}");
        }
    }
}
