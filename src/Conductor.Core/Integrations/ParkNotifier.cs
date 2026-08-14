using Conductor.Core.Evidence;
using Conductor.Core.Integrations.Messaging;

namespace Conductor.Core.Integrations;

/// <summary>
/// KS2.6 — the gate every engine notification passes through, and the reason a park now buzzes a
/// phone once instead of two hundred times.
///
/// <para><b>The incident of 2026-08-02.</b> A tracker handoff MENTIONED the escalation token in
/// prose. <c>ProgressConventions.MentionsHuman</c> is a plain case-insensitive substring over the
/// handoff block (deliberately — the house decree is to fix the flood, not the match), so the run
/// loop read it as a request for a human, parked at <c>NeedsHuman</c>, pushed, and <c>continue</c>d.
/// The park's idle delay was gated on <c>!DryRun</c>, so under <c>--dry-run</c> the next iteration
/// arrived immediately, re-read the same handoff, re-parked and re-pushed — roughly two hundred
/// notifications for one unchanged fact. Nothing in the notify path was rate-limited, because
/// nothing in the notify path had ever been called twice for the same thing.</para>
///
/// <para>Two rules, both of them here so that neither can be half-applied — there are three notify
/// entry points in this engine (<c>RunLoop.Plumbing.Notify</c>, <c>VerdictEngine.Notify</c> and
/// <c>VerdictEngine.NotifyOperator</c>) and rate-limiting one of them leaves the flood open:</para>
/// <list type="number">
/// <item><b>A dry run notifies nobody.</b> <c>--dry-run</c> is a preview: it spawns no agent, spends
/// nothing and must reach nobody's phone. Enforced HERE and not by assuming the notifier is absent,
/// because <c>TelegramService</c> is registered as an <c>IHostedService</c> and IS constructed and
/// started under <c>--dry-run</c> (only its store is null).</item>
/// <item><b>A park emits once per incident.</b> An incident is keyed on (status, attention reason).
/// Repeated identical parks inside one incident are suppressed past <see cref="MaxPerIncident"/>; a
/// NEW distinct reason opens a new incident and does notify; a session that actually runs
/// (<see cref="Resolve"/>) closes the open one, so the same reason reached again after real work is
/// a fresh incident and does buzz.</item>
/// </list>
///
/// <para>Instance-scoped, one per run process, and thread-safe: the notify path is reached from the
/// run loop, from the session watchdog and from control-plane callbacks.</para>
/// </summary>
public sealed class ParkNotifier
{
    /// <summary>One push per incident. Chosen rather than "a few": the second push about an
    /// unchanged fact carries no information and the owner's evidence is that it trains them to stop
    /// reading the first. <c>limits.maxPushesPerIncident</c> raises it; 0 removes the cap.</summary>
    public const int DefaultMaxPerIncident = 1;

    /// <summary>What <c>DeliveryBlocker</c> says while a dry run is muted — the run-start line
    /// otherwise announces that pushes "will deliver" on a run that is guaranteed to send none.</summary>
    public const string DryRunSilence =
        "this is a --dry-run: the run notifies nobody — no Telegram push, no webhook, no notify command";

    private readonly Lock _gate = new();
    private string? _incident;
    private int _sentThisIncident;
    private int _suppressedThisIncident;

    /// <param name="dryRun">The run's <c>RunOptions.DryRun</c>. True silences every leg.</param>
    /// <param name="maxPerIncident">The plan's <c>limits.maxPushesPerIncident</c>. 0 = uncapped;
    /// negative is read as the default, because a nonsense cap must not mean "silence".</param>
    public ParkNotifier(bool dryRun, int maxPerIncident = DefaultMaxPerIncident)
    {
        DryRun = dryRun;
        MaxPerIncident = maxPerIncident < 0 ? DefaultMaxPerIncident : maxPerIncident;
    }

    /// <summary>The run this notifier belongs to spends nothing and tells nobody.</summary>
    public bool DryRun { get; }

    /// <summary>How many pushes one incident may emit. 0 = no cap.</summary>
    public int MaxPerIncident { get; }

    /// <summary>The open incident key, or null when none is open. Diagnostic.</summary>
    public string? OpenIncident { get { lock (_gate) return _incident; } }

    /// <summary>How many pushes the open incident has swallowed. Diagnostic — the run log says this
    /// out loud so a quiet chat is never mistaken for a quiet run.</summary>
    public int SuppressedInIncident { get { lock (_gate) return _suppressedThisIncident; } }

    /// <summary>(status, attention reason) — the pair that identifies an incident. Reason-insensitive
    /// to case and surrounding space so a re-render of the same sentence is the same incident.</summary>
    public static string Key(string status, string? reason)
        => (status ?? "").Trim().ToUpperInvariant() + " | " + (reason ?? "").Trim().ToUpperInvariant();

    /// <summary>A push that is not about a park — run start, session end, run complete, an operator
    /// message from the session watchdog. Not rate-limited (each one is about a different event),
    /// but silent under <c>--dry-run</c> like everything else.</summary>
    public bool AllowOneOff() => !DryRun;

    /// <summary>A push about a park. True at most <see cref="MaxPerIncident"/> times for one
    /// (status, reason) pair, false for every repeat and always false under <c>--dry-run</c>.</summary>
    public bool Admit(string status, string? reason)
    {
        if (DryRun) return false;
        var key = Key(status, reason);
        lock (_gate)
        {
            if (!string.Equals(_incident, key, StringComparison.Ordinal))
            {
                _incident = key;
                _sentThisIncident = 0;
                _suppressedThisIncident = 0;
            }
            if (MaxPerIncident > 0 && _sentThisIncident >= MaxPerIncident)
            {
                _suppressedThisIncident++;
                return false;
            }
            _sentThisIncident++;
            return true;
        }
    }

    /// <summary>Close the open incident: something other than parking happened (a session ran), so
    /// the next park is news again even if it lands on the same sentence. Without this, a run that
    /// parks, is resumed, does real work and parks again on the same cause would be silent for the
    /// rest of its life.</summary>
    public void Resolve()
    {
        lock (_gate)
        {
            _incident = null;
            _sentThisIncident = 0;
            _suppressedThisIncident = 0;
        }
    }
}

/// <summary>KS2.6 — an <see cref="ITelegramService"/> that accepts every push and sends none. It
/// wraps the real service rather than replacing it so <see cref="DeliveryBlocker"/> keeps answering
/// with the real reason when there is one (the run-start readiness line reads it), and only falls
/// back to <see cref="ParkNotifier.DryRunSilence"/> when Telegram would otherwise have delivered.
/// <para>A decorator, not a flag at each call site: there are ten Telegram push call sites in the
/// run path and a dry run has to be silent on all of them, including ones written next year.</para></summary>
internal sealed class MutedTelegramService : ITelegramService
{
    private readonly ITelegramService _inner;

    public MutedTelegramService(ITelegramService inner) => _inner = inner;

    public string? DeliveryBlocker => _inner.DeliveryBlocker ?? ParkNotifier.DryRunSilence;

    public Task PushAsync(string message, PushSeverity severity = PushSeverity.Quiet,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task PushWithKeyboardAsync(string message,
        IReadOnlyList<(string Text, string CallbackData)> buttons,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task PushSessionEndAsync(SessionEndPush push, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task PushRunCompleteAsync(RunCompletePush push, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task PushEvidenceAsync(IReadOnlyList<EvidenceArtifact> artifacts, CancellationToken ct = default)
        => Task.CompletedTask;
}
