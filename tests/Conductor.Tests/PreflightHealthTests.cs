using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public sealed class PreflightHealthTests
{
    // ---------------------------------------------------------------- ComputeBackoff

    [Fact]
    public void ComputeBackoff_FirstFailure_ReturnsBase()
    {
        var backoff = PreflightHealth.ComputeBackoff(1, 60, 2.0, 3600);
        Assert.Equal(60, backoff);
    }

    [Fact]
    public void ComputeBackoff_SecondFailure_Doubles()
    {
        var backoff = PreflightHealth.ComputeBackoff(2, 60, 2.0, 3600);
        Assert.Equal(120, backoff);
    }

    [Fact]
    public void ComputeBackoff_ThirdFailure_Quadruples()
    {
        var backoff = PreflightHealth.ComputeBackoff(3, 60, 2.0, 3600);
        Assert.Equal(240, backoff);
    }

    [Fact]
    public void ComputeBackoff_RespectsMaxCap()
    {
        var backoff = PreflightHealth.ComputeBackoff(10, 60, 2.0, 300);
        Assert.Equal(300, backoff);
    }

    [Fact]
    public void ComputeBackoff_MultiplierOne_AlwaysReturnsBase()
    {
        Assert.Equal(60, PreflightHealth.ComputeBackoff(1, 60, 1.0, 3600));
        Assert.Equal(60, PreflightHealth.ComputeBackoff(5, 60, 1.0, 3600));
    }

    [Fact]
    public void ComputeBackoff_ConsecutiveFailures_ExponentialGrowth()
    {
        var results = Enumerable.Range(1, 5)
            .Select(n => PreflightHealth.ComputeBackoff(n, 60, 2.0, 3600))
            .ToList();
        Assert.Equal(new[] { 60, 120, 240, 480, 960 }, results);
    }

    // ---------------------------------------------------------------- AllPassed / AnyFailed

    [Fact]
    public void AllPassed_EmptyList_ReturnsFalse()
    {
        Assert.False(PreflightHealth.AllPassed(new List<PreflightHealth.CheckResult>()));
    }

    [Fact]
    public void AllPassed_AllGreen_ReturnsTrue()
    {
        var results = new List<PreflightHealth.CheckResult>
        {
            new("dns:github.com", true, "resolved"),
            new("disk", true, "500 MB free"),
        };
        Assert.True(PreflightHealth.AllPassed(results));
    }

    [Fact]
    public void AllPassed_OneFails_ReturnsFalse()
    {
        var results = new List<PreflightHealth.CheckResult>
        {
            new("dns:github.com", true, "resolved"),
            new("disk", false, "only 10 MB free"),
        };
        Assert.False(PreflightHealth.AllPassed(results));
    }

    [Fact]
    public void AnyFailed_NoResults_ReturnsFalse()
    {
        Assert.False(PreflightHealth.AnyFailed(new List<PreflightHealth.CheckResult>()));
    }

    // ---------------------------------------------------------------- RunAllAsync: disabled

    [Fact]
    public async Task RunAllAsync_Disabled_ReturnsEmpty()
    {
        var results = await PreflightHealth.RunAllAsync(null, "/tmp", 0m, null);
        Assert.Empty(results);
    }

    [Fact]
    public async Task RunAllAsync_NotEnabled_ReturnsEmpty()
    {
        var cfg = new DnsHealthCheckConfig { Enabled = false };
        var results = await PreflightHealth.RunAllAsync(cfg, "/tmp", 0m, null);
        Assert.Empty(results);
    }

    // ---------------------------------------------------------------- RunAllAsync: budget

    [Fact]
    public async Task RunAllAsync_BudgetExceeded_ReportsFailure()
    {
        var cfg = new DnsHealthCheckConfig
        {
            Enabled = true,
            Hosts = new List<string>(),
            MinFreeDiskMb = 0,
            EnableGitCheck = false,
        };
        var results = await PreflightHealth.RunAllAsync(
            cfg, null, currentCostUsd: 5.50m, maxRunCostUsd: 5.00m);
        var budget = results.SingleOrDefault(r => r.Name == "budget");
        Assert.NotNull(budget);
        Assert.False(budget!.Passed);
    }

    [Fact]
    public async Task RunAllAsync_BudgetWithinLimit_NoBudgetCheck()
    {
        var cfg = new DnsHealthCheckConfig
        {
            Enabled = true,
            Hosts = new List<string>(),
            MinFreeDiskMb = 0,
            EnableGitCheck = false,
        };
        var results = await PreflightHealth.RunAllAsync(
            cfg, null, currentCostUsd: 3.00m, maxRunCostUsd: 5.00m);
        Assert.DoesNotContain(results, r => r.Name == "budget");
    }
}
