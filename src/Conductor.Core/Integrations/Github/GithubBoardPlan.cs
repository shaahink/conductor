using System.Globalization;
using System.Text;
using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Models;

namespace Conductor.Core.Integrations.Github;

/// <summary>
/// KS9.1 — the mirror's DECISION, taken entirely from the local fold, with no HTTP in sight.
///
/// <para><b>Why this is a separate type from the thing that posts.</b> "What should the board say"
/// is a pure function of the event log, so it is testable without a network, a fake, or a recorded
/// transcript — and every mapping rule the contract names (the title shape, the label set, the
/// <c>confirmed</c> distinction, the stage milestone, the marker) is asserted directly against it.
/// The type that posts then has one job left: reconcile a desired list against an observed one.</para>
///
/// <para><b>It decides from the fold and the local map ONLY.</b> Never from what GitHub says a card
/// is. A reconciler that read GitHub to decide what to push would re-introduce the ingress D-7, A16
/// and ADR 0005 all forbid; observed issues are used to find OUR issue, never to change what ours
/// should say.</para>
/// </summary>
public static class GithubBoardPlan
{
    /// <summary>The desired board for a run, in the checkpoint order the graph itself keeps.</summary>
    public static List<GithubCard> Cards(IEnumerable<ConductorEvent> events, string labelPrefix)
    {
        var graph = new TaskGraph();
        graph.Fold(events);
        var prefix = string.IsNullOrWhiteSpace(labelPrefix) ? "conductor" : labelPrefix.Trim();
        return [.. graph.Checkpoints().Select(t => CardFor(t, prefix))];
    }

    /// <summary>One checkpoint as an issue. The title is the human line; the BODY carries the
    /// marker, which is the identity — a checkpoint reworded in the plan must update its issue, not
    /// mint a second one.</summary>
    public static GithubCard CardFor(TaskItem item, string prefix)
    {
        ArgumentNullException.ThrowIfNull(item);
        var labels = new List<string> { $"{prefix}:status:{item.Status}" };
        if (!string.IsNullOrWhiteSpace(item.Source)) labels.Add($"{prefix}:source:{item.Source}");
        // The DONE ✓ distinction is the whole point of W1.1's confirmed flag: a session CLAIMS, and
        // only the verdict engine confirms. A mirror that flattened the two would show a board of
        // green cards for work no gate battery has agreed with, which is the one lie this project
        // has paid for repeatedly.
        if (item.Confirmed) labels.Add($"{prefix}:confirmed");

        return new GithubCard(
            TaskId: item.TaskId,
            Title: $"{item.CheckpointId} — {item.Title}",
            Body: BodyFor(item),
            Labels: labels,
            Stage: item.StageId,
            Closed: item.Status is "done" or "skipped" or "archived",
            Retired: item.Status == "archived");
    }

    private static string BodyFor(TaskItem item)
    {
        var sb = new StringBuilder();
        sb.Append(GithubIdentity.TaskMarker(item.TaskId)).Append('\n').Append('\n');
        sb.Append("**Stage** ").Append(Or(item.StageId)).Append("  ")
          .Append("**Status** ").Append(Or(item.Status)).Append(item.Confirmed ? " ✓ confirmed" : "").Append('\n');
        sb.Append("**Source** ").Append(Or(item.Source)).Append("  ")
          .Append("**Commit** ").Append(Or(item.Commit)).Append("  ")
          .Append("**Evidence** ").Append(Or(item.Evidence)).Append('\n');
        if (item.Attempts > 0)
            sb.Append("**Attempts** ").Append(item.Attempts.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append('\n').Append("<sub>Mirrored by conductor. This board is a VIEW: the tracker and the run's ")
          .Append("event log are the contract, and nothing here is ever read back into the run.</sub>");
        return sb.ToString();
    }

    private static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "-" : s;

    /// <summary>The run's diary: one issue for the run, one comment per finished session. Closed
    /// when the run reached a terminal status, so a reader's issue list says which runs are over
    /// without opening one.</summary>
    public static GithubDiary Diary(IEnumerable<ConductorEvent> events, ArchivedRun run, string engineVersion)
    {
        ArgumentNullException.ThrowIfNull(run);
        var body = new StringBuilder()
            .Append(GithubIdentity.RunMarker(run.RunId)).Append('\n').Append('\n')
            .Append("**Plan** ").Append(Or(run.PlanName)).Append('\n')
            .Append("**Repo** ").Append(Or(run.Repo)).Append('\n')
            .Append("**Branch** ").Append(Or(run.Branch ?? "")).Append('\n')
            .Append("**Engine** ").Append(Or(engineVersion)).Append('\n')
            .Append("**Run** ").Append(run.RunId).Append('\n')
            .ToString();

        var comments = new List<GithubDiaryComment>();
        foreach (var e in events.OfType<SessionFinished>())
            comments.Add(new GithubDiaryComment(
                GithubIdentity.SessionMarker(run.RunId, e.Number), CommentFor(run.RunId, e)));

        return new GithubDiary(
            RunId: run.RunId,
            Title: $"run: {run.PlanName} — {run.ShortRunId}",
            Body: body,
            Closed: run.Status is "completed" or "aborted" or "failed" or "closed",
            Comments: comments);
    }

    private static string CommentFor(string runId, SessionFinished e)
    {
        var sb = new StringBuilder();
        sb.Append(GithubIdentity.SessionMarker(runId, e.Number)).Append('\n').Append('\n');
        sb.Append("**session ").Append(N(e.Number)).Append("** · stage ").Append(Or(e.StageId))
          .Append(" · ").Append(Or(e.Outcome)).Append('\n');
        if (e.NewlyDone.Count > 0) sb.Append("newly done: ").Append(string.Join(", ", e.NewlyDone)).Append('\n');
        if (e.NewCommits.Count > 0) sb.Append("commits: ").Append(string.Join(", ", e.NewCommits.Select(Short))).Append('\n');
        if (e.CostUsd is { } cost) sb.Append("cost: $").Append(cost.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');
        var tokens = (e.TokensInput ?? 0) + (e.TokensOutput ?? 0) + (e.TokensCacheRead ?? 0) + (e.TokensReasoning ?? 0);
        if (tokens > 0)
        {
            sb.Append("tokens: ").Append(N(tokens));
            if (e.TokensCacheRead is > 0) sb.Append(" (cache read ").Append(N(e.TokensCacheRead.Value)).Append(')');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    private static string Short(string sha) => sha.Length >= 8 ? sha[..8] : sha;
}
