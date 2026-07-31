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
/// <c>Enrich.FromLogContext</c>. Disposing it flushes and closes <em>this host's</em> Serilog logger,
/// and no other's.
/// </summary>
/// <remarks>
/// SC1.1: this comment used to claim the host ran "no long-running <c>IHostedService</c>", and that
/// claim was false — <see cref="TelegramService"/> has been registered as one since B6. Because the
/// claim was believed, nobody ever started the host, so <c>TelegramService.StartAsync</c> never ran,
/// <c>_started</c> stayed false, and every push returned early in silence for the life of the
/// feature. (<c>TestConnectionAsync</c> bypasses <c>_started</c>, which is why the Face's Test button
/// kept reporting success over a dead service.) The host is still not run as a hosted-lifetime
/// application; instead <see cref="StartRunServicesAsync"/> starts the registered hosted services
/// explicitly, following <c>ControlPlaneServer.Start()</c>'s precedent. Anything registered as an
/// <c>IHostedService</c> here is therefore genuinely started on the run path — and must be, since
/// the registration is now the only thing the run path consults.
/// </remarks>
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
        // preserveStaticLogger: the host's logger is the host's own, and disposing this host disposes
        // exactly it. The default (false) does two global things instead: it assigns the process-wide
        // Serilog.Log.Logger, and it registers the logger factory with a NULL logger — whose disposal
        // path is Log.CloseAndFlush(), i.e. "close whatever the static logger happens to be right
        // now". With two hosts alive in one process the second to be built owns the static slot, so
        // the first to be disposed closes the OTHER host's logger and its file sink, mid-run: no
        // throw, no warning, a log that simply stops. A logger resolved after the second build is
        // delivered to the second host's file for the same reason. That is what made
        // HostLoggingTests.DryRunWritesStructuredLogWithRunIdCorrelation red under the full battery
        // and green alone — its log held the "conductor start" line and nothing after it. Nothing in
        // this codebase reads the static Serilog.Log, so preserving it costs nothing.
        // See HostLoggerIsolationTests, which reproduces both halves without any parallelism.
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
        }, preserveStaticLogger: true);

        // Options pattern with fail-fast validation on start (B2.5, §5). The plan is loaded via
        // System.Text.Json (comment/trailing-comma tolerant), so it is registered as a prebuilt option
        // instance and re-validated here by the shared IValidateOptions before the run loop begins.
        builder.Services.AddSingleton<IOptions<PlanConfig>>(Options.Create(plan));
        builder.Services.AddSingleton<IValidateOptions<PlanConfig>, PlanConfigValidator>();

        builder.Services.AddSingleton(plan);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton(sink);

        // B6: Telegram bot.
        // SC1.3: registered whether or not the plan has a telegram block today. The old `else` branch
        // pinned a NoOpTelegramService for the life of the process, so a telegram block added later —
        // by the Face's setup tab, by `plan set`, by any /plan/edit — reached a service that could
        // never exist, and every surface still reported the setup as saved. The real service is a
        // no-op too when there is no block (StartAsync says so and returns), the difference being
        // that this one can be handed the block when it arrives.
        builder.Services.AddSingleton<TelegramService>(sp =>
            new TelegramService(plan, state, sp.GetRequiredService<ILogger<TelegramService>>(),
                store: opts.DryRun ? null : sp.GetRequiredService<IRunStore>()));
        builder.Services.AddSingleton<ITelegramService>(sp => sp.GetRequiredService<TelegramService>());
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TelegramService>());

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
            onPlanSwapped: fresh =>
            {
                sp.GetService<ControlPlaneServer>()?.SwapPlan(fresh);
                // SC1.3: Telegram was the other satellite holding a private copy of the plan — its
                // block AND its token were frozen at construction, so a telegram edit reloaded into
                // every other collaborator and stopped dead here. Not awaited: this runs on the run
                // loop's session boundary and a reload restarts the service, which drains the send
                // queue for up to DrainGrace. ApplyPlanAsync is non-throwing and logs its own
                // outcome, so nothing is lost by letting it finish behind the loop.
                if (sp.GetService<TelegramService>() is { } telegram)
                    _ = telegram.ApplyPlanAsync(fresh);
            }));

        var host = builder.Build();
        ValidateOptionsOnStart(host.Services, plan);
        return host;
    }

    /// <summary>
    /// SC1.1: starts every <see cref="IHostedService"/> the composition root registered. Call this on
    /// the run path, next to <c>ControlPlaneServer.Start()</c> — building the host only composes it.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT swallow start failures. The instinct is to wrap each start in a catch-all
    /// so a broken notifier cannot take the run down — but a service that failed to start and said
    /// nothing about it is the exact shape of the bug this checkpoint exists to fix, just relocated.
    /// Starting is cheap and local (the implementations spawn their own loops and return; the loops
    /// own their I/O and their own error handling), so a throw here means a wiring fault worth
    /// hearing about, not a flaky network.
    /// </remarks>
    /// <returns>The names of the services that were started, in registration order.</returns>
    public static async Task<IReadOnlyList<string>> StartRunServicesAsync(IHost host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var log = host.Services.GetRequiredService<ILogger<IHost>>();
        var started = new List<string>();
        foreach (var svc in host.Services.GetServices<IHostedService>())
        {
            await svc.StartAsync(ct).ConfigureAwait(false);
            started.Add(svc.GetType().Name);
        }
        log.LogInformation("Run services started: {Services}",
            started.Count == 0 ? "(none registered)" : string.Join(", ", started));
        return started;
    }

    /// <summary>SC1.1: the matching stop, so queued pushes flush and long-polls end before the
    /// process exits. Call it while the host is still alive — disposing it just drops the backlog.
    /// Implementations are responsible for making their own shutdown non-throwing and bounded
    /// (see <see cref="TelegramService.StopAsync"/>), because this runs in the run path's finally
    /// and must not replace the run's own outcome with a shutdown error.</summary>
    public static async Task StopRunServicesAsync(IHost host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        foreach (var svc in host.Services.GetServices<IHostedService>())
            await svc.StopAsync(ct).ConfigureAwait(false);
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
