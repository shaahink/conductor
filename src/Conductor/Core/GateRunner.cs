using Conductor.Models;

namespace Conductor.Core;

public sealed record GateResult(string Name, bool Passed, bool Skipped, bool Optional, int ExitCode, TimeSpan Duration, string Tail)
{
    public string Glyph => Skipped ? "-" : Passed ? "OK" : Optional ? "warn" : "FAIL";
}

public static class GateRunner
{
    /// <param name="fastOnly">When true, only run gates tagged tier "fast" (per-session under perPhase policy).</param>
    /// <param name="currentStage">Gates with a Stages filter only run when the current stage matches.</param>
    /// <param name="onGates">Live per-gate status callback (dashboard timers).</param>
    public static List<GateResult> RunAll(PlanConfig plan, Action<string>? onProgress = null, CancellationToken ct = default,
        bool fastOnly = false, string? currentStage = null, Action<IReadOnlyList<GateProgress>>? onGates = null)
    {
        var gates = plan.Gates
            .Where(g => (!fastOnly || g.IsFast) && g.AppliesToStage(currentStage))
            .ToList();
        var results = new GateResult?[gates.Count];

        // Live status array shared across the (possibly parallel) gate threads.
        var live = gates.Select(g => GateProgress.Pending(g.Name)).ToArray();
        var liveGate = new Lock();
        void Emit() { if (onGates != null) { lock (liveGate) onGates(live.ToArray()); } }
        void Mark(int i, GateProgress gp) { lock (liveGate) live[i] = gp; Emit(); }
        Emit();

        GateResult RunTracked(int i)
        {
            Mark(i, new GateProgress(gates[i].Name, "running", TimeSpan.Zero, DateTime.UtcNow));
            var r = RunOne(plan, gates[i], onProgress, ct);
            var state = r.Skipped ? "skip" : r.Passed ? "pass" : r.Optional ? "warn" : "fail";
            Mark(i, new GateProgress(gates[i].Name, state, r.Duration));
            return r;
        }

        // Walk in listed order; a non-parallel gate is a barrier (runs alone), consecutive
        // parallel gates run concurrently as one batch. Lets `build` gate everyone before the
        // slow independent gates (tests/pnpm/mcp-qa) fan out together.
        var batch = new List<int>();
        void Flush()
        {
            if (batch.Count == 0) return;
            var idx = batch.ToList();
            Parallel.ForEach(idx, new ParallelOptions { MaxDegreeOfParallelism = idx.Count },
                i => results[i] = RunTracked(i));
            batch.Clear();
        }

        for (var i = 0; i < gates.Count; i++)
        {
            if (ct.IsCancellationRequested) { Flush(); break; }
            if (gates[i].Parallel) { batch.Add(i); continue; }
            Flush();
            results[i] = RunTracked(i);
        }
        Flush();

        // any not-yet-populated slots (e.g. cancelled before reached) get a skipped placeholder
        return results.Select((r, i) => r ?? new GateResult(gates[i].Name, false, true, gates[i].Optional, 0, TimeSpan.Zero, "not run (cancelled)")).ToList();
    }

    /// <summary>Signature of the full gate battery for a given tree state — used to skip identical reruns.</summary>
    public static string BatterySignature(PlanConfig plan, string headSha, string? currentStage)
    {
        var names = plan.Gates.Where(g => g.AppliesToStage(currentStage)).Select(g => g.Name).OrderBy(n => n);
        return headSha + "|" + string.Join(",", names);
    }

    private static GateResult RunOne(PlanConfig plan, GateConfig g, Action<string>? onProgress, CancellationToken ct)
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
        var r = ProcessRunner.RunShell(shell, g.Command, cwd, TimeSpan.FromMinutes(g.TimeoutMinutes), ct);
        var passed = !r.TimedOut && r.ExitCode == 0;
        onProgress?.Invoke($"gate {g.Name}: {(passed ? "PASS" : $"FAIL (exit {r.ExitCode}{(r.TimedOut ? ", timeout" : "")})")} in {r.Duration.TotalSeconds:0}s");
        return new GateResult(g.Name, passed, false, g.Optional, r.ExitCode, r.Duration,
            TailOf(r.Output, 60) + (r.TimedOut ? $"\n[conductor] gate timed out after {g.TimeoutMinutes}m and was killed" : ""));
    }

    public static bool AllRequiredPassed(IEnumerable<GateResult> results)
        => results.All(r => r.Skipped || r.Passed || r.Optional);

    /// <summary>Best-effort lifecycle hook (setup/teardown). Logs its exit code but never blocks the run.</summary>
    public static void RunHook(PlanConfig plan, HookConfig? hook, string label, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (hook == null || string.IsNullOrWhiteSpace(hook.Command)) return;
        var cwd = string.IsNullOrWhiteSpace(hook.Cwd) ? plan.Repo : Path.Combine(plan.Repo, hook.Cwd);
        onProgress?.Invoke($"{label}: {hook.Command}");
        var r = ProcessRunner.RunPowerShell(hook.Command, cwd, TimeSpan.FromMinutes(hook.TimeoutMinutes), ct);
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
