using Conductor.Core.Evidence;
using Conductor.Core.Http;
using System.Net;

namespace Conductor.Http;

/// <summary>K5.3: the evidence registry on the wire (<c>GET /evidence</c>), so the Face — and
/// anything else attached to a run — can see what the run produced instead of the owner hiring a
/// second agent to notice a screenshot.
///
/// <para>Folded from the event log on each request rather than cached: it is the same read
/// <c>/timeline</c> already makes, it costs one pass over events the store has in hand, and a cached
/// registry would have to be invalidated by a path that does not exist yet.</para></summary>
public sealed partial class ControlPlaneServer
{
    /// <summary>Enough for any surface; a run with more evidence than this has said what it has to
    /// say, and an unbounded list on a loopback JSON endpoint is a footgun, not a feature.</summary>
    private const int MaxEvidenceRows = 200;

    private async Task WriteEvidenceAsync(HttpListenerContext ctx)
    {
        var registry = EvidenceRegistry.From(_store.ReadAllEvents(_state.RunId));

        // ?checkpoint=K5.3 — the question a checkpoint-shaped surface asks. Everything else gets the
        // whole registry newest first, which is what a feed shows.
        var checkpoint = ctx.Request.QueryString["checkpoint"];
        var selected = string.IsNullOrWhiteSpace(checkpoint)
            ? registry.Latest(MaxEvidenceRows)
            : [.. registry.ForCheckpoint(checkpoint.Trim()).Reverse()];

        var limit = int.TryParse(ctx.Request.QueryString["limit"], out var n) && n > 0
            ? Math.Min(n, MaxEvidenceRows)
            : MaxEvidenceRows;

        var rows = selected.Take(limit).Select(a => new EvidenceArtifactDto(
            a.Path, a.Kind, a.CheckpointId, a.StageId, a.SessionNumber,
            a.Sha256, a.Bytes, a.CreatedUtc.ToString("O"), a.Source,
            EvidenceKinds.IsVisual(a.Kind))).ToList();

        // Count is the whole registry, not the page: a surface that shows five of forty has to be
        // able to say so.
        await WriteJsonAsync(ctx, new EvidenceDto(rows, registry.Count),
            ControlPlaneJsonContext.Default.EvidenceDto).ConfigureAwait(false);
    }
}
