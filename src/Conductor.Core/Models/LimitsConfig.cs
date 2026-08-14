using System.Text.Json.Serialization;

namespace Conductor.Models;

public sealed class LimitsConfig
{
    /// <summary>No agent stdout + tool-call output + bg liveness for this long → session considered
    /// stalled and the soft-kill grace window starts (F3.1). If all three signals are quiet.</summary>
    public int StallMinutes { get; set; } = 12;
    /// <summary>After a stall is first detected, the agent gets this many minutes of grace to recover
    /// before being hard-killed (F3.2). Default 3.</summary>
    public int StallGraceMinutes { get; set; } = 3;
    public int SessionTimeoutMinutes { get; set; } = 240;

    // W3.1: sub-minute overrides for the three watchdog rails. Minutes are the right unit for a
    // real plan; they are useless for a toy/rehearsal run whose sessions last seconds (and for the
    // live tests that prove the rails fire at all). Null = use the minute field above.
    /// <summary>Optional seconds-precision override for <see cref="SessionTimeoutMinutes"/>.</summary>
    public int? SessionTimeoutSeconds { get; set; }
    /// <summary>Optional seconds-precision override for <see cref="StallMinutes"/>.</summary>
    public int? StallSeconds { get; set; }
    /// <summary>Optional seconds-precision override for <see cref="StallGraceMinutes"/>.</summary>
    public int? StallGraceSeconds { get; set; }

    /// <summary>The hard session timeout the watchdog enforces.</summary>
    [JsonIgnore]
    public TimeSpan EffectiveSessionTimeout => SessionTimeoutSeconds is { } s
        ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(SessionTimeoutMinutes);
    /// <summary>The all-signals-quiet duration the watchdog enforces.</summary>
    [JsonIgnore]
    public TimeSpan EffectiveStall => StallSeconds is { } s
        ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(StallMinutes);
    /// <summary>The post-stall recovery window the watchdog enforces.</summary>
    [JsonIgnore]
    public TimeSpan EffectiveStallGrace => StallGraceSeconds is { } s
        ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(StallGraceMinutes);

    /// <summary>SC4.1: ceiling in seconds on how long the gate battery waits for the session's own
    /// tracked bg children to exit before it starts judging. A battery taken against a tree the
    /// session is still writing scores the teardown, not the work (devcontext #12). 0 disables the
    /// wait; the cap exists so a dev server the agent left running delays the verdict rather than
    /// blocking the run forever. Default 120.</summary>
    public int BatterySettleSeconds { get; set; } = 120;

    /// <summary>The settle ceiling the battery actually enforces.</summary>
    [JsonIgnore]
    public TimeSpan EffectiveBatterySettle => TimeSpan.FromSeconds(Math.Max(0, BatterySettleSeconds));

    /// <summary>W3.2: ask the agent CLI for one token (~$0.001) before the run's first session, so a
    /// run cannot start on a dead credential. Only recognised provider CLIs are probed. Default true;
    /// set false to opt out.</summary>
    public bool AuthPreflight { get; set; } = true;

