using System.Text;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// SC6.1: the part of a run that is WORK, separated from the engine's own view of itself.
///
/// <para>REPORT.md re-renders on every state write, and <see cref="Reporter.WriteAndPublish"/> used to
/// commit whenever the rendered text changed anywhere but the timestamp line. Since the text carries the
/// engine's status, its attention sentence, its timeline, its health flags and its running cost, a run
/// that merely walked Idle → Paused → Aborted landed three commits — devcontext #14 caught exactly that,
/// two of them four seconds apart, every one touching nothing but <c>.conductor/REPORT.md</c>.</para>
///
/// <para>This signature is the commit trigger instead. It folds only facts a reviewer would call
/// delivery: what each checkpoint's status and commit are, and what each FINISHED session concluded.
/// Deliberately absent, because each one moved without any work happening:
/// <see cref="RunState.Status"/>, the attention/blocked sentences and their timestamps, every
/// <c>Pending*</c>, <see cref="RunState.AttemptsThisStage"/>, <see cref="RunState.CurrentStage"/>,
/// <see cref="RunState.SessionCounter"/>, cost and token totals, the timeline, the health and MCP
/// blocks, the repo strip, the live-activity section and the rendered timestamp.</para>
///
/// <para>The report on disk is still rewritten every single time — this changes what gets COMMITTED,
/// not what an operator reads. Process state already lives in run.db and conductor.log, and the report
/// regenerates from both, so nothing is lost by leaving it uncommitted until real work moves.</para>
/// </summary>
public static class ReportSubstance
{
    /// <summary>A stable, comparable digest of everything in this run that counts as delivered work.
    /// Two states with the same digest differ only in how the engine currently describes itself.</summary>
    public static string Of(RunState state, TrackerSnapshot track)
    {
        ArgumentNullException.ThrowIfNull(state);
        var sb = new StringBuilder();

        // The board: every row's settled status and the commit it points at. A claim, an amend, a
        // block, a skip and an evidence-bearing commit link all move this.
        sb.Append("cp:");
        if (track != null)
        {
            foreach (var c in track.Checkpoints.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
                sb.Append(c.Id).Append('=').Append(StatusToken(c)).Append('@').Append(c.Commit).Append(';');
        }

        // Finished sessions only. A session that has merely STARTED has delivered nothing yet, and its
        // record mutates continuously while it runs (cost, tokens, last activity) — including it would
        // reintroduce exactly the every-heartbeat commit this exists to stop.
        sb.Append("\nses:");
        foreach (var h in state.History.Where(h => h.Outcome != null))
        {
            sb.Append(h.Number).Append('|').Append(h.Stage).Append('|').Append(h.Kind).Append('|')
              .Append(h.Attempt).Append('|').Append(h.Outcome).Append('|')
              .Append(string.Join(",", h.NewlyDone)).Append('|')
              .Append(h.NewCommits.Count).Append('|').Append(h.SatelliteCommits.Count).Append('|')
              .Append(h.GateSummary).Append(';');
        }

        // Stage verdicts the run has settled: a confirm and a skip are both durable decisions about work.
        sb.Append("\nconf:").Append(string.Join(",", state.ConfirmedStages.OrderBy(s => s, StringComparer.Ordinal)));
        sb.Append("\nskip:").Append(string.Join(",", state.SkippedStages.OrderBy(s => s, StringComparer.Ordinal)));
        return sb.ToString();
    }

    private static string StatusToken(CheckpointRow c)
        => c.IsDone ? "done" : c.IsSkipped ? "skipped" : c.IsBlocked ? "blocked" : c.IsInProgress ? "wip" : "todo";
}
