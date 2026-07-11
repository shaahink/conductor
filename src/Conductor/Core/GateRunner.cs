using Conductor.Models;

namespace Conductor.Core;

public sealed record GateResult(string Name, bool Passed, bool Skipped, bool Optional, int ExitCode, TimeSpan Duration, string Tail)
{
    public string Glyph => Skipped ? "-" : Passed ? "OK" : Optional ? "warn" : "FAIL";
    /// <summary>Estimated overhead cost = Duration × rate (O3). Skipped gates contribute zero.</summary>
    public decimal EstimatedCostUsd(decimal ratePerSecond) => Skipped ? 0m : (decimal)Duration.TotalSeconds * ratePerSecond;
}

public static class GateRunner
{
    /// <param name="fastOnly">When true, only run gates tagged tier "fast" (per-session under perPhase policy).</param>
    /// <param name="currentStage">Gates with a Stages filter only run when the current stage matches.</param>
    /// <param name="onGates">Live per-gate status callback (dashboard timers).</param>
    public static async Task<List<GateResult>> RunAllAsync(PlanConfig plan, Action<string>? onProgress = null, CancellationToken ct = default,
        bool fastOnly = false, string? currentStage = null, Action<IReadOnlyList<GateProgress>>? onGates = null)
    {
        var gates = plan.Gates
            .Where(g => (!fastOnly || g.IsFast) && g.AppliesToStage(currentStage))
            .ToList();
        var results = new GateResult?[gates.Count];

        // Live status array shared across the (possibly parallel) gate tasks.
        var live = gates.Select(g => GateProgress.Pending(g.Name)).ToArray();
        var liveGate = new Lock();
        void Emit() { if (onGates != null) { lock (liveGate) onGates(live.ToArray()); } }
        void Mark(int i, GateProgress gp) { lock (liveGate) live[i] = gp; Emit(); }
        Emit();

        async Task<GateResult> RunTrackedAsync(int i)
        {
            Mark(i, new GateProgress(gates[i].Name, "running", TimeSpan.Zero, DateTime.UtcNow));
            var r = await RunOneAsync(plan, gates[i], onProgress, ct).ConfigureAwait(false);
            var state = r.Skipped ? "skip" : r.Passed ? "pass" : r.Optional ? "warn" : "fail";
            Mark(i, new GateProgress(gates[i].Name, state, r.Duration));
            return r;
        }

        // Walk in listed order; a non-parallel gate is a barrier (runs alone), consecutive
        // parallel gates run concurrently as one batch. Lets `build` gate everyone before the
        // slow independent gates (tests/pnpm/mcp-qa) fan out together.
        var batch = new List<int>();
        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            var idx = batch.ToList();
            var batchResults = await Task.WhenAll(idx.Select(RunTrackedAsync)).ConfigureAwait(false);
            for (var j = 0; j < idx.Count; j++) results[idx[j]] = batchResults[j];
            batch.Clear();
        }

        for (var i = 0; i < gates.Count; i++)
        {
            if (ct.IsCancellationRequested) { await FlushAsync().ConfigureAwait(false); break; }
            if (gates[i].Parallel) { batch.Add(i); continue; }
            await FlushAsync().ConfigureAwait(false);
            results[i] = await RunTrackedAsync(i).ConfigureAwait(false);
        }
        await FlushAsync().ConfigureAwait(false);

        // any not-yet-populated slots (e.g. cancelled before reached) get a skipped placeholder
        return results.Select((r, i) => r ?? new GateResult(gates[i].Name, false, true, gates[i].Optional, 0, TimeSpan.Zero, "not run (cancelled)")).ToList();
    }

    /// <summary>Signature of the full gate battery for a given tree state — used to skip identical reruns.</summary>
    public static string BatterySignature(PlanConfig plan, string headSha, string? currentStage)
    {
        var names = plan.Gates.Where(g => g.AppliesToStage(currentStage)).Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal);
        return headSha + "|" + string.Join(",", names);
    }

    private static async Task<GateResult> RunOneAsync(PlanConfig plan, GateConfig g, Action<string>? onProgress, CancellationToken ct)
    {
        if (g.SkipIfMissing != null)
        {
            var probe = Path.Combine(plan.Repo, g.SkipIfMissing);
            if (!File.Exists(probe) && !Directory.Exists(probe))
            {
                onProgress?.Invoke($"gate {g.Name}: skipped ({g.SkipIfMissing} missing)");
                return new GateResult(g.Name, false, true, g.Optional, 0, TimeSpan.Zero, $"skipped — {g.SkipIfMissing} does not exist yet");
            }
        }
        onProgress?.Invoke($"gate {g.Name}: {g.Command}");
        var cwd = string.IsNullOrWhiteSpace(g.Cwd) ? plan.Repo : Path.Combine(plan.Repo, g.Cwd);
        var shell = string.IsNullOrWhiteSpace(g.Shell) ? ProcessRunner.DefaultShell : g.Shell;
        var r = await ProcessRunner.RunShellAsync(shell, g.Command, cwd, TimeSpan.FromMinutes(g.TimeoutMinutes), ct).ConfigureAwait(false);
        var passed = !r.TimedOut && r.ExitCode == 0;
        onProgress?.Invoke($"gate {g.Name}: {(passed ? "PASS" : $"FAIL (exit {r.ExitCode}{(r.TimedOut ? ", timeout" : "")})")} in {r.Duration.TotalSeconds:0}s");
        return new GateResult(g.Name, passed, false, g.Optional, r.ExitCode, r.Duration,
            TailOf(r.Output, 60) + (r.TimedOut ? $"\n[conductor] gate timed out after {g.TimeoutMinutes}m and was killed" : ""));
    }

    public static bool AllRequiredPassed(IEnumerable<GateResult> results)
        => results.All(r => r.Skipped || r.Passed || r.Optional);

    /// <summary>Best-effort lifecycle hook (setup/teardown). Logs its exit code but never blocks the run.</summary>
    public static async Task RunHookAsync(PlanConfig plan, HookConfig? hook, string label, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (hook == null || string.IsNullOrWhiteSpace(hook.Command)) return;
        var cwd = string.IsNullOrWhiteSpace(hook.Cwd) ? plan.Repo : Path.Combine(plan.Repo, hook.Cwd);
        onProgress?.Invoke($"{label}: {hook.Command}");
        var r = await ProcessRunner.RunPowerShellAsync(hook.Command, cwd, TimeSpan.FromMinutes(hook.TimeoutMinutes), ct).ConfigureAwait(false);
        onProgress?.Invoke($"{label}: exit {r.ExitCode}{(r.TimedOut ? " (timed out)" : "")} in {r.Duration.TotalSeconds:0}s");
    }

    public static string Summary(IEnumerable<GateResult> results)
        => string.Join(" · ", results.Select(r => $"{r.Name}:{r.Glyph}"));

    /// <summary>Failing gates with output tails, capped for prompt embedding.</summary>
    public static string FailureDetails(IEnumerable<GateResult> results, int maxCharsPerGate = 4000)
    {
        var parts = results
            .Where(r => !r.Passed && !r.Skipped)
            .Select(r =>
            {
                var tail = r.Tail.Length > maxCharsPerGate ? "…" + r.Tail[^maxCharsPerGate..] : r.Tail;
                return $"### Gate `{r.Name}` FAILED (exit {r.ExitCode}, {r.Duration.TotalSeconds:0}s)\n```\n{tail}\n```";
            });
        return string.Join("\n\n", parts);
    }

    public static string TailOf(string output, int lines)
    {
        var all = output.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', all.TakeLast(lines)).Trim();
    }
}
