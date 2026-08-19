using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core;

public sealed record GateResult(string Name, bool Passed, bool Skipped, bool Optional, int ExitCode, TimeSpan Duration, string Tail)
{
    public bool Cached { get; init; }
    /// <summary>SC4.1: this result is the SECOND run of the gate — the first one failed.</summary>
    public bool Retried { get; init; }
    /// <summary>SC4.1: wall time the discarded first attempt burned. Counted in the cost estimate,
    /// kept OUT of <see cref="Duration"/> so a duration-vs-last-pass comparison stays like-for-like.</summary>
    public TimeSpan FirstAttemptDuration { get; init; }
    public string Glyph => Cached ? "cached" : Skipped ? "-"
        : Passed ? (Retried ? "OK-retry" : "OK")
        : Optional ? "warn" : (Retried ? "FAIL-retry" : "FAIL");
    /// <summary>Estimated overhead cost = Duration × rate (O3). Skipped or cached gates contribute zero.
    /// A retried gate is charged for both attempts — the battery really spent that time.</summary>
    public decimal EstimatedCostUsd(decimal ratePerSecond) =>
        (Skipped || Cached) ? 0m : (decimal)(Duration + FirstAttemptDuration).TotalSeconds * ratePerSecond;

    public bool IsGreen => Skipped || Passed || Optional || Cached;
}

public static class GateRunner
{
    /// <param name="fastOnly">When true, only run gates tagged tier "fast" (per-session under perPhase policy).
    /// Truth-tier gates are excluded from fast-only runs — they only execute at phase confirmation.</param>
    /// <param name="currentStage">Gates with a Stages filter only run when the current stage matches.</param>
    /// <param name="stageKind">Current stage kind — used with StageKinds filter for per-kind gate selection.</param>
    /// <param name="onGates">Live per-gate status callback (dashboard timers).</param>
    /// <param name="db">Optional run.db for per-gate SHA cache lookup.</param>
    /// <param name="runId">Run id for cache key.</param>
    /// <param name="headSha">Current HEAD sha for cache key.</param>
    public static async Task<List<GateResult>> RunAllAsync(PlanConfig plan, Action<string>? onProgress = null, CancellationToken ct = default,
        bool fastOnly = false, string? currentStage = null, string? stageKind = null,
        Action<IReadOnlyList<GateProgress>>? onGates = null,
        IRunStore? db = null, string? runId = null, string? headSha = null)
    {
        var gates = plan.Gates
            .Where(g => g.AppliesToStage(currentStage) && g.AppliesToStageKind(stageKind))
            .Where(g => !fastOnly || g.IsFast)
            .Where(g => !fastOnly || !g.IsTruth)
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
            var gate = gates[i];
            // F7.4: per-gate SHA cache — if this gate already passed at this tier+key, skip execution.
            // SC4.3: the key is the gate's whole world now, not just the primary repo's HEAD.
            if (db != null && runId != null && headSha != null)
            {
                var cachedResult = db.GetLastPassingGateResult(runId, gate.Name, gate.Tier, CacheKey(plan, gate, headSha));
                if (cachedResult is true)
                {
                    Mark(i, new GateProgress(gate.Name, "cached", TimeSpan.Zero));
                    return new GateResult(gate.Name, true, false, gate.Optional, 0, TimeSpan.Zero,
                        $"cached — passed at {headSha[..Math.Min(7, headSha.Length)]}") { Cached = true };
                }
            }
            Mark(i, new GateProgress(gate.Name, "running", TimeSpan.Zero, DateTime.UtcNow));
            var r = await RunOneAsync(plan, gate, onProgress, ct).ConfigureAwait(false);
            var state = r.Cached ? "cached" : r.Skipped ? "skip" : r.Passed ? "pass" : r.Optional ? "warn" : "fail";
            Mark(i, new GateProgress(gate.Name, state, r.Duration));
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

        // SC4.1: one unconditional retry of every REQUIRED gate that failed, before anything is
        // allowed to call this battery red. devcontext #12 analysed the wrong verdict and refuted
        // the tempting duration heuristic ("it failed too fast to be real") — a genuine compile
        // error also fails in two seconds. Running the gate again is the only cheap test that tells
        // a broken tree from a flaky one, and it costs exactly nothing on a green battery. Optional
        // gates are left alone: their failure never blocks a verdict, so a retry buys nothing.
        for (var i = 0; i < gates.Count && !ct.IsCancellationRequested; i++)
        {
            if (results[i] is not { } first || first.IsGreen) continue;
            onProgress?.Invoke($"gate {gates[i].Name}: failed (exit {first.ExitCode} in {first.Duration.TotalSeconds:0}s) — retrying once before the battery is called red");
            Mark(i, new GateProgress(gates[i].Name, "running", TimeSpan.Zero, DateTime.UtcNow));
            var second = await RunOneAsync(plan, gates[i], onProgress, ct).ConfigureAwait(false);
            // A skipIfFresh gate whose own failed run touched the watched artifact would come back
            // "cached" here. That is not a pass — keep the failure the gate actually produced.
            if (second.Cached || second.Skipped)
            {
                Mark(i, new GateProgress(gates[i].Name, first.Optional ? "warn" : "fail", first.Duration));
                continue;
            }
            results[i] = second with
            {
                Retried = true,
                FirstAttemptDuration = first.Duration,
                Tail = $"[conductor] retried once (SC4.1): the first attempt exited {first.ExitCode} after " +
                       $"{first.Duration.TotalSeconds:0}s. Below is the SECOND run.\n{second.Tail}",
            };
            Mark(i, new GateProgress(gates[i].Name, second.Passed ? "pass" : second.Optional ? "warn" : "fail", second.Duration));
        }

        // any not-yet-populated slots (e.g. cancelled before reached) get a skipped placeholder
        return results.Select((r, i) => r ?? new GateResult(gates[i].Name, false, true, gates[i].Optional, 0, TimeSpan.Zero, "not run (cancelled)")).ToList();
    }

