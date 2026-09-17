namespace Conductor.Core.Orchestration;

/// <summary>PK4.2 / D7 — the room's cards leave at the verdict, and only then.
///
/// <para>A claim the verdict confirms is QUEUED here rather than posted where it is confirmed: the
/// confirmation happens inside the session's evaluation, and the session's evidence — the before/after
/// pair a card carries — is registered after it (RunLoop's <c>RegisterEvidenceAsync</c>). So the queue
/// drains right after registration, and at a stage's confirmation, where every session's evidence is
/// already in. A claim whose gates go red never reaches <c>ConfirmPendingCheckpoints</c>, so it is
/// never queued and nothing is posted; the words stay on the claim and the next prompt says they are
/// held.</para>
///
/// <para>At most once: the queue is emptied before anything is sent, so a send that fails loses that
/// card instead of repeating it. A room hears about a checkpoint once or not at all, never twice.</para></summary>
public sealed partial class VerdictEngine
{
    private readonly List<string> _cardsDue = [];

    private void QueueCards(IEnumerable<string> confirmed)
    {
        foreach (var id in confirmed)
        {
            if (!_cardsDue.Contains(id, StringComparer.OrdinalIgnoreCase)) _cardsDue.Add(id);
        }
    }

    /// <summary>Posts a card for every checkpoint confirmed since the last drain, in confirmation order.
    /// A checkpoint whose claim carried no words posts nothing (the notifier decides, having the room).</summary>
    internal async Task PostDueCardsAsync(CancellationToken ct)
    {
        if (_cardsDue.Count == 0) return;
        var due = _cardsDue.ToArray();
        _cardsDue.Clear();
        foreach (var id in due)
        {
            try
            {
                await _telegram.PushCheckpointCardAsync(id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
            {
                _ctx.Log($"card for {id} not posted: {ex.Message}");
            }
        }
    }

    /// <summary>The stage card, once, with what changed: the stage's own commit subjects since its
    /// start head, oldest first, with the engine's bookkeeping left out.</summary>
    private async Task PostStageCardAsync(string stageId, IReadOnlyList<string> subjects, CancellationToken ct)
    {
        try
        {
            await _telegram.PushStageCardAsync(stageId, subjects, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            _ctx.Log($"stage card for {stageId} not posted: {ex.Message}");
        }
    }

    private List<string> StageChangeSubjects(string stageId)
    {
        if (!_ctx.State.StageStartHeads.TryGetValue(stageId, out var head) || string.IsNullOrWhiteSpace(head)) return [];
        var oneline = Git.ExcludeBookkeeping(Git.CommitsSince(_ctx.Plan.Repo, head));
        oneline.Reverse();
        return [.. oneline.Select(l => l.IndexOf(' ', StringComparison.Ordinal) is var cut and > 0 ? l[(cut + 1)..].Trim() : l)];
    }
}
