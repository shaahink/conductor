using Conductor.Core.Events;
using Conductor.Core.History;

namespace Conductor.Core.Integrations.Github;

/// <summary>
/// KS9.1 — the reconciler: a desired board (from the fold) against an observed one (from GitHub),
/// resolved with upsert-never-clobber and retire-don't-delete, the <c>WorkGraphSync</c> semantics
/// this project already settled on for the tracker.
///
/// <para><b>Idempotence is structural, not hopeful.</b> Identity is the marker in the body, matched
/// against a full LIST of the repository's issues — not GitHub's search index, which is eventually
/// consistent and would decide a second backfill's behaviour by timing. Nothing is created for a
/// task that already has an issue, and nothing is PATCHed when the observed document already says
/// what the fold says. Run it twice and the second run writes nothing.</para>
///
/// <para><b>Never clobber.</b> A PATCH carries only the fields that differ, and labels outside the
/// plan's prefix — a human's <c>needs-discussion</c>, a repo's own triage labels — are carried
/// through untouched. A card that left the plan is closed and labelled retired, never deleted: a
/// deleted issue takes its comment history with it, and that history is the reason to mirror.</para>
///
/// <para><b>One direction.</b> Observed issues answer exactly one question — which issue is ours —
/// and never influence what the run believes. D-7 / A16 / ADR 0005.</para>
/// </summary>
public sealed partial class GithubBoardSync(GithubClient client, string repo, string labelPrefix, GithubMap? map = null)
{
    private readonly string _prefix = string.IsNullOrWhiteSpace(labelPrefix) ? "conductor" : labelPrefix.Trim();

    /// <summary>KS9.2 — what THIS run has already created here, remembered locally. The listing below
    /// is a read replica: it does not show a just-created issue for seconds, which is how one process
    /// once put two complete copies of a board on one repository. See <see cref="GithubMap"/>.</summary>
    private readonly GithubMap _map = map ?? GithubMap.Transient();

    /// <summary>Push a whole run — board then diary. <paramref name="dryRun"/> reconciles and
    /// reports without issuing a single write, which is what makes "what would this do to a real
    /// repository" answerable before it is done to one.</summary>
    /// <param name="ledger">DV6.1 — the bug and followup entries to reconcile alongside the board,
    /// or null for a caller that has no ledger to push. They ride the SAME issue listing as the
    /// cards, so a mirror that gained a ledger did not gain a request.</param>
    public async Task<GithubSyncResult> BackfillAsync(
        IReadOnlyList<ConductorEvent> events, ArchivedRun run, string engineVersion,
        bool includeDiary, bool dryRun, IReadOnlyList<GithubLedgerCard>? ledger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        var result = new GithubSyncResult();

        var (issues, listError) = await client.ListIssuesAsync(repo, ct).ConfigureAwait(false);
        if (listError is not null) { result.Errors.Add(listError); return result; }
        var observed = issues ?? [];

        await SyncCardsAsync(GithubBoardPlan.Cards(events, _prefix), observed, result, dryRun, ct).ConfigureAwait(false);
        if (ledger is { Count: > 0 })
            await SyncLedgerAsync(ledger, observed, result, dryRun, ct).ConfigureAwait(false);
        if (includeDiary)
            await SyncDiaryAsync(GithubBoardPlan.Diary(events, run, engineVersion), observed, result, dryRun, ct)
                .ConfigureAwait(false);
        return result;
    }

    // ── the board ────────────────────────────────────────────────────────────────────────────────

