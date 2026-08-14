using System.ComponentModel;
using System.Diagnostics;
using Conductor.Core;
using Conductor.Core.Budget;
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
public sealed class DoctorSettings : PlanSettings
{
    /// <summary>W3.2: skip the one-token auth ping (the only check that spends money or talks to
    /// the model backend).</summary>
    [CommandOption("--no-auth-check")]
    [Description("Skip the one-token auth smoke test (~$0.001) against the configured agent CLI")]
    public bool NoAuthCheck { get; init; }

    /// <summary>SC8.3: skip the release-feed lookup. Also honoured as
    /// <c>CONDUCTOR_NO_UPDATE_CHECK</c>, for a machine that should never phone home.</summary>
    [CommandOption("--no-update-check")]
    [Description("Skip the check for a newer released engine")]
    public bool NoUpdateCheck { get; init; }
}

public sealed partial class DoctorCommand : AsyncCommand<DoctorSettings>
{
    internal sealed record Check(string Name, string State, string Message); // State: ok | warn | fail

    public override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sw = Stopwatch.StartNew();

        // SC3.1: a plan that fails validation is the commonest thing doctor is asked about, so it
        // reports it as a check — not as an unhandled exception that also drops a crash log in
        // whatever directory the operator happened to be standing in.
        PlanConfig plan;
        try
        {
            plan = PlanConfig.Load(settings.ResolvePlanPath());
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or System.Text.Json.JsonException)
        {
            AnsiConsole.MarkupLine("[bold aqua]conductor doctor[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(RenderCheck(new Check("plan", "fail", ex.Message)));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red]0 ok · 0 warn · 1 fail[/] — the plan does not load, so no other check ran ({sw.Elapsed.TotalMilliseconds:0}ms)");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold aqua]conductor doctor[/] — {Markup.Escape(plan.Name)}");
        AnsiConsole.MarkupLine($"repo: {Markup.Escape(plan.Repo)}");
        AnsiConsole.WriteLine();

        var checks = await RunChecksAsync(plan, authCheck: !settings.NoAuthCheck, updateCheck: !settings.NoUpdateCheck).ConfigureAwait(false);
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

