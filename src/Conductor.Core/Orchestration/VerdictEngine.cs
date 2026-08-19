using System.Text;
using Conductor.Core.Events;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Lanes;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

public sealed partial class VerdictEngine
{
    private readonly RunContext _ctx;
    private readonly GateOrchestrator _gates;
    private readonly LaneCoordinator _lanes;
    private readonly IRunNotifier _telegram;
    private readonly WebhookNotifier _webhooks;
    private readonly Action _saveAndReport;
    private readonly Action _pushIdleSnapshot;

    public VerdictEngine(
        RunContext ctx,
        GateOrchestrator gates,
        LaneCoordinator lanes,
        IRunNotifier telegram,
        WebhookNotifier webhooks,
        Action saveAndReport,
        Action pushIdleSnapshot)
    {
        _ctx = ctx;
        _gates = gates;
        _lanes = lanes;
        _telegram = telegram;
        _webhooks = webhooks;
        _saveAndReport = saveAndReport;
        _pushIdleSnapshot = pushIdleSnapshot;
    }

    // ── M4.1: claims vs confirmations ──

    private void ConfirmPendingCheckpoints(string stageId, int? sessionNumber = null)
    {
        if (_ctx.State.PendingConfirmation.Count == 0) return;
        var ids = _ctx.State.PendingConfirmation.ToArray();
        // W1.1: this emits CheckpointConfirmed into the work graph — the fold that TrackerGenerator's
        // "DONE ✓" and every other view read. Confirmation stays the engine's verdict alone.
        _ctx.Store?.ConfirmCheckpoints(_ctx.State.RunId, ids, sessionNumber);
        _ctx.Log($"confirmed {ids.Length} checkpoint(s) for stage {stageId}: [{string.Join(", ", ids)}]");
        _ctx.State.PendingConfirmation.Clear();
    }

    // ── static helpers ──

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "\u2026";

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "?" : sha.Length >= 7 ? sha[..7] : sha;

    // ── instance helpers ──

    private int MaxAttempts(StageConfig stage) => StageSelection.MaxAttempts(_ctx.Plan, stage);

    /// <summary>SC4.3: the ONE place a finished session's commits are collected, primary repo and
    /// declared satellites together. Four call sites used to read <c>Git.CommitsSince</c> on the
    /// primary repo alone, so a session that delivered in a sibling looked idle from every one
    /// of them.</summary>
    private void CollectCommits(SessionRecord rec, string startHead)
    {
        rec.NewCommits = Git.CommitsSince(_ctx.Plan.Repo, startHead);
        rec.SatelliteCommits = SatelliteRepos.CommitsSince(_ctx.Plan, rec.SatelliteStartHeads);
    }

    /// <summary>SC4.1: every battery in this engine goes through here, so this is the one place the
    /// settle has to live. The session judged is the last one on record — the one that just exited
    /// for a session battery, the stage's final one for a phase gate or the closing battery.</summary>
    private Task SettleBeforeGatesAsync(CancellationToken ct) =>
        BatterySettler.SettleAsync(
            _ctx.Store, _ctx.State.RunId,
            _ctx.State.History.Count > 0 ? _ctx.State.History[^1].Number : null,
            _ctx.Plan.Limits.EffectiveBatterySettle, _ctx.LogWithOutcome, ct: ct);

    private async Task<IReadOnlyList<GateResult>> RunGateBatteryAsync(CancellationToken ct, bool fastOnly = false)
    {
        await SettleBeforeGatesAsync(ct).ConfigureAwait(false);
        _ctx.CurGate = fastOnly ? "battery:fast" : "battery:full";
        try
        {
            return await _gates.RunBatteryAsync(_ctx.Log, _ctx.LogWithOutcome, _ctx.Sink.GateProgress, ct, fastOnly).ConfigureAwait(false);
        }
        finally { _ctx.CurGate = null; }
    }

    private void EmitGates(IReadOnlyList<GateResult> gates, string scope, string? sessionId = null)
    {
        _gates.PersistGates(gates, scope, sessionId);
    }