    private async Task SyncCardsAsync(
        List<GithubCard> desired, List<GithubIssue> observed, GithubSyncResult result, bool dryRun,
        CancellationToken ct)
    {
        var byTask = new Dictionary<string, GithubIssue>(StringComparer.Ordinal);
        foreach (var issue in observed)
            if (GithubIdentity.TaskIdIn(issue.Body) is { } id) byTask[id] = issue;

        var milestones = new GithubMilestones(client, repo, dryRun);

        foreach (var card in desired)
        {
            var milestone = await milestones.NumberForAsync(card.Stage, result, ct).ConfigureAwait(false);
            if (!byTask.TryGetValue(card.TaskId, out var existing) && _map.IssueFor(card.TaskId) is { } known)
            {
                // The listing did not show it and the local map says we made it. The listing is stale,
                // not wrong-in-principle: fetch that ONE issue by number, which reads through, and
                // reconcile against the real document so never-clobber still holds.
                var (fetched, fetchError) = await client.GetIssueAsync(repo, known, ct).ConfigureAwait(false);
                if (fetched is not null) existing = fetched;
                else result.Errors.Add($"{card.TaskId}: issue #{known} is in the local map but unreadable ({fetchError})");
            }

            if (existing is null)
            {
                result.Created.Add(card.TaskId);
                if (dryRun) continue;
                var (made, error) = await client.CreateIssueAsync(repo, new GithubIssueRequest
                {
                    Title = card.Title,
                    Body = card.Body,
                    Labels = card.Labels,
                    Milestone = milestone,
                    // An issue cannot be created closed; a card that is already done is created and
                    // then closed, which is two calls exactly once in its life.
                }, ct).ConfigureAwait(false);
                if (error is not null) { result.Errors.Add($"{card.TaskId}: {error}"); result.Created.Remove(card.TaskId); continue; }
                if (made is not null)
                {
                    // Recorded the moment GitHub answers, and BEFORE the close below: a crash between
                    // the two costs an open issue that the next pass closes, not a second issue.
                    _map.RecordIssue(card.TaskId, made.Number);
                    result.Urls[card.TaskId] = made.HtmlUrl;
                    if (card.Closed)
                        await CloseAsync(made.Number, card, result, ct).ConfigureAwait(false);
                }
                continue;
            }

            // Also learn from the listing: an issue an EARLIER run of this plan created is ours too,
            // and the map is what stops the next pass re-deciding that from a replica.
            _map.RecordIssue(card.TaskId, existing.Number);

            var patch = Diff(card, existing, milestone);
            if (patch is null) { result.Unchanged.Add(card.TaskId); continue; }
            result.Updated.Add(card.TaskId);
            if (dryRun) continue;
            var (_, patchError) = await client.UpdateIssueAsync(repo, existing.Number, patch, ct).ConfigureAwait(false);
            if (patchError is not null) result.Errors.Add($"{card.TaskId}: {patchError}");
            else result.Urls[card.TaskId] = existing.HtmlUrl;
        }

        await RetireAsync(desired, byTask, result, dryRun, ct).ConfigureAwait(false);
    }

    /// <summary>The fields that DIFFER, or null when the mirror already says what the fold says.
    /// Null is the answer that makes a second pass cost nothing.</summary>
    private GithubIssueRequest? Diff(GithubCard card, GithubIssue existing, int? milestone)
    {
        var wantLabels = MergeLabels(card.Labels, existing.LabelNames);
        var titleDiffers = !string.Equals(card.Title, existing.Title, StringComparison.Ordinal);
        var bodyDiffers = !SameText(card.Body, existing.Body);
        var labelsDiffer = !wantLabels.SequenceEqual(Sorted(existing.LabelNames), StringComparer.Ordinal);
        var stateDiffers = card.Closed == existing.IsOpen;
        var milestoneDiffers = milestone is not null && existing.Milestone?.Number != milestone;
        if (!titleDiffers && !bodyDiffers && !labelsDiffer && !stateDiffers && !milestoneDiffers) return null;

        return new GithubIssueRequest
        {
            Title = titleDiffers ? card.Title : null,
            Body = bodyDiffers ? card.Body : null,
            Labels = labelsDiffer ? wantLabels : null,
            Milestone = milestoneDiffers ? milestone : null,
            State = stateDiffers ? (card.Closed ? "closed" : "open") : null,
        };
    }

    /// <summary>Ours, plus every label that is not ours. A mirror that sent only its own labels
    /// would silently strip a human's triage on every status change.</summary>
    private List<string> MergeLabels(List<string> ours, IReadOnlyList<string> onIssue)
    {
        var foreign = onIssue.Where(l => !l.StartsWith(_prefix + ":", StringComparison.Ordinal)
                                      && !string.Equals(l, _prefix, StringComparison.Ordinal));
        return Sorted([.. ours, .. foreign]);
    }