    public int MaxResumesPerSession { get; set; } = 2;
    /// <summary>Attempt budget per stage = stage.sessions * this.</summary>
    public int StageSlackFactor { get; set; } = 2;
    /// <summary>Wait time when the agent backend reports a usage/rate limit.</summary>
    public int BackoffMinutes { get; set; } = 30;
    public int MaxBackoffs { get; set; } = 10;
    /// <summary>Maximum total cost (USD) allowed this run before the orchestrator parks at
    /// AwaitingOwner. null = no cap (B3.4).</summary>
    public decimal? MaxRunCostUsd { get; set; }
    /// <summary>Maximum total tokens allowed this run before the orchestrator parks at
    /// AwaitingOwner. null = no cap (B3.4).</summary>
    public long? MaxRunTokens { get; set; }
    /// <summary>If true, the orchestrator parks at <c>AwaitingOwner</c> before each session/commit,
    /// waiting for explicit approval (B3.4).</summary>
    public bool ApprovalMode { get; set; }
    /// <summary>Per-session token budget — a session exceeding this ends <c>RolledOver</c>
    /// with a compact handoff and the next session starts fresh (no attempt burned, B8.5).
    /// null = no per-session limit.</summary>
    public long? MaxSessionTokens { get; set; }
    /// <summary>Soft-break token threshold: when live tokens exceed this (as a fraction of
    /// <c>MaxSessionTokens</c>), the orchestrator injects a "finish current sub-task, write
    /// handoff, end cleanly" nudge signal for the agent (B9.4). Default 0.8 (80%). The
    /// nudge is cooperative — the hard <c>MaxSessionTokens</c> ceiling is the safety net.
    /// Only active when <c>MaxSessionTokens</c> is set. null = 0.8 (default).</summary>
    public double? SoftBreakRatio { get; set; }
    /// <summary>Maximum number of Tier A analysis lanes that may run concurrently (B12.2).
    /// Default 2 — conservative to avoid git/index and build-server contention. Set to 1 to
    /// disable parallelism (lanes still run, just one at a time).</summary>
    public int MaxConcurrentLanes { get; set; } = 2;
    /// <summary>O2: if 2 consecutive sessions stall with zero commits and empty output, skip
    /// directly to NeedsHuman instead of burning the remaining attempts. Default true.</summary>
    public bool StallPatternTermination { get; set; } = true;
    /// <summary>F3.3: if 2 consecutive sessions end with the same non-success outcome and
    /// matching symptoms (same failing gates, same stall pattern, etc.), break the retry cycle
    /// and consult the Advisor instead of queuing another fix/deliver session. Default true.</summary>
    public bool SameFailureCircuitBreaker { get; set; } = true;
    /// <summary>O2: initial backoff delay in minutes after a stalled session. Doubles each
    /// consecutive stall, reset on non-stall outcome. Default 12.</summary>
    public int StallBackoffMinutes { get; set; } = 12;
    /// <summary>O2: DNS health-check config for pre-session network validation.</summary>
    public DnsHealthCheckConfig? DnsHealthCheck { get; set; }
    /// <summary>O3: per-second overhead rate for gate runtime cost estimates.
    /// Default $0.0001/s = $0.36/hr — light compute, different from agent API cost.</summary>
    public decimal OverheadCostPerSecond { get; set; } = 0.0001m;
    /// <summary>F4: verifier score threshold (0-100). A session scoring ≥ this passes verification
    /// and its checkpoints are marked DONE. Below this threshold the findings feed a retry.
    /// Default 80, per-stage overridable in the plan.</summary>
    public int VerifierThreshold { get; set; } = 80;
    /// <summary>G3.3: live session cap for the run — when the run's total session count reaches this,
    /// the loop PARKS at the next session boundary (Paused, with a clear reason) instead of spawning
    /// another session; raising or clearing the cap (Plan tab → Settings, which triggers a live
    /// reload) resumes it. Editable in flight, unlike the process-scoped <c>--max-sessions</c> flag
    /// (which stops the process rather than parking). null/0 = no cap.</summary>
    public int? MaxSessions { get; set; }

    /// <summary>KS2.6: how many notifications ONE park incident may emit. An incident is keyed on
    /// (status, attention reason), so a park that holds unchanged for a week is one incident and one
    /// push, while a new distinct reason opens a new incident and does notify. Default 1; 0 removes
    /// the cap (the pre-KS2.6 behaviour, which is how one handoff mentioning the escalation token in
    /// prose produced roughly two hundred phone notifications on 2026-08-02).
    /// <para>Read by <see cref="Conductor.Core.Integrations.ParkNotifier"/>, which every notify path
    /// in the engine passes through.</para></summary>
    public int MaxPushesPerIncident { get; set; } = Conductor.Core.Integrations.ParkNotifier.DefaultMaxPerIncident;
}

/// <summary>O2: DNS preflight configuration for network health validation before spawning.</summary>
public sealed class DnsHealthCheckConfig
{
    /// <summary>Enable preflight health check before each session. Default true.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Hosts to resolve via DNS. Default: github.com, api.nuget.org.</summary>
    public List<string> Hosts { get; set; } = new() { "github.com", "api.nuget.org" };
    /// <summary>Seconds between health re-checks while parked. Default 60 (used as base for
    /// exponential backoff when <see cref="BackoffMultiplier"/> &gt; 1).</summary>
    public int IntervalSeconds { get; set; } = 60;
    /// <summary>F3.4: minimum free disk space (MB) on the repo drive. Default 100.</summary>
    public long MinFreeDiskMb { get; set; } = 100;
    /// <summary>F3.4: API endpoints to HTTP HEAD check for reachability (e.g. agent backend).
    /// Each URL must return any HTTP response (timeout = failure). Default empty.</summary>
    public List<string> ApiEndpoints { get; set; } = new();
    /// <summary>F3.4: verify git repository is clean and writable. Default true.</summary>
    public bool EnableGitCheck { get; set; } = true;
    /// <summary>F3.4: exponential backoff multiplier for recheck intervals while parked.
    /// Interval doubles (multiplies by this) each consecutive failure, capped by <see cref="MaxBackoffSeconds"/>.
    /// Default 2.0 (doubles each time). Set to 1.0 for fixed-interval parking.</summary>
    public double BackoffMultiplier { get; set; } = 2.0;
    /// <summary>F3.4: maximum backoff interval in seconds. Default 3600 (1 hour).</summary>
    public int MaxBackoffSeconds { get; set; } = 3600;
}
