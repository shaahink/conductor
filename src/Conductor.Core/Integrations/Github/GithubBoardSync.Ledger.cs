namespace Conductor.Core.Integrations.Github;

/// <summary>
/// DV6.1 — the ledger half of the reconciler: bugs and followups, in their own issue class, with
/// their own lifetime.
///
/// <para><b>Why it is a separate sweep and not another list of cards.</b> The board sweep ends in
/// <c>RetireAsync</c>, which closes any issue of ours whose task the plan no longer declares — and a
/// run's plan declares no bugs at all. Feeding ledger entries through the same sweep would mean the
/// rule "these survive the run" was one <c>if</c> away from being false. Here it is structural: the
/// retire sweep indexes issues by the TASK marker, a ledger issue carries a bug or followup marker,
/// and the two sets cannot intersect.</para>
///
/// <para><b>Create only what is open.</b> A closed entry is reconciled but never created. Twenty-six
/// runs of history in one <c>run.db</c> would otherwise arrive on the destination as a wall of
/// already-closed issues, which is precisely the graveyard this checkpoint exists to empty.</para>
///
/// <para><b>Still one direction.</b> An observed issue answers "which issue is ours". What it should
/// SAY is decided from the local ledger — a bug row and a followups.md row — and never from GitHub.
/// D-7 / A16 / ADR 0005.</para>
/// </summary>
public sealed partial class GithubBoardSync
{
    private async Task SyncLedgerAsync(
        IReadOnlyList<GithubLedgerCard> desired, List<GithubIssue> observed, GithubSyncResult result,
        bool dryRun, CancellationToken ct)
    {
        if (desired.Count == 0) return;

        var byMarker = new Dictionary<string, GithubIssue>(StringComparer.Ordinal);
        foreach (var issue in observed)
        {
            if (GithubIdentity.BugIdIn(issue.Body) is { } bug) byMarker["bug:" + bug] = issue;
            else if (GithubIdentity.FollowupIdIn(issue.Body) is { } followup) byMarker["followup:" + followup] = issue;
        }

        foreach (var card in desired)
        {
            byMarker.TryGetValue(card.Key, out var existing);
            if (existing is null && _map.IssueFor(card.Key) is { } known)
            {
                // The same read-replica lag KS9.2 measured on the board half: the listing does not
                // show an issue this process created seconds ago, and a duplicate ledger issue is
                // worse than a duplicate card because nothing ever retires it.
                var (fetched, fetchError) = await client.GetIssueAsync(repo, known, ct).ConfigureAwait(false);
                if (fetched is not null) existing = fetched;
                else result.Errors.Add($"{card.Key}: issue #{known} is in the local map but unreadable ({fetchError})");
            }

            if (existing is null)
            {
                // A closed entry with no issue is left alone, deliberately and silently: it is not an
                // error, and counting it as "unchanged" would drown the summary in history.
                if (!card.CreateIfMissing) continue;
                await CreateLedgerIssueAsync(card, result, dryRun, ct).ConfigureAwait(false);
                continue;
            }

            _map.RecordIssue(card.Key, existing.Number);
            // DV6.2 — a ledger entry has two states, not five: open work belongs in the board's first
            // column and a closed one in its last, which is exactly what the issue half already says
            // about it.
            Place(card.Key, existing, card.Closed ? "done" : "todo");
            await UpdateLedgerIssueAsync(card, existing, result, dryRun, ct).ConfigureAwait(false);
        }
    }

    private async Task CreateLedgerIssueAsync(
        GithubLedgerCard card, GithubSyncResult result, bool dryRun, CancellationToken ct)
    {
        result.Created.Add(card.Key);
        if (dryRun) return;
        var (made, error) = await client.CreateIssueAsync(repo, new GithubIssueRequest
        {
            Title = card.Title,
            Body = card.Body,
            Labels = card.Labels,
            // No milestone. A stage milestone is a container for work that ENDS; a bug that outlives
            // the stage that found it would be filed under a milestone nobody reopens.
        }, ct).ConfigureAwait(false);
        if (error is not null || made is null)
        {
            result.Errors.Add($"{card.Key}: {error ?? "no issue returned"}");
            result.Created.Remove(card.Key);
            return;
        }
        _map.RecordIssue(card.Key, made.Number);
        result.Urls[card.Key] = made.HtmlUrl;
        Place(card.Key, made, card.Closed ? "done" : "todo");
    }

    private async Task UpdateLedgerIssueAsync(
        GithubLedgerCard card, GithubIssue existing, GithubSyncResult result, bool dryRun, CancellationToken ct)
    {
        var wantLabels = MergeLabels(card.Labels, existing.LabelNames);
        var titleDiffers = !string.Equals(card.Title, existing.Title, StringComparison.Ordinal);
        var bodyDiffers = !SameText(card.Body, existing.Body);
        var labelsDiffer = !wantLabels.SequenceEqual(Sorted(existing.LabelNames), StringComparer.Ordinal);
        var closing = card.Closed && existing.IsOpen;
        // A ledger issue is never REOPENED from here. Reopening is a human act on a human's board:
        // the ledger's own vocabulary has no "was closed, now open" transition, so a mirror that
        // reopened would be inventing one from a status string it does not fully understand.
        if (!titleDiffers && !bodyDiffers && !labelsDiffer && !closing)
        {
            result.Unchanged.Add(card.Key);
            return;
        }

        result.Updated.Add(card.Key);
        result.Urls[card.Key] = existing.HtmlUrl;
        if (dryRun) return;

        if (closing)
        {
            var (_, commentError) = await client.CreateCommentAsync(repo, existing.Number, ClosingNote(card), ct)
                .ConfigureAwait(false);
            if (commentError is not null) result.Errors.Add($"{card.Key}: {commentError}");
        }

        var (_, patchError) = await client.UpdateIssueAsync(repo, existing.Number, new GithubIssueRequest
        {
            Title = titleDiffers ? card.Title : null,
            Body = bodyDiffers ? card.Body : null,
            Labels = labelsDiffer ? wantLabels : null,
            State = closing ? "closed" : null,
        }, ct).ConfigureAwait(false);
        if (patchError is not null) result.Errors.Add($"{card.Key}: {patchError}");
    }

    /// <summary>Why this one closed, said out loud. An issue that simply went grey tells a reader
    /// nothing about WHICH side closed it — and the whole claim of this checkpoint is that the ledger
    /// closes it, not the run ending.</summary>
    private static string ClosingNote(GithubLedgerCard card) =>
        card.Key.StartsWith("bug:", StringComparison.Ordinal)
            ? "Closed by conductor: the bug ledger in this repo's run.db no longer lists this bug as open. " +
              "The issue is closed and kept."
            : "Closed by conductor: the row for this followup in `.conductor/followups.md` is no longer OPEN. " +
              "The issue is closed and kept.";
}
