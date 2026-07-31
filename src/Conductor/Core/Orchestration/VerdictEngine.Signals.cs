using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>
/// The read-only READINGS the verdict takes off a finished session, as distinct from the decisions it
/// makes with them: what the session did outside the work tree, and whether it is repeating a stall
/// that has already proved fruitless. Neither changes state; both are what
/// <see cref="VerdictEngine.EvaluateSessionAsync"/> consults before it judges anything.
/// </summary>
public sealed partial class VerdictEngine
{
    /// <summary>SC7.1 (devcontext #11): the verdict says what the session did OUTSIDE the work tree.
    /// Collected by <c>SessionRunner.TrackActivity</c> from the structured tool events — which is the
    /// whole reason SC7.1 had to stop cutting tool arguments mid-string: a <c>file_path</c> past the
    /// old 150-character cut was never captured, so no verdict could ever have mentioned it.</summary>
    /// <remarks>Raised FIRST in <see cref="EvaluateSessionAsync"/>, ahead of every early return, so a
    /// session that stalls or is killed still reports where it wrote. It is a note, not a judgement:
    /// writing outside the repo is often correct (a scratch rig, a satellite the plan forgot to
    /// declare) and the operator is the one who can tell.</remarks>
    private void NoteOutsideRepoWrites(SessionRecord rec)
    {
        if (rec.OutsideRepoWrites.Count == 0) return;
        var shown = rec.OutsideRepoWrites.Take(4).Select(p => Trunc(p, 120));
        var more = rec.OutsideRepoWrites.Count > 4 ? $", +{rec.OutsideRepoWrites.Count - 4} more" : "";
        _ctx.Log($"note: {rec.OutsideRepoWrites.Count} file(s) written outside the repo: {string.Join(", ", shown)}{more}");
    }

    /// <summary>Two consecutive stalls that produced nothing at all — no work commits, no result
    /// text. The environment or the agent is broken, and a third attempt buys nothing.</summary>
    private bool IdenticalStallPattern(SessionRecord rec)
    {
        // SC4.2: a stall that produced only conductor's own bookkeeping commits produced nothing.
        // SC4.3: a stall that committed to a declared satellite produced something.
        if (SessionProgress.HasWorkCommits(rec)) return false;
        var summary = rec.ResultSummary?.Trim();
        if (!string.IsNullOrEmpty(summary)) return false;

        var stalledCount = 1;
        for (var i = _ctx.State.History.Count - 2; i >= 0; i--)
        {
            var prev = _ctx.State.History[i];
            if (prev.Outcome != SessionOutcome.Stalled) break;
            if (!SessionProgress.HasWorkCommits(prev) && string.IsNullOrEmpty(prev.ResultSummary?.Trim()))
            {
                stalledCount++;
                if (stalledCount >= 2) return true;
            }
            else break;
        }
        return false;
    }
}
