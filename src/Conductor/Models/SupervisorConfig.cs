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

    /// <summary>SF5.3 — where the wake goes when the supervisor is not on this machine.</summary>
    public SupervisorRemote? Remote { get; set; }
}

/// <summary>
/// SF5.3 — remote supervision: the wake leaves the box.
///
/// <para><see cref="SupervisorConfig.Command"/> covers the babysitter that lives on the same machine as
/// the run. It cannot cover the two cases the owner actually has: a phone, and a cloud Claude Code
/// session with repo access. Both need the wake to travel, and both need the SAME brief the local
/// supervisor reads on stdin — a "something happened" ping just makes the remote reader go and look,
/// which is the polling cost this stage exists to delete, paid over a network.</para>
///
/// <code>
///   "supervisor": {
///     "remote": {
///       "webhookUrl": "https://api.github.com/repos/me/site/dispatches",
///       "headers": { "Authorization": "Bearer ${GH_WAKE_TOKEN}", "Accept": "application/vnd.github+json" },
///       "telegram": true
///     }
///   }
/// </code>
///
/// <para>Delivery is best-effort by design: a webhook that is down must not turn a parked run into a
/// second outage, so a failed send is reported on stderr and the watch still exits on its wake code.</para>
/// </summary>
public sealed class SupervisorRemote
{
    /// <summary>Set false to keep the block (and its URL) in the plan while silencing delivery.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Endpoint that receives the wake brief as the POST body, verbatim
    /// (<c>Content-Type: application/json</c>) — the same document the local supervisor gets on stdin.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Headers for the webhook. Values expand <c>${NAME}</c> and <c>%NAME%</c> from the
    /// environment, so the plan can name a credential without ever containing one.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Push a compact wake line to the plan's <c>telegram.allowedChatIds</c>. Uses the same
    /// <c>CONDUCTOR_TELEGRAM_TOKEN</c> (or secrets file) as the run's own pushes — but this send is made
    /// by the <c>watch</c> process, so it still arrives when the engine is the thing that died.</summary>
    public bool Telegram { get; set; }

    /// <summary>How long any one delivery may take. Default 20.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Max remote dispatches per rolling hour; 0 = unlimited. Default 12.
    ///
    /// <para>Its own fuse, separate from <see cref="SupervisorConfig.MaxPerHour"/>, because the two bound
    /// different things and must not silence each other: a webhook that starts a cloud session costs
    /// money and needs bounding, while a local supervisor that has burnt its budget is exactly when the
    /// human most needs the wake to reach them.</para></summary>
    public int MaxPerHour { get; set; } = 12;
}
