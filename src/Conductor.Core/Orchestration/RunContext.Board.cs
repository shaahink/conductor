using Conductor.Core.Publishing;

namespace Conductor.Core.Orchestration;

/// <summary>DV6.3 — the board page's half of the run context: render it at a boundary, and push it
/// out as a document.
///
/// <para><b>Why it lives here and not in the loop.</b> Same reason <c>MirrorBoard</c> does. It is a
/// boundary side effect that must be null-safe, non-blocking and incapable of throwing into the
/// verdict path — and the loop is at its class-coupling ceiling, which is a design bar this run has
/// twice been told about by the analyzer rather than by a reviewer.</para></summary>
public sealed partial class RunContext
{
    /// <summary>Render the board to one self-contained HTML file and push it as a document.
    ///
    /// <para>The failure is LOGGED, never swallowed: a page that quietly stopped being produced two
    /// days ago is DV1.1's defect in a new place — the record's own channel failing in silence.</para>
    ///
    /// <para>The push is fire-and-forget for the same reason the mirror is: a boundary must not wait
    /// on a chat, and a chat that is not configured must not slow one down.</para></summary>
    public void PublishBoard(string boundary, TrackerSnapshot track, DashboardSnapshot dash)
    {
        var published = BoardSnapshotPublisher.Publish(Plan, State, track, dash, Store, boundary,
            DateTime.UtcNow, out var refusal);
        if (published is null)
        {
            Log($"board page not written at {boundary}: {refusal}");
            return;
        }

        Log($"board page written to {published.Path}");
        _ = Messenger.PushBoardSnapshotAsync(published.Path, published.Snapshot);
    }
}
