namespace Conductor.Models;

/// <summary>DV5.2 / findings §2.3 CL-1 — the cloud lane, and the flag that keeps it off.
///
/// <para>CL-1 is named in the findings as <b>an experiment, behind a flag</b>, and the reason is
/// §2.4: out there conductor has no per-turn telemetry, no stall watchdog, no rollover, no circuit
/// breaker, no control-plane reach and no holdout proof. A lane with none of those is a lane that
/// must be asked for, so <see cref="Enabled"/> is false and there is deliberately no environment
/// override to turn it on by accident — unlike the courier's token, which has one because a machine
/// secret has to arrive from outside a public plan file.</para>
///
/// <para>What the lane may do is bounded by what it CANNOT do: no conductor tools and no verdict.
/// The referee never moves — every gate still runs on this machine, and nothing the cloud says
/// confirms a checkpoint. See <c>CloudLane</c>, where that is a source-scanned invariant rather than
/// a promise.</para></summary>
public sealed class CloudLaneConfig
{
    /// <summary>DEFAULT OFF, and off is the state every existing plan is in without saying anything.</summary>
    public bool Enabled { get; set; }

    /// <summary>How long to wait for the cloud to answer, in minutes. 30 is the CLI's own default for
    /// <c>ultrareview --timeout</c>; it is carried here rather than left implicit because a lane that
    /// hangs is the failure mode §2.4 item 2 says nothing out there will rescue.</summary>
    public int TimeoutMinutes { get; set; } = 30;

    /// <summary>The base the review is taken against — a branch name or a PR number, exactly as the
    /// CLI's positional <c>[target]</c> takes it. Null lets the CLI choose, which is what it does
    /// when the branch has an obvious base.</summary>
    public string? Base { get; set; }

    /// <summary>What a plan got wrong about this block, in one sentence, or null.</summary>
    public string? Refusal() =>
        TimeoutMinutes is < 1 or > 240
            ? $"cloud.timeoutMinutes is {TimeoutMinutes}; it must be between 1 and 240 minutes."
            : null;
}
