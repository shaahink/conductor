using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class SessionRunner
{
    // ── W3.1: watchdog dispatch (runs on the watchdog thread, never the poll loop) ──

    /// <summary>
    /// Act on a watchdog verdict. The kill goes first and the notification second: the operator
    /// notify command is allowed a minute of its own, and a hung session must not wait on it.
    /// A hung or stalled session notifies immediately — before W3.1 the only thing that ever
    /// reached Telegram was a NeedsHuman park, so bug #8's 337-minute hang was silent.
    /// </summary>
    private void OnWatchdogAction(AgentSession agent, SessionRecord rec, StageConfig stage,
        WatchdogAction action, string message)
    {
        if (action == WatchdogAction.None) return;
        _ctx.Log(message);
        if (action is not (WatchdogAction.StallKill or WatchdogAction.Timeout)) return;

        try { agent.Kill(); } catch (Exception ex) { _ctx.Log($"watchdog kill failed: {ex.Message}"); }
        var what = action == WatchdogAction.Timeout ? "hit the hard timeout" : "stalled (no output)";
        try
        {
            _notify($"Conductor {_ctx.Plan.Name}: session #{rec.Number} ({stage.Id}) {what} — killed by the watchdog. {message}");
        }
        catch (Exception ex) { _ctx.Log($"watchdog notify failed: {ex.Message}"); }
    }

    /// <summary>W3.2: the exact command that fixes it, per provider — a park that just says
    /// "authentication failed" makes the human go find the incantation.</summary>
    internal static string ReauthHint(string providerName) => providerName switch
    {
        "claude" => "run `claude setup-token` (or `claude login`) and resume",
        "opencode" => "run `opencode auth login` and resume",
        _ => "re-authenticate the agent CLI and resume",
    };
}
