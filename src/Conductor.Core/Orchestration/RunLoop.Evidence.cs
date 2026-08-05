using System.Globalization;
using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>K5.3 — the run loop's evidence leg, in its own file: the registration it awaits at the
/// session boundary. Split out of <c>RunLoop.Plumbing.cs</c> when it took that file past the
/// architecture ratchet's 500-line ceiling, which is the ratchet doing its job — evidence is a
/// responsibility, not more plumbing.</summary>
public sealed partial class RunLoop
{


    /// <summary>K5.3 — evidence becomes an artifact the engine knows about, at the session boundary.
    /// <para>Two ways in, and both are things agents already do. A claim carrying
    /// <c>--evidence &lt;path&gt;</c> registers that file — the free-text field is untouched and still
    /// stored, because an artifact registry that breaks every existing claim is not an improvement.
    /// And any file that appeared in a watched directory is registered too, which is how a PNG an
    /// agent never mentioned still reaches a surface: the owner's real case is conductor building a
    /// website, the agent screenshotting it, and a SECOND agent hired to notice the images.</para>
    /// <para>Failure here is never allowed to touch the session's verdict — a hashing error or a file
    /// that vanished between the scan and the read is logged and dropped.</para></summary>
    private async Task RegisterEvidenceAsync(SessionRecord rec, CancellationToken ct)
    {
        if (_ctx.Store is not { } db) return;
        try
        {
            var registry = EvidenceRegistry.From(db.ReadAllEvents(_ctx.State.RunId));
            var repo = _ctx.Plan.Repo;
            var stateDir = _ctx.Plan.StateDir;
            var fresh = new List<EvidenceArtifact>();

            // 1. What the claims named. The checkpoint is known here, so the artifact carries it.
            var claimed = db.GetCheckpoints(_ctx.State.RunId)
                .Where(c => rec.NewlyDone.Contains(c.Id, StringComparer.OrdinalIgnoreCase));
            foreach (var cp in claimed)
            {
                var path = EvidenceReader.ResolvePath(cp.Evidence, repo, stateDir);
                if (path is null) continue;
                var artifact = await EvidenceReader
                    .ReadAsync(path, repo, cp.Id, rec.Number, "claim", ct: ct).ConfigureAwait(false);
                if (artifact is not null && registry.Add(artifact)) fresh.Add(artifact);
            }

            // 2. What simply appeared. Checkpoint inferred from the file name when it follows the
            //    convention this repo's own evidence directory has used since Sarban.
            var scanned = await EvidenceWatcher.ScanAsync(
                EvidenceWatcher.DefaultDirectories(repo, stateDir), registry, repo, rec.Number, ct: ct)
                .ConfigureAwait(false);
            foreach (var artifact in scanned)
            {
                if (registry.Add(artifact)) fresh.Add(artifact);
            }

            foreach (var a in fresh)
            {
                _ctx.Events.Emit(new EvidenceRegistered
                {
                    SessionId = rec.Number.ToString(CultureInfo.InvariantCulture),
                    Path = a.Path,
                    Kind = a.Kind,
                    Sha256 = a.Sha256,
                    Bytes = a.Bytes,
                    CheckpointId = a.CheckpointId,
                    StageId = a.StageId,
                    SessionNumber = a.SessionNumber,
                    Source = a.Source,
                });
            }

            if (fresh.Count > 0)
            {
                _ctx.Log($"evidence: {fresh.Count} artifact(s) registered — " +
                         string.Join(", ", fresh.Take(4).Select(a => $"{a.Path} ({a.Kind})")) +
                         (fresh.Count > 4 ? $", +{fresh.Count - 4} more" : ""));
                // Fire-and-forget like every other push, and deliberately NOT cancelled with the
                // session: a run being torn down is exactly when the last artifact matters most.
                _ = _ctx.Telegram.PushEvidenceAsync(fresh, CancellationToken.None);
            }
        }
        // Evidence must never be able to fail a session: a vanished file, a locked directory or a
        // store hiccup is a warning in the log, not a verdict.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException or System.Data.Common.DbException
                                      or ArgumentException or NotSupportedException)
        {
            _ctx.Log($"evidence registration failed: {ex.Message}", "warn");
        }
    }
}
