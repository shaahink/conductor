using Conductor.Models;

namespace Conductor.Core;

public sealed record GateResult(string Name, bool Passed, bool Skipped, bool Optional, int ExitCode, TimeSpan Duration, string Tail)
{
    public string Glyph => Skipped ? "-" : Passed ? "OK" : Optional ? "warn" : "FAIL";
}

public static class GateRunner
{
    public static List<GateResult> RunAll(PlanConfig plan, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var results = new List<GateResult>();
        foreach (var g in plan.Gates)
        {
            if (ct.IsCancellationRequested) break;
            if (g.SkipIfMissing != null)
            {
                var probe = Path.Combine(plan.Repo, g.SkipIfMissing);
                if (!File.Exists(probe) && !Directory.Exists(probe))
                {
                    results.Add(new GateResult(g.Name, false, true, g.Optional, 0, TimeSpan.Zero, $"skipped — {g.SkipIfMissing} does not exist yet"));
                    onProgress?.Invoke($"gate {g.Name}: skipped ({g.SkipIfMissing} missing)");
                    continue;
                }
            }
            onProgress?.Invoke($"gate {g.Name}: {g.Command}");
            var cwd = string.IsNullOrWhiteSpace(g.Cwd) ? plan.Repo : Path.Combine(plan.Repo, g.Cwd);
            var r = ProcessRunner.RunPowerShell(g.Command, cwd, TimeSpan.FromMinutes(g.TimeoutMinutes), ct);
            var passed = !r.TimedOut && r.ExitCode == 0;
            results.Add(new GateResult(g.Name, passed, false, g.Optional, r.ExitCode, r.Duration,
                TailOf(r.Output, 60) + (r.TimedOut ? $"\n[conductor] gate timed out after {g.TimeoutMinutes}m and was killed" : "")));
            onProgress?.Invoke($"gate {g.Name}: {(passed ? "PASS" : $"FAIL (exit {r.ExitCode}{(r.TimedOut ? ", timeout" : "")})")} in {r.Duration.TotalSeconds:0}s");
        }
        return results;
    }

    public static bool AllRequiredPassed(IEnumerable<GateResult> results)
        => results.All(r => r.Skipped || r.Passed || r.Optional);

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