    private static async Task<List<Check>> RunChecksAsync(PlanConfig plan, bool authCheck = false, bool updateCheck = true)
    {
        var checks = new List<Check>
        {
            CheckAgentCli(plan),
            CheckModelToken(plan),
            CheckGit(plan),
            CheckSatelliteRepos(plan),
            CheckFace(),
            CheckGates(plan),
            CheckWorkCoverage(plan),
            CheckPrompt(plan),
            CheckAdvisor(plan),
            // KS1.4 — the plan-semantics lints. Offline, read-only, and every one of them names the
            // artifact it is unhappy about. Their bodies are the two partials named for what they
            // read: DoctorCommand.PlanSemantics.cs and DoctorCommand.PromptSemantics.cs.
            CheckGatePaths(plan),
            CheckHooks(plan),
            CheckCheckpointIds(plan),
            CheckPlanDrift(plan),
            CheckArgvLength(plan),
        };
        // These read files — the templates, and the plan document as raw text — so they are async all
        // the way down rather than blocking here (MA0045 is an error in this tree).
        checks.Add(await CheckTemplateBracesAsync(plan).ConfigureAwait(false));
        checks.Add(await CheckEscalationTokenAsync(plan).ConfigureAwait(false));
        // KS3.3 — the file's own keys, judged against the shape the engine declares. Warn-level:
        // an inert key cannot break a run, it just cannot do what the author thinks it does.
        checks.Add(await CheckInertKeysAsync(plan).ConfigureAwait(false));

        var (currentCostUsd, hasRun) = TryReadCostFromRunDb(plan);
        // KS5.4: the grants live in the persisted run_state row, not on the event spine the status
        // report folds, so they are read through the one loader that reads run state before a host
        // exists. Missing db, missing table or torn JSON all answer "no grant" — the plan's own caps.
        var resumed = await RunStateResume
            .TryLoadLatestAsync(plan.RunDbPath, plan.Name, CancellationToken.None)
            .ConfigureAwait(false);
        var budgetGrantUsd = resumed?.BudgetGrantUsd ?? 0m;
        var budgetGrantTokens = resumed?.BudgetGrantTokens ?? 0L;

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

        checks.Add(CheckBudget(plan, currentCostUsd, hasRun, budgetGrantUsd, budgetGrantTokens));
        checks.Add(CheckTokenBudget(plan));
        checks.Add(CheckState(plan));
        checks.Add(CheckTelegram(plan));

        // W3.2: the one check that talks to the model backend. A dead token is invisible to every
        // other check here and kills a run thirteen sessions in, so it is on by default — and it is
        // the only thing `--no-auth-check` turns off.
        if (authCheck)
        {
            var auth = await AuthSmokeTest.RunAsync(plan, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            checks.Add(new Check(auth.Name, auth.Passed ? "ok" : "fail", auth.Message));
        }

        if (updateCheck) checks.Add(await CheckUpdateAsync(DateTimeOffset.UtcNow).ConfigureAwait(false));

        return checks;
    }

    internal static Check CheckAgentCli(PlanConfig plan)
    {
        var cmd = plan.Agent.Command;
        if (string.IsNullOrWhiteSpace(cmd))
            return new Check("agent", "fail", "no agent.command configured in the plan");

        if (IsPathLike(cmd))
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

    /// <summary>SC3.4 — the advisor is optional, but a CONFIGURED one that cannot answer is worse than
    /// none: the consult spawns, burns its timeout, falls back to the deterministic default, and says so
    /// in one grey log line. The invocation that cannot answer at all (no <c>args</c>, no
    /// <c>{prompt}</c>, an unknown output kind, an unknown key) is refused at plan load and lands here
    /// as the <c>plan</c> check; what is left for doctor is the half only the machine knows — whether
    /// the CLI named in <c>advisor.command</c> is actually installed — plus printing the invocation, so
    /// "which model is my second brain" is answerable without reading the plan file.</summary>
    internal static Check CheckAdvisor(PlanConfig plan)
    {
        if (plan.Advisor is not { } a)
            return new Check("advisor", "ok", "not configured — an ambiguous session outcome takes the deterministic default");
        if (!a.Enabled)
            return new Check("advisor", "ok", "disabled — an ambiguous session outcome takes the deterministic default");

        var invocation = $"{a.Command} {string.Join(" ", a.Args)}".TrimEnd();
        var found = IsPathLike(a.Command) ? (File.Exists(a.Command) ? a.Command : null) : ResolveOnPath(a.Command);
        return found is null
            ? new Check("advisor", "warn",
                $"'{a.Command}' not found on PATH — every consult fails to spawn and falls back to the deterministic default " +
                "(install it, fix advisor.command, or set advisor.enabled false)")
            : new Check("advisor", "ok", $"{invocation} → {a.Output}, {a.TimeoutMinutes}m timeout");
    }

    private static bool IsPathLike(string cmd)
        => cmd.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || cmd.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
        || Path.IsPathRooted(cmd);

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

    /// <summary>SC3.1 — the single most dangerous config trap found in the field (devcontext #2).
    /// <c>AgentSession.ResolveArgs</c> substitutes the model ONLY where the template already says
    /// <c>{model}</c>, so a plan that pins <c>agent.model</c> in a template without that placeholder
    /// runs the agent CLI's own default model — while <c>journey</c>, <c>/state</c> and the plan file
    /// all keep reporting the pinned one. It was caught once, by reading a raw session stream.
    /// <para>Checked per stage against the MERGED agent (stage overrides fold into the plan default),
    /// and per template: <c>AgentSession.Start</c> swaps <c>args</c> for <c>resumeArgs</c> on resume,
    /// so a resumeArgs template missing the placeholder silently changes model halfway through a
    /// stage — the harder half to notice. A role rule model (<c>pipeline.roles.*.model</c>) reaches
    /// the same substitution via <c>SessionRunner</c>, so it counts as a pinned model too.
    /// This is <c>fail</c>, never <c>warn</c>: nothing downstream can detect it.</para></summary>
    internal static Check CheckModelToken(PlanConfig plan)
    {
        var roleModels = plan.Pipeline?.Roles?
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value?.Model))
            .Select(kv => $"pipeline.roles.{kv.Key}.model")
            .ToList() ?? [];

        var pinned = new SortedSet<string>(StringComparer.Ordinal);
        var argsGaps = new List<string>();
        var resumeGaps = new List<string>();

        foreach (var stage in plan.Stages)
        {
            var eff = plan.ResolveAgent(stage);
            var sources = new List<string>(roleModels);
            if (!string.IsNullOrWhiteSpace(eff.Model))
                sources.Add(stage.Agent?.Model is { Length: > 0 } ? $"stage '{stage.Id}' agent.model" : "plan.agent.model");
            if (sources.Count == 0) continue;

            var argsMissing = !HasModelToken(eff.Args);
            var resumeMissing = eff.ResumeArgs is { Count: > 0 } && !HasModelToken(eff.ResumeArgs);
            if (!argsMissing && !resumeMissing) continue;

            if (argsMissing) argsGaps.Add(stage.Id);
            if (resumeMissing) resumeGaps.Add(stage.Id);
            foreach (var s in sources) pinned.Add(s);
        }

        if (argsGaps.Count == 0 && resumeGaps.Count == 0)
        {
            var configured = plan.Agent.Model ?? plan.Stages.Select(s => plan.ResolveAgent(s).Model).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
            return string.IsNullOrWhiteSpace(configured) && roleModels.Count == 0
                ? new Check("model", "ok", "no model pinned — every session runs the agent CLI's own default")
                : new Check("model", "ok", $"pinned model reaches the CLI — every args/resumeArgs template carries {{model}}");
        }

        var parts = new List<string>();
        if (argsGaps.Count > 0)
            parts.Add($"args for stage(s) [{string.Join(", ", argsGaps)}] carry no {{model}} placeholder, so those sessions run the CLI's default model instead");
        if (resumeGaps.Count > 0)
            parts.Add($"resumeArgs for stage(s) [{string.Join(", ", resumeGaps)}] carry no {{model}} placeholder, so a resumed session silently switches model");

        return new Check("model", "fail",
            $"{string.Join(" + ", pinned)} set, but {string.Join("; ", parts)} — add \"--model\", \"{{model}}\" to the template(s), or clear the model");
    }

