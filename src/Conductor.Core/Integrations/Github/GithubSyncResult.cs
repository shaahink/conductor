using System.Globalization;

namespace Conductor.Core.Integrations.Github;

/// <summary>
/// KS9.1 — what one reconcile pass DID, in the four numbers the idempotence bar is stated in.
/// A second identical backfill must report created 0 and comments 0; that is the claim, and this is
/// where it is counted.
/// </summary>
public sealed class GithubSyncResult
{
    public List<string> Created { get; } = [];
    public List<string> Updated { get; } = [];
    public List<string> Unchanged { get; } = [];
    public List<string> Retired { get; } = [];

    /// <summary>CH4.3 - task-marked issues the retire sweep DECLINED to close, because nothing
    /// attributes them to the run being synced: no owner marker of ours in the body and no row in
    /// this run's local map. Each entry is "<c>taskId #number</c>" - the sweep names what it would
    /// have closed rather than doing it, and rather than saying nothing.
    ///
    /// <para>Not an error, and deliberately not counted as one: a repository that carries an
    /// earlier era's board is the NORMAL case for this project, and a backfill that exited
    /// non-zero because it correctly left another run alone would be unusable. It is reported
    /// instead - loudly, with the numbers - which is the difference between a refusal and the
    /// silent skip KS12.3 was.</para></summary>
    public List<string> RetireRefused { get; } = [];

    /// <summary>Session keys whose diary comment this pass posted (or would post).</summary>
    public List<string> Comments { get; } = [];

    /// <summary>Every failure, named. A partial push is reported as a partial push — the mirror
    /// never claims a board it did not write.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Issue URLs by task id (and <c>run:&lt;id&gt;</c> for the diary) — what a proof
    /// transcript quotes.</summary>
    public Dictionary<string, string> Urls { get; } = new(StringComparer.Ordinal);

    /// <summary>DV6.2 — what the COLUMNS did, or null when no project board was asked for or the
    /// scope gate refused. Null and empty are different answers: null is "nobody asked", and an empty
    /// pass is "asked, and there was nothing to place".</summary>
    public GithubProjectPass? Project { get; set; }

    public bool Ok => Errors.Count == 0 && (Project?.Ok ?? true);

    public string Summary()
    {
        var issues = string.Create(CultureInfo.InvariantCulture,
            $"{Created.Count} created · {Updated.Count} updated · {Unchanged.Count} unchanged · " +
            $"{Retired.Count} retired · {Comments.Count} comments · {Errors.Count} errors");
        // Only when there is something to say. A zero here is the ordinary case and a seventh
        // permanent number would bury the six the idempotence bar is actually stated in.
        if (RetireRefused.Count > 0)
            issues += string.Create(CultureInfo.InvariantCulture, $" · {RetireRefused.Count} retire refused");
        return Project is null ? issues : issues + Environment.NewLine + Project.Summary();
    }
}

/// <summary>KS9.1 — stages, as GitHub milestones. Listed once per pass and created lazily, so a
/// board with eleven stages costs one request, not eleven.</summary>
internal sealed class GithubMilestones(GithubClient client, string repo, bool dryRun)
{
    private Dictionary<string, int>? _byTitle;

    public async Task<int?> NumberForAsync(string stage, GithubSyncResult result, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stage)) return null;
        if (_byTitle is null)
        {
            var (list, error) = await client.ListMilestonesAsync(repo, ct).ConfigureAwait(false);
            if (error is not null) { result.Errors.Add($"milestones: {error}"); _byTitle = new(StringComparer.Ordinal); }
            else _byTitle = (list ?? []).GroupBy(m => m.Title, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Number, StringComparer.Ordinal);
        }
        if (_byTitle.TryGetValue(stage, out var number)) return number;
        if (dryRun) return null;

        var (made, createError) = await client.CreateMilestoneAsync(repo, stage, ct).ConfigureAwait(false);
        if (createError is not null || made is null)
        {
            // A milestone is a nicety; a board without one is still a board. The failure is named
            // once and the cards go up anyway.
            result.Errors.Add($"milestone {stage}: {createError ?? "no milestone returned"}");
            return null;
        }
        _byTitle[stage] = made.Number;
        return made.Number;
    }
}
