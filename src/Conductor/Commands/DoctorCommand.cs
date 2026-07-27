using System.Diagnostics;
using Conductor.Core;
using Conductor.Core.Face;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// M8.1 — a &lt;2s health check that says exactly what is missing before a run: agent CLI present,
/// model/API reachable, face-go binary built, git clean, disk, DNS, budget headroom, Telegram
/// configured. Repurposed in place (owner decision, M8-PRESESSION.md) from the pre-M2 "what
/// happens on resume" preview, which read the now-deleted <c>state.json</c> and showed stale/empty
/// state — <c>conductor status</c> already covers current-run verdicts from the database. No LLM
/// call; the only network is the cheap reachability probes <see cref="PreflightHealth"/> already
/// does with a 10s timeout each. Read-only; never writes state.
/// </summary>
public sealed class DoctorCommand : AsyncCommand<PlanSettings>
{
    internal sealed record Check(string Name, string State, string Message); // State: ok | warn | fail

    public override async Task<int> ExecuteAsync(CommandContext context, PlanSettings settings)
    {
        var sw = Stopwatch.StartNew();
        var plan = PlanConfig.Load(settings.ResolvePlanPath());

        AnsiConsole.MarkupLine($"[bold aqua]conductor doctor[/] — {Markup.Escape(plan.Name)}");
        AnsiConsole.MarkupLine($"repo: {Markup.Escape(plan.Repo)}");
        AnsiConsole.WriteLine();

        var checks = await RunChecksAsync(plan).ConfigureAwait(false);
        sw.Stop();

        foreach (var c in checks) AnsiConsole.MarkupLine(RenderCheck(c));

        var failed = checks.Count(c => c.State == "fail");
        var warned = checks.Count(c => c.State == "warn");
        var ok = checks.Count - failed - warned;

        AnsiConsole.WriteLine();
        var summaryColor = failed > 0 ? "red" : warned > 0 ? "yellow" : "green";
        AnsiConsole.MarkupLine($"[{summaryColor}]{ok} ok · {warned} warn · {failed} fail[/] — {sw.Elapsed.TotalMilliseconds:0}ms");

        return failed > 0 ? 1 : 0;
    }

    private static async Task<List<Check>> RunChecksAsync(PlanConfig plan)
    {
        var checks = new List<Check>
        {
            CheckAgentCli(plan),
            CheckGit(plan),
            CheckFace(),
            CheckGates(plan),
            CheckWorkCoverage(plan),
        };

        var (currentCostUsd, hasRun) = TryReadCostFromRunDb(plan);

        // Reuse PreflightHealth for DNS/disk/API — sane defaults when the plan hasn't configured
        // DnsHealthCheck at all, so doctor is useful on a fresh plan out of the box. Git is
        // disabled here since CheckGit above already does a richer clean+branch check.
        var configured = plan.Limits.DnsHealthCheck ?? new DnsHealthCheckConfig();
        var dnsCfg = new DnsHealthCheckConfig
        {
            Enabled = configured.Enabled,
            Hosts = configured.Hosts,
            IntervalSeconds = configured.IntervalSeconds,
            MinFreeDiskMb = configured.MinFreeDiskMb,
            ApiEndpoints = configured.ApiEndpoints,
            EnableGitCheck = false,
            BackoffMultiplier = configured.BackoffMultiplier,
            MaxBackoffSeconds = configured.MaxBackoffSeconds,
        };
        var preflight = await PreflightHealth.RunAllAsync(dnsCfg, plan.Repo, currentCostUsd, null).ConfigureAwait(false);
        checks.AddRange(preflight.Select(r => new Check(r.Name, r.Passed ? "ok" : "fail", r.Message)));

        checks.Add(CheckBudget(plan, currentCostUsd, hasRun));
        checks.Add(CheckTelegram(plan));

        return checks;
    }

    internal static Check CheckAgentCli(PlanConfig plan)
    {
        var cmd = plan.Agent.Command;
        if (string.IsNullOrWhiteSpace(cmd))
            return new Check("agent", "fail", "no agent.command configured in the plan");

        if (cmd.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || cmd.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(cmd))
        {
            return File.Exists(cmd)
                ? new Check("agent", "ok", $"{cmd}")
                : new Check("agent", "fail", $"{cmd} not found — check plan.agent.command");
        }

        var resolved = ResolveOnPath(cmd);
        return resolved != null
            ? new Check("agent", "ok", $"{cmd} → {resolved}")
            : new Check("agent", "fail", $"'{cmd}' not found on PATH — install it or fix plan.agent.command");
    }