    private static bool HasModelToken(IEnumerable<string> template)
        => template.Any(a => a.Contains("{model}", StringComparison.Ordinal));

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

    /// <summary>SC4.3: a declared satellite that is not a readable git repo is a silent hole in the
    /// verdict — the run keeps scoring, it just never counts the commits that repo receives, which is
    /// the failure the setting exists to prevent. A typo in the path has to be loud at authoring time,
    /// not discovered as a second NoProgress on a delivered stage.</summary>
    internal static Check CheckSatelliteRepos(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var resolved = SatelliteRepos.Resolve(plan);
        var declared = plan.SatelliteRepos?.Count(s => !string.IsNullOrWhiteSpace(s)) ?? 0;
        if (declared == 0)
            return new Check("satellites", "ok", "none declared — the verdict counts commits in this repo only");

        var bad = new List<string>();
        foreach (var (label, path) in resolved)
        {
            if (!Directory.Exists(path)) { bad.Add($"{label}: path does not exist ({path})"); continue; }
            var r = Git.Exec(path, "rev-parse", "HEAD");
            var sha = r.Output.Trim();
            if (r.ExitCode != 0 || sha.Length < 7 || !sha.All(Uri.IsHexDigit))
                bad.Add($"{label}: not a git repository with commits ({path})");
        }
        if (bad.Count > 0)
            return new Check("satellites", "fail",
                $"{bad.Count} of {declared} satelliteRepos unusable — their commits will NOT count toward the verdict: {string.Join("; ", bad)}");

        var dropped = declared - resolved.Count;
        var note = dropped > 0 ? $" ({dropped} duplicate or same-as-repo entr(y/ies) ignored)" : "";
        return new Check("satellites", "ok",
            $"{resolved.Count} satellite repo(s) diffed for commits alongside this one: {string.Join(", ", resolved.Select(s => s.Label))}{note}");
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
    /// <remarks>SC2.2 adds the per-stage half. Gates scoped with <c>gates[].stages</c> or
    /// <c>gates[].stageKinds</c> leave the stages they do not name with NO battery at all, and nothing
    /// said so: on one run nine of thirteen stages confirmed with zero gates — including the stage that
    /// deployed the live site — and the only way to learn that was to cross-reference the plan by hand
    /// (sk-platform #2). Still a warn: a gateless stage is a legitimate choice, an unknowing one is not.</remarks>
    internal static Check CheckGates(PlanConfig plan)
    {
        if (plan.Gates.Count == 0)
            return new Check("gates", "warn", "none configured — every session verdict will trust commits + tracker only");

        var tiers = string.Join("/", plan.Gates.Select(g => g.Tier).Distinct(StringComparer.OrdinalIgnoreCase));
        var gateless = plan.Stages.Where(s => Core.GateRunner.ConfiguredForStage(plan, s) == 0).Select(s => s.Id).ToList();
        if (gateless.Count > 0)
            return new Check("gates", "warn",
                $"{plan.Gates.Count} configured ({tiers}), but stage(s) [{string.Join(", ", gateless)}] match none of them — " +
                "those stages confirm on claims, commits and tracker diff alone (widen gates[].stages / gates[].stageKinds, or accept it knowingly)");

        return new Check("gates", "ok", $"{plan.Gates.Count} configured ({tiers}), every stage covered");
    }

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

    /// <summary>SC3.3 — the prompt is the product, so doctor composes it. A brace in authored prose is
    /// refused at plan load (and lands here as the <c>plan</c> check), but a template FILE under
    /// <c>templatesDir</c> is not part of the plan document and nothing validated it: a single typo'd
    /// <c>{name}</c> in it killed the run at the next stage boundary, mid-plan, after the operator had
    /// walked away. Rendering every session kind for every stage costs a few file reads and turns that
    /// into a pre-launch failure naming the template and the token.</summary>
    internal static Check CheckPrompt(PlanConfig plan)
    {
        var prompts = new PromptBuilder(plan);

        foreach (var stage in plan.Stages)
        {
            // KS1.4: the matrix moved to DoctorCommand.PromptSemantics.cs so the argv-length lint
            // measures exactly the sessions this check renders — one definition of "a session kind".
            foreach (var (template, render) in PromptMatrix(prompts, plan, stage))
            {
                try { render(); }
                catch (PromptCompositionException ex)
                {
                    return new Check("prompt", "fail", $"stage '{stage.Id}' {template}: {ex.Message}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new Check("prompt", "warn", $"stage '{stage.Id}' {template} could not be read ({ex.Message})");
                }
            }
        }

        return new Check("prompt", "ok",
            $"every session kind renders for all {plan.Stages.Count} stage(s) with no unresolved placeholder");
    }

    // CheckBudget lives in DoctorCommand.Budget.cs (KS5.4 round 3: extracted, not appended — this
    // file was at the 500-line ceiling when the check grew its token half).

    /// <summary>SC1.2: the sentences live in <see cref="TelegramReadiness"/>, which
    /// <c>GET /telegram/status</c> and <c>TelegramService.StartAsync</c> also read, so doctor and the
    /// live surfaces cannot drift apart on what "working" means. Started-ness is passed as null:
    /// doctor runs outside the engine and has no honest way to know it.</summary>
    internal static Check CheckTelegram(PlanConfig plan)
    {
        var cfg = plan.Telegram;
        var hasToken = Environment.GetEnvironmentVariable("CONDUCTOR_TELEGRAM_TOKEN") is { Length: > 0 }
            || SecretsStore.TryReadTelegramToken(plan.StateDir) != null;

        var missing = TelegramReadiness.MissingHalf(
            hasBlock: cfg is not null, hasToken: hasToken,
            allowedChatIds: cfg?.AllowedChatIds.Count ?? 0, started: null);

        return missing is not null
            ? new Check("telegram", "warn", missing)
            : new Check("telegram", "ok", $"token present, {cfg!.AllowedChatIds.Count} allowed chat id(s)");
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