    /// <summary>Signature of the full gate battery for a given tree state — used to skip identical reruns.</summary>
    /// <remarks>SC4.3: the gates' COMMANDS are part of the signature, not just their names. A plan
    /// edited mid-run to change what a gate actually executes produced a byte-identical signature at
    /// the same HEAD, so the phase gate reported "tree unchanged since last green battery — reusing
    /// result" for a battery that no longer existed. <see cref="GateConfig.Shell"/> stays out by
    /// B11.1's contract (adding the shell selector must not invalidate existing signatures).</remarks>
    public static string BatterySignature(PlanConfig plan, string headSha, string? currentStage)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var applicable = plan.Gates.Where(g => g.AppliesToStage(currentStage))
            .OrderBy(g => g.Name, StringComparer.Ordinal).ToList();
        var names = applicable.Select(g => g.Name);
        return headSha + "|" + string.Join(",", names) + "|" + CommandDigest(applicable);
    }

    /// <summary>SC4.3: the key one gate's pass is filed and looked up under.
    ///
    /// <para>The F7.4 cache keyed a gate result on the PRIMARY repo's HEAD alone, which answers a
    /// different question than the one the cache is asked. A gate whose <c>cwd</c> is a sibling repo
    /// was served a 40-minute-old pass for a tree that had changed underneath it — the sibling's
    /// commits are invisible to this repo's HEAD. And a gate whose command was edited mid-run kept
    /// being served the OLD command's pass, because the key never mentioned what the gate runs.</para>
    ///
    /// <para>So the key carries all three: the primary HEAD, the gate's own working directory HEAD
    /// (or the newest write time under its declared <see cref="GateConfig.WatchPaths"/>, for a cwd
    /// that is not itself a repo), and a digest of the command text. Anything the key cannot read —
    /// a missing directory, a cwd outside git — degrades to a marker that simply never matches a
    /// previous key, which costs one gate run and never a false pass.</para>
    /// </summary>
    public static string CacheKey(PlanConfig plan, GateConfig gate, string headSha)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(gate);
        var cwd = ResolveCwd(plan, gate);
        var parts = new List<string>(4) { headSha, "cwd:" + CwdMarker(cwd) };
        if (gate.WatchPaths is { Count: > 0 })
            parts.Add("watch:" + WatchMarker(plan, gate));
        parts.Add("cmd:" + CommandDigest([gate]));
        return string.Join("|", parts);
    }

    /// <summary>The gate's working directory HEAD, or a marker that cannot match a stale key. When the
    /// cwd is inside the primary repo this is simply the primary HEAD, so single-repo plans keep the
    /// behaviour they had.</summary>
    private static string CwdMarker(string cwd)
    {
        if (!Directory.Exists(cwd)) return "absent";
        var r = Git.Exec(cwd, "rev-parse", "HEAD");
        var sha = r.Output.Trim();
        return r.ExitCode == 0 && sha.Length >= 7 && sha.All(Uri.IsHexDigit) ? sha : "nogit";
    }

    /// <summary>Newest last-write time under the gate's declared watch paths, as ticks. The escape
    /// hatch for a gate whose inputs are not under any git HEAD (generated sources, a vendored drop).</summary>
    private static string WatchMarker(PlanConfig plan, GateConfig gate)
    {
        long newest = 0;
        foreach (var rel in gate.WatchPaths!)
        {
            if (string.IsNullOrWhiteSpace(rel)) continue;
            var full = Path.IsPathRooted(rel) ? rel : Path.Combine(plan.Repo, rel);
            try
            {
                if (File.Exists(full)) newest = Math.Max(newest, File.GetLastWriteTimeUtc(full).Ticks);
                else if (Directory.Exists(full))
                    foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                        newest = Math.Max(newest, File.GetLastWriteTimeUtc(f).Ticks);
            }
            catch (IOException) { return "unreadable"; }
            catch (UnauthorizedAccessException) { return "unreadable"; }
        }
        return newest.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Short stable digest of what a set of gates actually RUNS — command text and working
    /// directory. Not a security boundary; it only has to change when the gate does.</summary>
    private static string CommandDigest(IEnumerable<GateConfig> gates)
    {
        var text = string.Join(" ", gates.Select(g => $"{g.Name}{g.Command}{g.Cwd}"));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string ResolveCwd(PlanConfig plan, GateConfig g)
        => string.IsNullOrWhiteSpace(g.Cwd) ? plan.Repo : Path.Combine(plan.Repo, g.Cwd);

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
        // F7.5: skipIfFresh — skip if the output artifact exists and is newer than the newest
        // change to the source. This avoids re-running builds when nothing has changed since the
        // last successful run.
        // SC4.3: "newest change" used to mean the last COMMIT, and a session's work is uncommitted
        // for almost its whole length — so a build output left over from BEFORE the agent started
        // editing still dated newer than the last commit, and every skipIfFresh gate skipped
        // straight over the changes it exists to check. The clock is now the last commit OR a newer
        // uncommitted edit, whichever is later, with the artifact itself excluded from the scan so
        // an untracked output never dates itself fresh.
        if (g.SkipIfFresh is { } freshPath)
        {
            var fullFresh = Path.Combine(plan.Repo, freshPath);
            if (File.Exists(fullFresh) || Directory.Exists(fullFresh))
            {
                var freshTime = Directory.Exists(fullFresh) && !File.Exists(fullFresh)
                    ? Directory.GetLastWriteTimeUtc(fullFresh)
                    : File.GetLastWriteTimeUtc(fullFresh);
                try
                {
                    var mostRecentChange = Git.MostRecentChangeTime(plan.Repo, freshPath);
                    if (mostRecentChange is { } changeTime && freshTime > changeTime)
                    {
                        onProgress?.Invoke($"gate {g.Name}: cached (output at {freshPath} is fresh — newer than the last commit and than every uncommitted change)");
                        return new GateResult(g.Name, true, false, g.Optional, 0, TimeSpan.Zero,
                            $"cached — output at {freshPath} is fresh") { Cached = true };
                    }
                    if (mostRecentChange is { } t && Git.IsDirty(plan.Repo))
                        onProgress?.Invoke($"gate {g.Name}: running — the working tree has changes newer than {freshPath} (source {t:HH:mm:ss}Z vs output {freshTime:HH:mm:ss}Z)");
                }
                catch (IOException) { /* freshness check is best-effort — run the gate if it fails */ }
                catch (UnauthorizedAccessException) { /* ditto */ }
            }
        }
        var cwd = ResolveCwd(plan, g);

        // KS0.3, bug #16: never rebuild the image this process is running from.
        var command = g.Command;
        if (ShadowBuild.For(g.Command, plan.Repo, Environment.ProcessPath, ShadowBuild.RootFor(plan.Repo))
            is { } shadow)
        {
            command = shadow.Command;
            onProgress?.Invoke($"gate {g.Name}: {shadow.Why}");
        }

        // Logged AFTER the redirect, and it is the command that actually ran — not the one the plan
        // asked for. When the two differ the line above says why; a log that names a command the
        // engine did not execute is how a gate failure gets debugged against the wrong command line.
        onProgress?.Invoke($"gate {g.Name}: {command}");

        var shell = string.IsNullOrWhiteSpace(g.Shell) ? ProcessRunner.DefaultShell : g.Shell;
        var r = await ProcessRunner.RunShellAsync(shell, command, cwd, TimeSpan.FromMinutes(g.TimeoutMinutes), ct).ConfigureAwait(false);
        var passed = !r.TimedOut && r.ExitCode == 0;
        onProgress?.Invoke($"gate {g.Name}: {(passed ? "PASS" : $"FAIL (exit {r.ExitCode}{(r.TimedOut ? ", timeout" : "")})")} in {r.Duration.TotalSeconds:0}s");
        return new GateResult(g.Name, passed, false, g.Optional, r.ExitCode, r.Duration,
            TailOf(r.Output, 60) + (r.TimedOut ? $"\n[conductor] gate timed out after {g.TimeoutMinutes}m and was killed" : ""));
    }

    public static bool AllRequiredPassed(IEnumerable<GateResult> results)
        => results.All(r => r.IsGreen);

    /// <summary>Best-effort lifecycle hook (setup/teardown). Logs its exit code but never blocks the run.</summary>
    public static async Task RunHookAsync(PlanConfig plan, HookConfig? hook, string label, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (hook == null || string.IsNullOrWhiteSpace(hook.Command)) return;
        var cwd = string.IsNullOrWhiteSpace(hook.Cwd) ? plan.Repo : Path.Combine(plan.Repo, hook.Cwd);
        onProgress?.Invoke($"{label}: {hook.Command}");
        var r = await ProcessRunner.RunPowerShellAsync(hook.Command, cwd, TimeSpan.FromMinutes(hook.TimeoutMinutes), ct).ConfigureAwait(false);
        onProgress?.Invoke($"{label}: exit {r.ExitCode}{(r.TimedOut ? " (timed out)" : "")} in {r.Duration.TotalSeconds:0}s");
    }

    /// <summary>U0.3: a plan with no gates configured must say so honestly — an empty string here
    /// read as a blank/missing field everywhere it's embedded (session log, REPORT.md, the prompt),
    /// indistinguishable from "gate info didn't make it into the record". "gates green (none
    /// configured)" is the one true verdict for a gateless plan: <see cref="AllRequiredPassed"/>
    /// already returns true on an empty list (vacuous), this just makes the TEXT match the verdict.</summary>
    public static string Summary(IEnumerable<GateResult> results)
    {
        var list = results as ICollection<GateResult> ?? results.ToList();
        return list.Count == 0 ? "gates green (none configured)" : string.Join(" · ", list.Select(r => $"{r.Name}:{r.Glyph}"));
    }

    /// <summary>SC2.2: THE canonical gate verdict token — one spelling for every log line that reports a
    /// battery, session verdict and phase gate alike. The single most consequential line in a run (a
    /// phase-gate RED) used to be spelled differently from the session verdict, so a watcher filtering on
    /// one grammar saw eleven minutes of silence and then a Fix session with no stated cause
    /// (devcontext #18). Three-valued on purpose: an empty battery is vacuously "all required passed",
    /// and calling that GREEN is exactly the lie SC2.2 exists to kill.</summary>
    public static string Token(IEnumerable<GateResult> results)
    {
        var list = results as ICollection<GateResult> ?? results.ToList();
        if (list.Count == 0) return "gates NONE";
        return AllRequiredPassed(list) ? "gates GREEN" : "gates RED";
    }

    /// <summary>SC2.2: what a stage confirmation actually rests on, in the three honest states the
    /// confirmation line must distinguish. Nine of thirteen stages on one run logged "CONFIRMED (full
    /// battery green)" when <em>no battery existed</em> for them (sk-platform #2) — gates were scoped per
    /// stage via <c>gates[].stages</c> and those stages matched none.</summary>
    /// <param name="configuredForStage">Gates the plan scopes to this stage — the difference between
    /// "no gates exist for this stage" and "the battery result is not on record right now" (a reused
    /// battery after a restart), which must never be reported as the same thing.</param>
    /// <param name="reused">The battery was not re-run: an identical tree already passed it.</param>
    public static string ConfirmationBasis(int configuredForStage, IEnumerable<GateResult>? results, bool reused = false)
    {
        if (configuredForStage == 0)
            return "no gates configured for this stage — advanced on claims, commits and tracker diff alone";

        var list = results as ICollection<GateResult> ?? results?.ToList();
        if (list is null || list.Count == 0)
            return $"{configuredForStage} gate(s) configured for this stage but no battery result on record";

        var suffix = reused ? ", reused on an unchanged tree" : "";
        return AllRequiredPassed(list)
            ? $"gates GREEN: {string.Join(", ", list.Select(r => r.Name))}{suffix}"
            : $"gates RED: {string.Join(", ", list.Where(r => !r.IsGreen).Select(r => r.Name))} — confirmed anyway";
    }

    /// <summary>Gates the plan scopes to a given stage — the denominator behind
    /// <see cref="ConfirmationBasis"/> and doctor's zero-gate-stage warning.</summary>
    public static int ConfiguredForStage(PlanConfig plan, StageConfig stage)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(stage);
        return plan.Gates.Count(g => g.AppliesToStage(stage.Id) && g.AppliesToStageKind(stage.Kind));
    }

    /// <summary>Failing gates with output tails, capped for prompt embedding.</summary>
    public static string FailureDetails(IEnumerable<GateResult> results, int maxCharsPerGate = 4000)
    {
        var parts = results
            .Where(r => !r.Passed && !r.Skipped)
            .Select(r =>
            {
                var tail = r.Tail.Length > maxCharsPerGate ? "…" + r.Tail[^maxCharsPerGate..] : r.Tail;
                // SC4.1: say it was retried. A fix session that knows the gate failed TWICE does not
                // waste its first move re-running it to see whether the battery was just unlucky.
                var retried = r.Retried ? ", failed twice — retried once" : "";
                return $"### Gate `{r.Name}` FAILED (exit {r.ExitCode}, {r.Duration.TotalSeconds:0}s{retried})\n```\n{tail}\n```";
            });
        return string.Join("\n\n", parts);
    }

    public static string TailOf(string output, int lines)
    {
        var all = output.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', all.TakeLast(lines)).Trim();
    }
}