    private static List<string> Sorted(IEnumerable<string> labels) =>
        [.. labels.Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal)];

    /// <summary>GitHub stores a body with CRLF and trims it. Comparing raw would make every second
    /// pass "changed" — a false positive that looks exactly like a real one.</summary>
    private static bool SameText(string? a, string? b) =>
        string.Equals((a ?? "").ReplaceLineEndings("\n").TrimEnd(),
                      (b ?? "").ReplaceLineEndings("\n").TrimEnd(), StringComparison.Ordinal);

    private async Task CloseAsync(int number, GithubCard card, GithubSyncResult result, CancellationToken ct)
    {
        var (_, error) = await client.UpdateIssueAsync(repo, number,
            new GithubIssueRequest { State = "closed" }, ct).ConfigureAwait(false);
        if (error is not null) result.Errors.Add($"{card.TaskId}: {error}");
    }

    /// <summary>An issue of ours whose task no longer appears in the plan. Closed with a label and a
    /// sentence saying why — never deleted.</summary>
    private async Task RetireAsync(
        List<GithubCard> desired, Dictionary<string, GithubIssue> byTask, GithubSyncResult result,
        bool dryRun, CancellationToken ct)
    {
        var live = new HashSet<string>(desired.Where(c => !c.Retired).Select(c => c.TaskId), StringComparer.Ordinal);
        var retiredLabel = _prefix + ":retired";
        foreach (var (taskId, issue) in byTask)
        {
            if (live.Contains(taskId)) continue;
            if (!issue.IsOpen && issue.LabelNames.Contains(retiredLabel, StringComparer.Ordinal)) continue;
            result.Retired.Add(taskId);
            if (dryRun) continue;

            var (_, commentError) = await client.CreateCommentAsync(repo, issue.Number,
                "Retired by conductor: this checkpoint is no longer declared in the plan. " +
                "The issue is closed and kept — its history is the point.", ct).ConfigureAwait(false);
            if (commentError is not null) result.Errors.Add($"{taskId}: {commentError}");
            var (_, error) = await client.UpdateIssueAsync(repo, issue.Number, new GithubIssueRequest
            {
                Labels = Sorted([.. issue.LabelNames, retiredLabel]),
                State = "closed",
            }, ct).ConfigureAwait(false);
            if (error is not null) result.Errors.Add($"{taskId}: {error}");
        }
    }

    // ── the diary ────────────────────────────────────────────────────────────────────────────────

    private async Task SyncDiaryAsync(
        GithubDiary diary, List<GithubIssue> observed, GithubSyncResult result, bool dryRun,
        CancellationToken ct)
    {
        var marker = GithubIdentity.RunMarker(diary.RunId);
        var existing = observed.Find(i => i.Body?.Contains(marker, StringComparison.Ordinal) == true);
        var runKey = "run:" + diary.RunId;
        if (existing is null && _map.IssueFor(runKey) is { } knownRun)
        {
            // Same replica lag as the cards, and worse if it wins: a duplicate diary issue takes the
            // comment history with it, and the history is the reason to mirror at all.
            var (fetched, fetchError) = await client.GetIssueAsync(repo, knownRun, ct).ConfigureAwait(false);
            if (fetched is not null) existing = fetched;
            else result.Errors.Add($"run issue: #{knownRun} is in the local map but unreadable ({fetchError})");
        }
        int number;
        if (existing is null)
        {
            result.Created.Add(runKey);
            if (dryRun) { result.Comments.AddRange(diary.Comments.Select(c => c.Key)); return; }
            var (made, error) = await client.CreateIssueAsync(repo, new GithubIssueRequest
            {
                Title = diary.Title,
                Body = diary.Body,
                Labels = [_prefix + ":run"],
            }, ct).ConfigureAwait(false);
            if (error is not null || made is null)
            {
                result.Errors.Add($"run issue: {error ?? "no issue returned"}");
                result.Created.Remove(runKey);
                return;
            }
            number = made.Number;
            _map.RecordIssue(runKey, number);
            result.Urls[runKey] = made.HtmlUrl;
        }
        else
        {
            number = existing.Number;
            _map.RecordIssue(runKey, number);
            result.Urls[runKey] = existing.HtmlUrl;
            if (SameText(diary.Body, existing.Body) && diary.Closed != existing.IsOpen)
                result.Unchanged.Add(runKey);
            else
            {
                result.Updated.Add(runKey);
                if (!dryRun)
                {
                    var (_, error) = await client.UpdateIssueAsync(repo, number, new GithubIssueRequest
                    {
                        Body = SameText(diary.Body, existing.Body) ? null : diary.Body,
                        State = diary.Closed == existing.IsOpen ? (diary.Closed ? "closed" : "open") : null,
                    }, ct).ConfigureAwait(false);
                    if (error is not null) result.Errors.Add($"run issue: {error}");
                }
            }
        }

        await SyncCommentsAsync(diary, number, result, dryRun, ct).ConfigureAwait(false);
        if (existing is null && diary.Closed && !dryRun)
            await client.UpdateIssueAsync(repo, number, new GithubIssueRequest { State = "closed" }, ct)
                .ConfigureAwait(false);
    }

    private async Task SyncCommentsAsync(
        GithubDiary diary, int number, GithubSyncResult result, bool dryRun, CancellationToken ct)
    {
        if (diary.Comments.Count == 0) return;
        // Read the existing comments even on a dry run: "how many comments would this post" is the
        // number the operator is asking for, and answering it without looking would be a guess.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var (comments, error) = await client.ListCommentsAsync(repo, number, ct).ConfigureAwait(false);
        if (error is not null) { result.Errors.Add($"run comments: {error}"); return; }
        foreach (var c in comments ?? [])
            if (GithubIdentity.SessionKeyIn(c.Body) is { } key) seen.Add(key);

        foreach (var comment in diary.Comments)
        {
            var key = GithubIdentity.SessionKeyIn(comment.Key) ?? comment.Key;
            // The listing OR the local map. A comment endpoint is a read replica like every other one,
            // and a duplicated diary comment is the most visible way for a mirror to look broken.
            if (seen.Contains(key) || _map.CommentPosted(key)) continue;
            result.Comments.Add(key);
            if (dryRun) continue;
            var (_, postError) = await client.CreateCommentAsync(repo, number, comment.Body, ct).ConfigureAwait(false);
            if (postError is not null) result.Errors.Add($"session comment {key}: {postError}");
            else _map.RecordComment(key, number);
        }
    }
}
