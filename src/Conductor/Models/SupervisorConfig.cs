namespace Conductor.Models;

/// <summary>
/// SF5.2 — the babysitter, named in the plan instead of remembered in a shell history.
///
/// <para><c>conductor watch --hook '…'</c> already runs a command on wake. That is the right mechanism
/// and the wrong place to keep it: the supervisor is a property of the run — this plan is watched by
/// that agent, under these standing orders — not of whoever last typed the loop. Put it in the plan and
/// the supervision survives the terminal it was started from, ships with the repo, and can be reviewed
/// in a diff like everything else the run does.</para>
///
/// <para>Costs nothing while quiet: the command is invoked only when the wake set fires. The whole
/// point of SF5 is that the waiting is a file-stat loop and the expensive reader thinks once, at the
/// moment that needed it.</para>
///
/// <code>
///   "supervisor": {
///     "command": "claude -p \"You are the night watch. The wake brief is on stdin. Read your standing orders in it.\"",
///     "timeoutMinutes": 10,
///     "maxPerHour": 6,
///     "standingOrders": "You may: conductor approve an owner gate whose checkpoint has evidence; conductor inject a hint on a circuit breaker. You must escalate: anything that spends money, any merge, any plan edit."
///   }
/// </code>
/// </summary>
public sealed class SupervisorConfig
{
    /// <summary>Set false to keep the block (and its standing orders) in the plan while silencing the
    /// command — the reviewable way to turn a babysitter off for one night.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The command, run through the platform shell with the brief on stdin. Blank disables the
    /// block as surely as <see cref="Enabled"/>=false, because a supervisor with no command is not a
    /// supervisor and should not read as one.</summary>
    public string Command { get; set; } = "";

    /// <summary>How long the command may run before it is killed. Default 10.</summary>
    public int TimeoutMinutes { get; set; } = 10;

    /// <summary>Max supervisor invocations per rolling hour; 0 = unlimited. Default 6.
    ///
    /// <para>This is a cost fuse, not a nicety. The shipped shape is a shell loop re-arming the watch
    /// after every wake, so a run that parks, is resumed by the supervisor, and parks again on the same
    /// cause is a model invocation every few seconds until someone notices the bill. Six an hour is
    /// enough for a real night and cheap enough to be wrong about.</para></summary>
    public int MaxPerHour { get; set; } = 6;

    /// <summary>What the supervisor may decide alone, and what it must escalate. Carried INTO the brief
    /// (<c>standingOrders</c>), so the agent reads its authority on the same stdin as the wake rather
    /// than being trusted to have been told separately. Null = no orders stated, which a careful
    /// supervisor should read as "escalate everything".</summary>
    public string? StandingOrders { get; set; }
}
