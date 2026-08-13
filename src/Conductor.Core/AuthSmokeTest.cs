using System.Diagnostics;
using Conductor.Core.Accounting;
using Conductor.Core.Providers;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// W3.2: ask the configured agent CLI for one token before committing a run to it.
///
/// The U-series run started on an OAuth token that expired mid-flight; nothing checked, and the
/// failure read as a generic agent error thirteen sessions in. This is the cheapest possible
/// question — the plan's own agent invocation with a one-word prompt, ~$0.001 — asked once at run
/// start and on demand from <c>doctor</c>.
///
/// It probes ONLY a recognised provider CLI. There is no meaningful "one-token ping" for an
/// arbitrary command (a fake agent, a shell wrapper, a test script), and spawning one would cost
/// real time to learn nothing, so those are reported as skipped rather than guessed at.
/// </summary>
public static class AuthSmokeTest
{
    public const string CheckName = "auth";

    /// <summary>The prompt: short enough to be free-ish, specific enough that a healthy CLI answers
    /// immediately instead of starting work.</summary>
    public const string ProbePrompt = "Reply with exactly: ok";

    /// <summary>True when this agent command is a provider CLI we can meaningfully ping.</summary>
    public static bool CanProbe(AgentConfig agent)
    {
        if (agent is null || string.IsNullOrWhiteSpace(agent.Command)) return false;
        var exe = Path.GetFileNameWithoutExtension(agent.Command);
        return exe.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
            || exe.StartsWith("opencode", StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="onSpend">KS5.2 — what the probe was billed, handed to the caller's ledger. The
    /// probe is the plan's own invocation with a one-word prompt: cheap, but not free, and it was the
    /// one model spawn that ran on EVERY run start while contributing nothing to any total. Null when
    /// the wire reported no figure; the callback is invoked either way so the caller can say so.</param>
    public static async Task<PreflightHealth.CheckResult> RunAsync(
        PlanConfig plan, TimeSpan timeout, CancellationToken ct = default,
        Action<SpendReceipt?>? onSpend = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!CanProbe(plan.Agent))
            return new PreflightHealth.CheckResult(CheckName, true,
                $"skipped — no one-token probe defined for agent command '{plan.Agent?.Command}'");

        var provider = AgentProviderFactory.Create(plan.Agent);
        var started = Stopwatch.GetTimestamp();
        var psi = new ProcessStartInfo(plan.Agent.Command)
        {
            WorkingDirectory = Directory.Exists(plan.Repo) ? plan.Repo : Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // The plan's own argv, with the prompt placeholder filled by the ping. Nothing else changes:
        // the point is to exercise the very invocation the run will use, credentials and all.
        foreach (var arg in plan.Agent.Args)
            psi.ArgumentList.Add(arg.Replace("{prompt}", ProbePrompt, StringComparison.Ordinal));

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
                return new PreflightHealth.CheckResult(CheckName, false, $"could not start '{plan.Agent.Command}'");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                // A CLI that cannot answer "ok" inside the window is not proof of a dead token —
                // report it honestly as inconclusive rather than blocking a healthy run.
                return new PreflightHealth.CheckResult(CheckName, true,
                    $"inconclusive — no answer within {timeout.TotalSeconds:0}s");
            }

            var answer = await stdout.ConfigureAwait(false);
            var evidence = answer + " " + (await stderr.ConfigureAwait(false));
            onSpend?.Invoke(BilledSpend.Read(plan.Agent, SpendCategory.AuthProbe, answer,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            if (provider.DetectsAuthFailure(evidence))
                return new PreflightHealth.CheckResult(CheckName, false,
                    $"credential rejected — {Orchestration.SessionRunner.ReauthHint(provider.Name)}");
            if (provider.DetectsUsageLimit(evidence))
                return new PreflightHealth.CheckResult(CheckName, true,
                    "credential valid, but the backend is rate limited right now");
            return proc.ExitCode == 0
                ? new PreflightHealth.CheckResult(CheckName, true, $"{provider.Name} answered a one-token ping")
                : new PreflightHealth.CheckResult(CheckName, true,
                    $"inconclusive — '{plan.Agent.Command}' exited {proc.ExitCode} with no auth error");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new PreflightHealth.CheckResult(CheckName, false, $"could not run '{plan.Agent.Command}': {ex.Message}");
        }
    }
}
