using System.ComponentModel;

using Conductor.Core;
using Conductor.Core.Accounting;
using Conductor.Core.Store;
using Conductor.Core.Watch;
using Conductor.Models;

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// SF5.1 — <c>conductor watch</c>: block silently on a live run and return only when something needs
/// judgment.
///
/// <para>The verb exists because the owner priced the alternative: a babysitter agent polling a log
/// tail spends its budget on accumulation, not on the polls — over ten hours ~95% of its ticks say
/// "still running", and each one is paid for again in every later tick's context. The right shape is
/// a shell condition: waiting costs nothing, and the expensive reader is invoked once, at the moment
/// that needed it.</para>
///
/// <code>
///   conductor watch --json --timeout 60          # one wake or one heartbeat, then exit
///   while ($true) { conductor watch --json --hook 'claude -p "you are the night watch. brief on stdin."' }
///   while ($true) { conductor watch --json }     # SF5.2: the babysitter is the plan's supervisor block
/// </code>
///
/// <para>SF5.2 — a <c>supervisor</c> block in the plan names that command once, with its own timeout,
/// an hourly fuse and the standing orders the brief carries to it; <c>--hook</c> overrides it for a
/// deliberate one-off. See <see cref="Models.SupervisorConfig"/>.</para>
///
/// <para>Exit codes are the loop's control flow, so a shell can tell the two apart without a model:
/// <b>0</b> the wake set fired, <b>10</b> the timeout heartbeat expired with nothing to report,
/// <b>1</b> the watch could not be armed at all.</para>
/// </summary>
public sealed class WatchCommand : AsyncCommand<WatchCommand.Settings>
{
    /// <summary>A wake fired.</summary>
    public const int ExitWake = 0;

    /// <summary>--timeout expired with nothing on the wake set.</summary>
    public const int ExitTimeout = 10;

    public sealed class Settings : PlanSettings
    {
        [CommandOption("--json")]
        [Description("Emit only the JSON brief on stdout (nothing else) — for a hook, a pipe, or a model's stdin.")]
        public bool Json { get; init; }

        [CommandOption("--timeout <MINUTES>")]
        [Description("Long-fallback heartbeat: return with reason=timeout (exit 10) after N minutes of silence. Omit to block indefinitely.")]
        public double? TimeoutMinutes { get; init; }

        [CommandOption("--hook <COMMAND>")]
        [Description("Run this command on wake with the brief on stdin, overriding the plan's supervisor block. Fires on the wake set ONLY — never on a timeout heartbeat.")]
        public string? Hook { get; init; }

        [CommandOption("--notify <URL>")]
        [Description("SF5.3: POST the brief to this URL on wake, replacing the plan's supervisor.remote block (phone included). Not bound by its hourly fuse.")]
        public string? Notify { get; init; }

        [CommandOption("--hook-timeout <MINUTES>")]
        [Description("How long a --hook command may run before it is killed (default 10). The plan block uses its own supervisor.timeoutMinutes.")]
        public double HookTimeoutMinutes { get; init; } = 10;

        [CommandOption("--poll <SECONDS>")]
        [Description("How often the event log is checked for new lines (default 2). Costs a file-length read.")]
        public double PollSeconds { get; init; } = 2;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        if (!Directory.Exists(plan.StateDir))
        {
            AnsiConsole.MarkupLine($"[red]error:[/] no run state at {Markup.Escape(plan.StateDir)} — nothing to watch. Start one with [bold]conductor run[/].");
            return 1;
        }

        var poll = TimeSpan.FromSeconds(Math.Clamp(settings.PollSeconds, 0.1, 3600));
        using var loop = new WatchLoop(plan.StateDir, plan.Name, poll, plan.RunDbPath);
        var folded = loop.Arm();

        // The armed line goes to stderr so `conductor watch --json > brief.json` yields a file that is
        // exactly a JSON document, and a human running it in a terminal still sees that it started.
        // The run id is on it because "0 event(s) folded" has two very different causes — a fresh run,
        // and a watch that attached to nothing — and only this line tells them apart.
        await Console.Error.WriteLineAsync(
            $"watching {plan.Name} ({plan.StateDir}) — run {loop.RunId?[..Math.Min(8, loop.RunId.Length)] ?? "none found"}, " +
            $"{folded} event(s) of history folded, engine {(loop.EngineAlive() ? "alive" : "not running")}" +
            (settings.TimeoutMinutes is { } tm ? $", heartbeat in {tm:0.#}m" : ", no heartbeat") +
            " — silent until the wake set fires").ConfigureAwait(false);

        var timeout = settings.TimeoutMinutes is { } m && m > 0 ? TimeSpan.FromMinutes(m) : (TimeSpan?)null;
        var wake = await loop.RunAsync(timeout, CancellationToken.None).ConfigureAwait(false);

        var state = loop.ReadState();
        var brief = WatchBrief.Build(wake, plan, state, TryStatus(plan), loop.EngineAlive(), DateTimeOffset.UtcNow);
        var text = WatchBrief.Render(brief);

        if (settings.Json) await Console.Out.WriteLineAsync(text).ConfigureAwait(false);
        else RenderHuman(wake, state, text);