    private static string? ResolveOnPath(string command)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries).Prepend("").ToArray()
            : [""];
        foreach (var dir in dirs)
        foreach (var ext in exts)
        {
            var candidate = Path.Combine(dir, command + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static Check CheckGit(PlanConfig plan)
    {
        if (!Directory.Exists(plan.Repo))
            return new Check("git", "fail", $"repo path does not exist: {plan.Repo}");
        try
        {
            var branch = Git.Branch(plan.Repo);
            if (string.IsNullOrWhiteSpace(branch))
                return new Check("git", "fail", $"{plan.Repo} does not look like a git repository");
            return Git.IsDirty(plan.Repo)
                ? new Check("git", "warn", $"branch {branch}, working tree dirty: {Git.DirtySummary(plan.Repo)}")
                : new Check("git", "ok", $"branch {branch}, clean");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new Check("git", "fail", ex.Message);
        }
    }

    internal static Check CheckFace()
    {
        var path = FaceLauncher.ResolveEntrypoint();
        return path != null
            ? new Check("face", "ok", path)
            : new Check("face", "warn",
                $"no built {FaceLauncher.BinaryName} found — run `go build -o bin/{FaceLauncher.BinaryName} ./cmd/conductor-face/` in face-go/, or set {FaceLauncher.PathEnvVar}");
    }

    /// <summary>U0.3: a gateless plan (`"gates": []` or absent) is a deliberate, supported choice —
    /// not a misconfiguration — so this is a warn-level notice, never a failure. Every session
    /// verdict on such a plan trusts commits + tracker diff alone (<see cref="GateRunner.Summary"/>
    /// already renders "gates green (none configured)" rather than a blank/lying summary).</summary>
    internal static Check CheckGates(PlanConfig plan)
        => plan.Gates.Count == 0
            ? new Check("gates", "warn", "none configured — every session verdict will trust commits + tracker only")
            : new Check("gates", "ok",
                $"{plan.Gates.Count} configured ({string.Join("/", plan.Gates.Select(g => g.Tier).Distinct(StringComparer.OrdinalIgnoreCase))})");

    /// <summary>W1.2 (G13): stage↔work-item coverage. A declared work item pointing at a stage the
    /// plan doesn't have is an authoring error the engine could never schedule (fail). A stage with
    /// zero declared items is survivable — WorkGraphSync scaffolds a placeholder at the next
    /// boundary — but the author should know (warn).</summary>
    internal static Check CheckWorkCoverage(PlanConfig plan)
    {
        Core.TrackerSnapshot declared;
        try { declared = Core.Planning.ProgressProviderFactory.Create(plan).Read(plan); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new Check("work", "warn", $"declared work unreadable ({ex.Message}) — the graph cannot sync until this resolves");
        }

        if (declared.Checkpoints.Count == 0)
            return new Check("work", "warn", "no declared work items — every stage gets a scaffolded placeholder checkpoint at the next sync");

        var stageIds = plan.Stages.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = declared.Checkpoints.Where(c => !stageIds.Contains(c.StageId)).Select(c => c.Id).ToList();
        if (orphans.Count > 0)
            return new Check("work", "fail",
                $"work item(s) [{string.Join(", ", orphans)}] derive stages not in the plan — fix the ids or add the stages (G13)");

        var uncovered = plan.Stages
            .Where(s => !declared.Checkpoints.Any(c => c.StageId.Equals(s.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(s => s.Id).ToList();
        return uncovered.Count > 0
            ? new Check("work", "warn",
                $"stage(s) [{string.Join(", ", uncovered)}] declare no work items — a placeholder checkpoint is scaffolded at the next sync")
            : new Check("work", "ok", $"{declared.Checkpoints.Count} work item(s) cover all {plan.Stages.Count} stage(s)");
    }

    internal static Check CheckBudget(PlanConfig plan, decimal currentCostUsd, bool hasRun)
    {
        if (plan.Limits.MaxRunCostUsd is not { } cap)
            return new Check("budget", "ok", "no cost cap configured (unbounded)");
        if (!hasRun)
            return new Check("budget", "ok", $"cap ${cap:0.00}, no run yet");
        if (currentCostUsd >= cap)
            return new Check("budget", "fail", $"${currentCostUsd:0.00} ≥ cap ${cap:0.00} — raise limits.maxRunCostUsd or the run will park at AwaitingOwner");

        var pct = cap > 0 ? (double)(currentCostUsd / cap) * 100 : 0;
        return pct >= 80
            ? new Check("budget", "warn", $"${currentCostUsd:0.00} / ${cap:0.00} ({pct:0}%) — approaching the cap")
            : new Check("budget", "ok", $"${currentCostUsd:0.00} / ${cap:0.00} ({pct:0}%)");
    }

    internal static Check CheckTelegram(PlanConfig plan)
    {
        if (plan.Telegram is not { } cfg)
            return new Check("telegram", "warn", "not configured — optional; add a telegram block to the plan, or set it up from the Face's Telegram tab");

        var hasToken = Environment.GetEnvironmentVariable("CONDUCTOR_TELEGRAM_TOKEN") is { Length: > 0 }
            || SecretsStore.TryReadTelegramToken(plan.StateDir) != null;
        if (!hasToken)
            return new Check("telegram", "warn", "configured but no bot token — set CONDUCTOR_TELEGRAM_TOKEN, or save one from the Face's Telegram tab");
        if (cfg.AllowedChatIds.Count == 0)
            return new Check("telegram", "warn", "token present but no allowedChatIds — bot is push-only to nobody");
        return new Check("telegram", "ok", $"token present, {cfg.AllowedChatIds.Count} allowed chat id(s)");
    }

    private static (decimal CostUsd, bool HasRun) TryReadCostFromRunDb(PlanConfig plan)
    {
        var runDbPath = Path.Combine(plan.StateDir, "run.db");
        if (!File.Exists(runDbPath)) return (0m, false);
        try
        {
            using var store = new SqliteRunStore(runDbPath, NullLogger<SqliteRunStore>.Instance);
            var report = StatusReportBuilder.Build(plan, store);
            return (report.TotalCostUsd, report.Kind != "norun");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return (0m, false);
        }
    }

    private static string RenderCheck(Check c)
    {
        var (glyph, color) = c.State switch
        {
            "ok" => ("✓", "green"),
            "warn" => ("⚠", "yellow"),
            _ => ("✗", "red"),
        };
        return $"[{color}]{glyph}[/] [bold]{Markup.Escape(c.Name),-8}[/] {Markup.Escape(c.Message)}";
    }
}
