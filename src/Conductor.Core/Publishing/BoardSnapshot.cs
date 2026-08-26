using Conductor.Core.Http;

namespace Conductor.Core.Publishing;

/// <summary>
/// DV6.3 — everything the board page says, in the shapes the control plane already serves.
///
/// <para><b>Why the wire DTOs and not the engine's own objects.</b> The page is a fifth reader of
/// the same facts, after the Face, the tracker, the REPORT and the mirror, and every one of those
/// that re-derived a number derived a different one (a card's age was computed three ways before
/// SF3.2 folded it once). <see cref="StateDto"/>, <see cref="TasksDto"/>, <see cref="OwnerQueueDto"/>
/// and <see cref="EvidenceArtifactDto"/> are the projections that already exist and are already
/// pinned by tests; this record is those, plus the two facts a FILE needs and a live view does
/// not — when it was rendered and which boundary it was rendered at.</para>
///
/// <para><b>Nothing is nullable by accident.</b> <paramref name="LedgerLine"/> is empty when the
/// ledger is empty (DV6.1's rule: a line that says "0 open bugs" every day teaches a reader to skip
/// the line that will one day say eleven), and the page then omits the row rather than printing a
/// zero.</para>
/// </summary>
/// <param name="Boundary">What produced this render, in the words a reader can check against the
/// run — "session 18 end". Never "now": the page outlives the instant it was made.</param>
/// <param name="RenderedUtc">The instant the page was rendered. It is the page's whole claim to
/// freshness and it is printed at the top, not hidden in a comment.</param>
public sealed record BoardSnapshot(
    StateDto State,
    TasksDto Tasks,
    OwnerQueueDto Owner,
    IReadOnlyList<EvidenceArtifactDto> Evidence,
    string LedgerLine,
    string Boundary,
    DateTime RenderedUtc);
