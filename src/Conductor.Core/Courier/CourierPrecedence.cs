namespace Conductor.Core.Courier;

/// <summary>DV4.3 / findings §6.9 — who owns the token on this machine, decided once.
///
/// <para>KS11.1's rule one applies here and shaped the wording: this file is channel-agnostic, so
/// the refusal it returns names the COURIER and the rule, never the messenger. The seam test strips
/// comments and reads string literals, which is the right way round — a messenger's name reaching an
/// operator through channel-agnostic code is a leak whether it came from an identifier or a
/// sentence.</para>
///
/// <para><b>The transition has a 409 waiting inside it.</b> Telegram allows exactly one
/// <c>getUpdates</c> consumer per bot token. The day the courier takes the token, any plan whose
/// telegram block still polls in-run fights it for updates: the two steal each other's messages,
/// each sees a random half, and neither says anything is wrong — a note the owner sent arrives or
/// does not depending on which process asked last. That is not a transport hiccup to retry through;
/// it is two processes both being right about a rule that allows one of them.</para>
///
/// <para>So the rule is stated once, here, and it keys on CONFIGURATION rather than on a running
/// pid. A courier that is written down but not started right now will be started — at the next logon
/// if nothing else (DV4.2) — and a run that polled until that moment would begin fighting it
/// mid-session, which is the worst of the three available behaviours. Deciding from the file makes
/// the verdict the same for a run, for <c>doctor</c> and for a test, none of which can be sure what
/// is running.</para>
///
/// <para><b>A courier-less machine is untouched</b>, and that is the KS11.1 golden-replay standard
/// this checkpoint is held to: with no <c>courier.json</c> there is no refusal, no new branch taken
/// and no line logged — an old-shape plan behaves byte-identically.</para></summary>
public static class CourierPrecedence
{
    /// <summary>Whether this machine has an OPERATIVE courier written down. Half-configured is not
    /// configured: a <c>courier.json</c> with no chats or no projects is one the daemon refuses to
    /// start on (<see cref="CourierSettings.Refusal"/>), so nothing will ever hold the token and a
    /// run that stopped polling for it would go deaf for no reason at all.</summary>
    public static bool Configured(string? stateHomeRoot = null)
    {
        if (!File.Exists(CourierHome.SettingsPathFor(stateHomeRoot))) return false;
        return CourierSettings.Load(stateHomeRoot).Refusal() is null;
    }

    /// <summary>Why this run will not poll Telegram itself, or null when it may. The sentence names
    /// the courier — the whole point of §6.9's rule is that an operator who reads "not polling" can
    /// tell WHICH process took the phone, and type the verb that shows them its state.</summary>
    public static string? PollingRefusal(string? stateHomeRoot = null)
    {
        if (!Configured(stateHomeRoot)) return null;

        var live = CourierPresence.Live(stateHomeRoot);
        var name = live?.TaskName is { Length: > 0 } named ? named : CourierTask.DefaultName;

        return $"the courier \"{name}\" is configured on this machine ({CourierHome.SettingsPathFor(stateHomeRoot)}) "
             + "and owns the bot token. The messenger allows one update consumer per token, so a run "
             + "that also polled would steal half the courier's messages and lose half its own. This "
             + "run pushes through the courier instead. See it: conductor courier status";
    }
}
