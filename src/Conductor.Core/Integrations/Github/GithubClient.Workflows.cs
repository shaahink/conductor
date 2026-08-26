namespace Conductor.Core.Integrations.Github;

/// <summary>
/// CH1.3 — the two reads that answer "what does CI say about this commit".
///
/// <para><b>Why not the commit's check-runs.</b> A commit's check-runs only list the workflows THAT
/// COMMIT TRIGGERED, so a schedule-only or dispatch-only workflow is invisible there and a branch
/// reports green while a broken workflow sits red. Measured on this repo on 2026-08-26:
/// <c>gh pr checks</c> listed only the Vercel checks on a PR whose gates workflow had never run on
/// the new head, and <c>mergeStateStatus</c> said CLEAN. So the question is asked the other way
/// round — every ACTIVE workflow, then its latest run on the branch — which cannot be silent about a
/// workflow that did not run.</para>
/// </summary>
public sealed partial class GithubClient
{
    /// <summary>Every workflow the repository has, active or not. The caller filters, because
    /// "there are five workflows and two are disabled" is part of the answer.</summary>
    public Task<(GithubWorkflowList? Value, string? Error)> ListWorkflowsAsync(
        string repo, CancellationToken ct = default) =>
        GetAsync($"/repos/{repo}/actions/workflows?per_page=100",
            GithubJsonContext.Default.GithubWorkflowList, ct);

    /// <summary>The newest runs of one workflow on one branch, newest first. One is enough to answer
    /// the question; more than one is how a caller sees that the newest run is not for HEAD.</summary>
    public Task<(GithubWorkflowRunList? Value, string? Error)> WorkflowRunsAsync(
        string repo, long workflowId, string branch, int count = 1, CancellationToken ct = default) =>
        GetAsync($"/repos/{repo}/actions/workflows/{Id(workflowId)}/runs"
               + $"?branch={Uri.EscapeDataString(branch ?? "")}&per_page={Num(count)}",
            GithubJsonContext.Default.GithubWorkflowRunList, ct);

    /// <summary>A workflow id is a long and the invariant form is not optional in a URL.</summary>
    private static string Id(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