        // "Fires a hook ONLY on the wake set" is load-bearing: a heartbeat that invoked the expensive
        // supervisor would reintroduce exactly the per-tick cost this verb was built to remove.
        if (wake.Reason != WatchReason.Timeout)
        {
            var now = DateTimeOffset.UtcNow;

            // SF5.3 — the remote goes first, and goes regardless of what the local supervisor is allowed
            // to do. It is the escalation path: an hour in which the local babysitter has burnt its fuse,
            // or a plan with no local supervisor at all, is exactly when a human off this box needs the
            // wake. Sending it before the command also means a supervisor that hangs to its full timeout
            // cannot sit on the notification for ten minutes.
            var remote = await WatchRemote.DispatchAsync(plan, brief, text, settings.Notify, now).ConfigureAwait(false);
            foreach (var d in remote.Deliveries)
                await Console.Error.WriteLineAsync(
                    $"remote {d.Target} — {(d.Delivered ? "delivered" : "NOT delivered")}: {d.Detail}").ConfigureAwait(false);
            if (remote.Skipped is { } skippedWhy)
                await Console.Error.WriteLineAsync($"remote not sent — {skippedWhy}").ConfigureAwait(false);

            var decision = SupervisorPolicy.Decide(plan, settings.Hook,
                TimeSpan.FromMinutes(Math.Clamp(settings.HookTimeoutMinutes, 0.1, 1440)), now);

            if (decision.ShouldRun)
            {
                // Stamped before the run, not after: a supervisor that hangs for its whole timeout has
                // spent its invocation, and a fuse that only counts clean exits does not bound anything.
                if (decision.Source == "plan.supervisor") SupervisorPolicy.RecordFire(plan.StateDir, now);
                await Console.Error.WriteLineAsync(
                    $"supervisor ({decision.Source}) — running, brief on stdin, up to {decision.Timeout.TotalMinutes:0.#}m").ConfigureAwait(false);

                var r = await WatchHook.RunAsync(decision.Command!, plan.Repo, text, decision.Timeout).ConfigureAwait(false);
                await Console.Error.WriteLineAsync(
                    $"supervisor exit {r.ExitCode}{(r.TimedOut ? " (timed out)" : "")} in {r.Duration.TotalSeconds:0.#}s"
                    + (string.IsNullOrWhiteSpace(r.StdErr) ? "" : $" — {r.StdErr.Trim()}")).ConfigureAwait(false);
                RecordSupervisorSpend(plan, state, decision.Command!, r);
            }
            else if (decision.Skipped is { } why)
            {
                // A supervisor that does not run says so. Silence here reads identically to a supervisor
                // that ran and had nothing to say, and those are opposite situations.
                await Console.Error.WriteLineAsync($"supervisor not run — {why}").ConfigureAwait(false);
            }
        }

        return wake.Reason == WatchReason.Timeout ? ExitTimeout : ExitWake;
    }

    private static void RenderHuman(WatchWake wake, RunState? state, string briefJson)
    {
        var slug = WatchBrief.ReasonSlug(wake, state);
        var colour = wake.Reason == WatchReason.Timeout ? "grey" : "red";
        AnsiConsole.MarkupLine($"[bold {colour}]{Markup.Escape(slug)}[/] — {Markup.Escape(wake.Detail)}");
        AnsiConsole.WriteLine(briefJson);
    }

    /// <summary>KS5.2 — the supervisor is a model invocation like any other, and it was the only one
    /// that ran on somebody else's schedule and cost the run nothing on paper.
    /// <para>Best-effort by construction, and it has to be: this is the <c>watch</c> PROCESS, not the
    /// engine, so it is a second writer against a live WAL database. <c>RecordCost</c> goes through
    /// <c>TryExecute</c>, the store is opened and closed around the one insert, and every failure is
    /// swallowed — a supervisor's bookkeeping must never be able to break the wake it was reporting.
    /// No accrual FROM HERE: the engine owns the cap counters in its own memory and this process
    /// cannot move them. The row is what crosses the process boundary — the engine takes it in at its
    /// next session boundary through <c>RunContext.AbsorbOutOfProcessSpend</c>, which reads
    /// <c>IRunStore.SumSideSpendUsd</c> and accrues whatever the table holds beyond what it has already
    /// counted. That call is the mechanism; without it this row would be a receipt no ceiling could
    /// ever see, which is the silence this checkpoint exists to end.</para>
    /// <para>The row is keyed to the run's CURRENT session — the supervisor fires between sessions, so
    /// that is the last session it was watching.</para></summary>
    private static void RecordSupervisorSpend(PlanConfig plan, RunState? state, string command, ProcResult r)
    {
        if (state is not { RunId.Length: > 0 }) return;
        try
        {
            var spend = BilledSpend.ReadFromCommand(command, SpendCategory.Supervisor, r.Output,
                (long)r.Duration.TotalMilliseconds);
            if (spend is null) return;
            var dbPath = plan.RunDbPath;
            if (!File.Exists(dbPath)) return;
            using var store = new SqliteRunStore(dbPath, NullLogger<SqliteRunStore>.Instance);
            new RunSpendLedger(store, state.RunId,
                log: m => Console.Error.WriteLine($"supervisor spend — {m}"))
                .Record(spend, state.SessionCounter, "supervisor hook");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // The wake is the deliverable; the row is a bonus.
        }
    }

    // run.db is where the stage board and spend live. It is optional on purpose: a run that has not
    // written one yet, or a database locked by the engine at the instant of the wake, must still
    // produce a brief — the wake reason is the part that cannot be missing.
    private static StatusReport? TryStatus(PlanConfig plan)
    {
        try
        {
            var dbPath = plan.RunDbPath;
            if (!File.Exists(dbPath)) return null;
            using var store = new SqliteRunStore(dbPath, NullLogger<SqliteRunStore>.Instance);
            return StatusReportBuilder.Build(plan, store);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }
}
