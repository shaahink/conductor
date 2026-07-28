using System.Collections.Concurrent;
using Conductor.Core.Events;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Conductor.Core.Hosting;

/// <summary>
/// Composition root (B2.5, D-12): builds a <see cref="IHost"/> that wires the Orchestrator and its
/// collaborators through DI, validates the plan config on start via the Options pattern, and routes
/// structured logs through Serilog (file sink under <c>.conductor/logs/</c>, plus an optional console
/// sink for non-dashboard runs). Correlation properties (runId/sessionId/stage/gate) are attached per
/// log line by the Orchestrator via <c>ILogger.BeginScope</c> and flow to Serilog through
/// <c>Enrich.FromLogContext</c>. The host is used purely as a composition + logging root (no
/// long-running <c>IHostedService</c>): disposing it flushes Serilog.
/// </summary>
public static class ConductorHost
{
    /// <summary>Output template carrying the correlation scope; missing properties render empty so a
    /// pre-session line (no sessionId/gate yet) is still well-formed.</summary>
    internal const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] " +
        "run={runId} s={sessionId} stage={stage} gate={gate} {Message:lj}{NewLine}{Exception}";

    /// <summary>Builds the composition root. The caller provides <paramref name="sink"/>
    /// (chosen by run mode) and resolves the <see cref="Orchestrator"/> from <see cref="IHost.Services"/>.
    /// Throws <see cref="OptionsValidationException"/> if the plan is invalid (fail-fast on start).</summary>
    public static IHost Build(
        PlanConfig plan,
        RunState state,
        IProgressSink sink,
        RunOptions opts,
        bool consoleSink)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sink);

        var builder = Host.CreateApplicationBuilder();

        var logDir = Path.Combine(plan.StateDir, "logs");
        Directory.CreateDirectory(logDir);

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((_, lc) =>
        {
            lc.MinimumLevel.Debug()
              .Enrich.FromLogContext()
              .WriteTo.File(
                  path: Path.Combine(logDir, "conductor-.log"),
                  rollingInterval: RollingInterval.Day,
                  outputTemplate: FileTemplate,
                  shared: true)
              .WriteTo.File(
                  new RenderedCompactJsonFormatter(),
                  path: Path.Combine(logDir, "conductor-.json"),
                  rollingInterval: RollingInterval.Day,
                  shared: true);
            // The live dashboard owns stdout; a console sink would corrupt the TUI, so it is only
            // attached for plain/dry-run/redirected runs where narration is already going to stdout.
            if (consoleSink)
                lc.WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information, outputTemplate: FileTemplate);
        });

        // Options pattern with fail-fast validation on start (B2.5, §5). The plan is loaded via
        // System.Text.Json (comment/trailing-comma tolerant), so it is registered as a prebuilt option
        // instance and re-validated here by the shared IValidateOptions before the run loop begins.
        builder.Services.AddSingleton<IOptions<PlanConfig>>(Options.Create(plan));
        builder.Services.AddSingleton<IValidateOptions<PlanConfig>, PlanConfigValidator>();

        builder.Services.AddSingleton(plan);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton(sink);

        // B6: Telegram bot
        if (plan.Telegram != null)
        {
            builder.Services.AddSingleton<TelegramService>(sp =>
                new TelegramService(plan, state, sp.GetRequiredService<ILogger<TelegramService>>(),
                    store: opts.DryRun ? null : sp.GetRequiredService<IRunStore>()));
            builder.Services.AddSingleton<ITelegramService>(sp => sp.GetRequiredService<TelegramService>());
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TelegramService>());
        }
        else
        {
            builder.Services.AddSingleton<ITelegramService>(new NoOpTelegramService());
        }

        // B6.4: Webhook notifier (generic/Discord/Slack) — fire-and-forget HTTP POST.
        builder.Services.AddSingleton<WebhookNotifier>();

        // B9.2: planner decomposition — produces ordered sub-tasks from a checkpoint.
        builder.Services.AddSingleton<IPlanner>(new CheckpointPlanner());

        // P0/P1/P2: the planning seams — the engine asks the interfaces, never the implementations.
        builder.Services.AddSingleton<IWorkflowResolver>(new WorkflowEngine());
        builder.Services.AddSingleton<IAssignmentPolicy>(new DefaultAssignmentPolicy());
        builder.Services.AddSingleton<IQaPolicy>(new DefaultQaPolicy());

        // M2: SQLite run.db — the single authoritative store.
        // Registered as both IRunStore (write + query surface) and IEventSink (event spine).
        // Dry runs skip the database (no write side-effects).
        builder.Services.AddSingleton(sp =>
        {
            if (opts.DryRun) return null!;
            var runDbPath = Path.Combine(plan.StateDir, "run.db");
            var store = new SqliteRunStore(runDbPath, sp.GetRequiredService<ILogger<SqliteRunStore>>());
            store.SetRunId(state.RunId);
            return store;
        });
        builder.Services.AddSingleton<IRunStore>(sp =>
        {
            var store = sp.GetService<SqliteRunStore>();
            return store!;
        });
        builder.Services.AddSingleton<IEventSink>(sp =>
        {
            var store = sp.GetService<SqliteRunStore>();
            if (store != null) return store;
            // Dry run: no store → use null sink (the IEventSink DI contract requires non-null)
            return NullEventSink.Instance;
        });

        // F2.1: Process supervisor — run-level Job Object + PID tracking.
        // Created before the Orchestrator so its JobObject covers the full run lifecycle.
        builder.Services.AddSingleton<ProcessSupervisor>(sp =>
        {
            var store = sp.GetService<IRunStore>();
            var supLogger = sp.GetRequiredService<ILogger<ProcessSupervisor>>();
            return new ProcessSupervisor(supLogger, state.RunId, store);
        });

        // F5: control-plane inbox
        builder.Services.AddSingleton(new ConcurrentQueue<ControlCommand>());

        // F5/F6: HTTP+SSE control plane — opt-in (RunOptions.ControlPlane), off by default.
        if (opts.ControlPlane)
        {
            builder.Services.AddSingleton(sp => new ControlPlaneServer(
                plan,
                state,
                sp.GetRequiredService<IRunStore>(),
                sp.GetRequiredService<ConcurrentQueue<ControlCommand>>(),
                sp.GetRequiredService<ITelegramService>(),
                sp.GetRequiredService<ILogger<ControlPlaneServer>>(),
                opts.ControlPlanePort));
        }

        // M2: Orchestrator — the run-loop entry point.
        builder.Services.AddSingleton(sp => new Orchestrator(
            sp.GetRequiredService<PlanConfig>(),
            sp.GetRequiredService<RunState>(),
            sp.GetRequiredService<IProgressSink>(),
            sp.GetRequiredService<IEventSink>(),
            sp.GetRequiredService<RunOptions>(),
            sp.GetRequiredService<ILogger<Orchestrator>>(),
            sp.GetRequiredService<ITelegramService>(),
            sp.GetRequiredService<WebhookNotifier>(),
            planner: null,
            store: sp.GetService<IRunStore>(),
            processSupervisor: sp.GetService<ProcessSupervisor>(),
            controlInbox: sp.GetRequiredService<ConcurrentQueue<ControlCommand>>(),
            workflowResolver: sp.GetRequiredService<IWorkflowResolver>(),
            assignmentPolicy: sp.GetRequiredService<IAssignmentPolicy>(),
            qaPolicy: sp.GetRequiredService<IQaPolicy>(),
            // W5.1: the control plane caches a plan reference and was the one satellite the reload
            // never swapped, so the Face served the pre-edit plan for the rest of the run.
            onPlanSwapped: fresh => sp.GetService<ControlPlaneServer>()?.SwapPlan(fresh)));

        var host = builder.Build();
        ValidateOptionsOnStart(host.Services, plan);
        return host;
    }

    private static void ValidateOptionsOnStart(IServiceProvider services, PlanConfig plan)
    {
        var failures = new List<string>();
        foreach (var validator in services.GetServices<IValidateOptions<PlanConfig>>())
        {
            var result = validator.Validate(Options.DefaultName, plan);
            if (result.Failed)
                failures.AddRange(result.Failures);
        }
        if (failures.Count > 0)
            throw new OptionsValidationException(nameof(PlanConfig), typeof(PlanConfig), failures);
    }
}

/// <summary>Validates <see cref="PlanConfig"/> on host start, reusing the same rule set as
/// <see cref="PlanConfig.Load"/> so a config that loads cleanly never fails validation and vice-versa.</summary>
public sealed class PlanConfigValidator : IValidateOptions<PlanConfig>
{
    public ValidateOptionsResult Validate(string? name, PlanConfig options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = options.CollectErrors();
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
