using System.Text.Json;
using Conductor.Models;

namespace Conductor.Tests;

public sealed class O2BudgetIntelligenceTests
{
    [Fact]
    public void LimitsConfigDefaults_StallPatternTerminationEnabled()
    {
        var limits = new LimitsConfig();
        Assert.True(limits.StallPatternTermination);
        Assert.Equal(12, limits.StallBackoffMinutes);
        Assert.Null(limits.DnsHealthCheck);
    }

    [Fact]
    public void DnsHealthCheckConfigDefaults_EnabledGitHubAndNuget()
    {
        var dns = new DnsHealthCheckConfig();
        Assert.True(dns.Enabled);
        Assert.Equal(60, dns.IntervalSeconds);
        Assert.Contains("github.com", dns.Hosts);
        Assert.Contains("api.nuget.org", dns.Hosts);
    }

    [Fact]
    public void PlanConfig_DeserializesStallPatternFields()
    {
        const string json = """
        {
          "name": "O2", "repo": ".", "tracker": "t.md",
          "agent": { "command": "opencode", "args": ["run", "{prompt}"] },
          "limits": {
            "stallPatternTermination": false,
            "stallBackoffMinutes": 6,
            "dnsHealthCheck": {
              "enabled": false,
              "hosts": ["gitlab.local"],
              "intervalSeconds": 30
            }
          },
          "stages": [{ "id": "S1", "title": "Test", "sessions": 1 }]
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;

        Assert.False(cfg.Limits.StallPatternTermination);
        Assert.Equal(6, cfg.Limits.StallBackoffMinutes);
        Assert.NotNull(cfg.Limits.DnsHealthCheck);
        Assert.False(cfg.Limits.DnsHealthCheck!.Enabled);
        Assert.Single(cfg.Limits.DnsHealthCheck.Hosts);
        Assert.Equal("gitlab.local", cfg.Limits.DnsHealthCheck.Hosts[0]);
        Assert.Equal(30, cfg.Limits.DnsHealthCheck.IntervalSeconds);
    }

    [Fact]
    public void PlanConfig_DeserializesStallBackoffField()
    {
        const string json = """
        {
          "name": "O2b", "repo": ".", "tracker": "t.md",
          "agent": { "command": "opencode", "args": ["run", "{prompt}"] },
          "limits": { "stallBackoffMinutes": 24 },
          "stages": [{ "id": "S1", "title": "Test", "sessions": 1 }]
        }
        """;
        var cfg = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;

        Assert.Equal(24, cfg.Limits.StallBackoffMinutes);
    }

    [Fact]
    public void LimitsConfig_StallPatternTerminationSerializesRoundTrip()
    {
        var limits = new LimitsConfig
        {
            StallPatternTermination = false,
            StallBackoffMinutes = 20,
            DnsHealthCheck = new DnsHealthCheckConfig
            {
                Enabled = true,
                IntervalSeconds = 120,
                Hosts = new System.Collections.Generic.List<string> { "dev.azure.com" },
            },
        };

        var json = JsonSerializer.Serialize(limits, PlanConfig.JsonOpts);
        var round = JsonSerializer.Deserialize<LimitsConfig>(json, PlanConfig.JsonOpts)!;

        Assert.False(round.StallPatternTermination);
        Assert.Equal(20, round.StallBackoffMinutes);
        Assert.NotNull(round.DnsHealthCheck);
        Assert.True(round.DnsHealthCheck!.Enabled);
        Assert.Equal(120, round.DnsHealthCheck.IntervalSeconds);
        Assert.Single(round.DnsHealthCheck.Hosts);
        Assert.Equal("dev.azure.com", round.DnsHealthCheck.Hosts[0]);
    }
}
