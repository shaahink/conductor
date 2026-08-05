using System.Diagnostics;
using System.Net;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// F3.4: Pre-flight health check run before each session. Validates DNS, API reachability,
/// disk space, git health, and budget. On failure, the orchestrator parks with exponential
/// backoff and notifies via Telegram.
/// </summary>
public static class PreflightHealth
{
    public sealed record CheckResult(string Name, bool Passed, string Message);

    public static async Task<IReadOnlyList<CheckResult>> RunAllAsync(
        DnsHealthCheckConfig? cfg,
        string? repoPath,
        decimal currentCostUsd,
        decimal? maxRunCostUsd,
        HttpClient? httpClient = null)
    {
        var results = new List<CheckResult>();
        if (cfg is not { Enabled: true }) return results;

        // DNS
        if (cfg.Hosts is { Count: > 0 })
        {
            foreach (var host in cfg.Hosts)
            {
                try
                {
                    await Dns.GetHostEntryAsync(host).ConfigureAwait(false);
                    results.Add(new CheckResult($"dns:{host}", true, "resolved"));
                }
                catch (Exception ex)
                {
                    results.Add(new CheckResult($"dns:{host}", false, ex.Message));
                }
            }
        }

        // API (HTTP) reachability
        if (cfg.ApiEndpoints is { Count: > 0 })
        {
            HttpClient? ownedClient = null;
            HttpClient client;
            if (httpClient != null)
            {
                client = httpClient;
            }
            else
            {
                ownedClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                client = ownedClient;
            }
            try
            {
                foreach (var url in cfg.ApiEndpoints)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        using var request = new HttpRequestMessage(HttpMethod.Head, url);
                        using var resp = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
                        results.Add(new CheckResult($"api:{url}", resp.IsSuccessStatusCode || (int)resp.StatusCode < 500,
                            $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}"));
                    }
                    catch (Exception ex)
                    {
                        results.Add(new CheckResult($"api:{url}", false, ex.Message));
                    }
                }
            }
            finally
            {
                ownedClient?.Dispose();
            }
        }

        // Disk
        if (cfg.MinFreeDiskMb > 0 && repoPath != null)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(repoPath));
                if (root != null)
                {
                    var di = new DriveInfo(root);
                    var freeMb = di.AvailableFreeSpace / (1024 * 1024);
                    var passed = freeMb >= cfg.MinFreeDiskMb;
                    results.Add(new CheckResult("disk", passed,
                        passed
                            ? $"{freeMb} MB free (min {cfg.MinFreeDiskMb} MB)"
                            : $"only {freeMb} MB free, need {cfg.MinFreeDiskMb} MB"));
                }
            }
            catch (Exception ex)
            {
                results.Add(new CheckResult("disk", false, ex.Message));
            }
        }

        // Git
        if (cfg.EnableGitCheck && repoPath != null)
        {
            try
            {
                var psi = new ProcessStartInfo("git", "status --porcelain")
                {
                    WorkingDirectory = repoPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    results.Add(new CheckResult("git", false, "could not start git process"));
                }
                else
                {
                    await proc.WaitForExitAsync().ConfigureAwait(false);
                    var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    if (proc.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
                        results.Add(new CheckResult("git", false, $"exit {proc.ExitCode}: {stderr.Trim()}"));
                    else
                        results.Add(new CheckResult("git", true, "repo accessible"));
                }
            }
            catch (Exception ex)
            {
                results.Add(new CheckResult("git", false, ex.Message));
            }
        }

        // Budget
        if (maxRunCostUsd.HasValue && currentCostUsd >= maxRunCostUsd.Value)
        {
            results.Add(new CheckResult("budget", false,
                $"${currentCostUsd:0.00} ≥ limit ${maxRunCostUsd:0.00}"));
        }

        return results;
    }

    public static bool AllPassed(IReadOnlyList<CheckResult> results) =>
        results.Count > 0 && results.All(r => r.Passed);

    public static bool AnyFailed(IReadOnlyList<CheckResult> results) =>
        results.Count > 0 && !results.All(r => r.Passed);

    /// <summary>
    /// F3.4: compute the next backoff interval using exponential backoff.
    /// On first failure, returns <c>baseSeconds</c>. Each subsequent call increments the
    /// <c>consecutiveFailures</c> counter, multiplying the interval by <c>multiplier</c>,
    /// capped at <c>maxSeconds</c>.
    /// </summary>
    public static int ComputeBackoff(int consecutiveFailures, int baseSeconds, double multiplier, int maxSeconds)
    {
        if (consecutiveFailures <= 1) return baseSeconds;
        if (multiplier <= 1.0) return baseSeconds;
        var seconds = baseSeconds * Math.Pow(multiplier, consecutiveFailures - 1);
        return Math.Min((int)seconds, maxSeconds);
    }
}
