using System.Globalization;

using Conductor.Core.Courier;
using Conductor.Core.Events;
using Conductor.Core.Evidence;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>PK4.2 / D7 — the cards the ROOM is told, composed by the engine at the verdict.
///
/// <para>A session used to post these itself with <c>report.ps1</c>, right after its claim and before
/// any gate had judged it (F-OBS-2). The words are still the session's — carried on the claim by
/// <c>task --done --tell</c> — but the bar, the counts, the live rule, the footer and the pair are
/// the engine's, and so is the moment: after the claim is confirmed, never before.</para>
///
/// <para>Both cards are templates like every other push (<c>checkpoint-card</c>, <c>stage-card</c>),
/// so a plan can reword them without a build.</para></summary>
public sealed partial class MessageComposer
{
    /// <summary>The card for a confirmed checkpoint, or null when the claim carried no words — a claim
    /// without <c>--tell</c> is not a card, and the engine does not invent one.</summary>
    /// <param name="room">The room it goes to: its footer strings are the room's own words.</param>
    public async Task<CardPush?> CheckpointCardAsync(string checkpointId, Room? room)
    {
        if (_store is null) return null;
        // The confirmation and the session's evidence were emitted a moment ago and persist through an
        // async drain; a card read before it would count one checkpoint short and lose its pair
        // (measured on the PK4.2 rig: the card went out as text with both images registered).
        _store.FlushEvents();
        var events = _store.ReadAllEvents(_state.RunId);
        var graph = new TaskGraph();
        graph.Fold(events);
        if (graph.Find(checkpointId) is not { } item || CardWords.Parse(item.Tell) is not { } words) return null;

        var counts = CardFacts.Count(_plan, graph, item.StageId);
        var pair = CardFacts.Pair(EvidenceRegistry.From(events), checkpointId, ResolveArtifact);
        var facts = CardFactsFor(counts, item.StageId, room);
        facts["title"] = EscapeHtml(words.Title);
        facts["line"] = EscapeHtml(words.Line);
        facts["checkpoint"] = EscapeHtml(checkpointId);

        var text = await ComposeAsync("checkpoint-card", NotifyDefaults.CheckpointCard, facts).ConfigureAwait(false);
        return new CardPush(CardPush.CheckpointKind, checkpointId, text, pair ?? []);
    }

    /// <summary>The card for a confirmed stage: what changed, from the stage's commit subjects.</summary>
    public async Task<CardPush> StageCardAsync(string stageId, IReadOnlyList<string> commitSubjects, Room? room)
    {
        ArgumentNullException.ThrowIfNull(commitSubjects);
        var graph = new TaskGraph();
        if (_store is not null)
        {
            _store.FlushEvents();
            graph.Fold(_store.ReadAllEvents(_state.RunId));
        }

        var counts = CardFacts.Count(_plan, graph, stageId);
        var facts = CardFactsFor(counts, stageId, room);
        var title = _plan.Stages.FirstOrDefault(s => string.Equals(s.Id, stageId, StringComparison.OrdinalIgnoreCase))?.Title;
        facts["title"] = EscapeHtml(string.IsNullOrWhiteSpace(title) ? stageId : title.Trim());
        facts["changes"] = CardFacts.Changes(commitSubjects);

        var text = await ComposeAsync("stage-card", NotifyDefaults.StageCard, facts).ConfigureAwait(false);
        return new CardPush(CardPush.StageKind, stageId, text, []);
    }

    /// <summary>The facts both cards share: the bar, the counts, the room's counts line and footer.</summary>
    private static Dictionary<string, string> CardFactsFor(CardCounts counts, string stageId, Room? room)
    {
        var footer = counts.StageLive ? room?.Footer.Live ?? CardFacts.DefaultLive : room?.Footer.Pending ?? CardFacts.DefaultPending;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bar"] = CardFacts.Bar(counts.Done, counts.Total),
            ["done"] = counts.Done.ToString(CultureInfo.InvariantCulture),
            ["total"] = counts.Total.ToString(CultureInfo.InvariantCulture),
            ["live"] = counts.Live.ToString(CultureInfo.InvariantCulture),
            ["stage"] = EscapeHtml(stageId),
            ["counters"] = EscapeHtml(CardFacts.Fill(room?.Footer.Counters ?? CardFacts.DefaultCounters, counts, stageId)),
            ["footer"] = EscapeHtml(CardFacts.Fill(footer, counts, stageId)),
        };
    }
}