    /// <remarks>K5.4: <paramref name="telegram"/> is false only where a COMPOSED push replaces this
    /// sentence on the Telegram leg — the webhooks and the notify command still get the sentence.</remarks>
    private void Notify(string message, PushSeverity severity = PushSeverity.Quiet, bool telegram = true)
    {
        if (!_ctx.Notifier.AllowOneOff()) return;   // KS2.6: a dry run reaches nobody, on every leg
        if (telegram) _ = _telegram.PushAsync(message, severity);
        _webhooks.FireAsync(message);

        var n = _ctx.Plan.Notify;
        if (n == null || string.IsNullOrWhiteSpace(n.Command)) return;
        try
        {
            var args = n.Args.Select(a => a.Replace("{message}", message));
            ProcessRunner.Run(n.Command, args, _ctx.Plan.Repo, TimeSpan.FromMinutes(1));
        }
        catch (Exception ex) { _ctx.Log($"notify failed: {ex.Message}"); }
    }

    public void ReflectionStep(SessionRecord rec)
    {
        if (string.IsNullOrWhiteSpace(rec.ResultSummary)) return;

        var parsed = SessionResult.Parse(rec.ResultSummary);
        if (!parsed.HasMarker) return;

        // K5.1: a structured result hands the ledger its bullets and its gaps — the parts that can
        // carry a rule. The headline is status by construction, and status teaches nobody anything.
        var difficulty = parsed.IsStructured
            ? parsed.ForLessons()
            : parsed.Raw[SessionResult.Marker.Length..].Trim();
        if (difficulty.Length == 0) return;

        // K1.3: the whole SESSION-RESULT goes in, un-truncated. It used to be cut at 500 characters
        // and pasted verbatim, which is what made lessons.md a file of narratives sheared mid-word.
        // LessonsManager extracts the rule-shaped sentences and writes nothing when there are none \u2014
        // so a status-only result now costs the next prompt nothing instead of 500 characters of it.
        _ctx.Lessons.Append(rec.Stage, rec.Number, difficulty);
    }

    // ── main-loop entry points: closing the plan lives in VerdictEngine.Completion.cs ──

    /// <summary>W3.1: the operator notification path (Telegram + webhook + notify command), exposed
    /// so the session watchdog can raise a hung or stalled session the moment it kills it. Before
    /// W3.1 only a NeedsHuman park ever notified, so a hang stayed silent until a human looked.</summary>
    public void NotifyOperator(string message) => Notify(message);

    public void NeedsHuman(string reason)
    {
        _ctx.State.Status = RunStatus.NeedsHuman;
        _ctx.State.SetAttention(reason);
        _ctx.Events.Emit(new AttentionRequested { Reason = reason });
        _ctx.Log($"🛑 NEEDS HUMAN: {reason}");
        _saveAndReport();
        // KS2.6: the park buzzes ONCE. The 2026-08-02 incident is this very line reached again and
        // again over one unchanged fact — a handoff MENTIONING the escalation token in prose — while
        // nothing counted. The run holds parked for as long as it takes and says so once; a
        // DIFFERENT reason is a different incident and does buzz. See ParkNotifier.
        if (!_ctx.Notifier.Admit(nameof(RunStatus.NeedsHuman), reason)) return;
        // K5.4: the whole point of severity. The run has stopped and cannot restart itself — this is
        // the one message that has earned the right to buzz a phone at 3am.
        Notify($"Conductor {_ctx.Plan.Name}: needs attention — {reason}", PushSeverity.Alert);
        // KS9.2: same beat as the buzz, and gated by the same Admit — a park that has already been
        // announced does not re-push a board that has not changed.
        _ctx.MirrorBoard("needs-human");
        _ = _telegram.PushWithKeyboardAsync(reason,
        [
            ("Resume", "resume"),
            ("Skip Stage", "skip"),
            ("Inject\u2026", "inject:needsHuman"),
            ("Chat", "chat:needsHuman"),
        ]);
    }

}
