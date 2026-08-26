using System.Globalization;

namespace Conductor.Core.Integrations.Github;

/// <summary>DV6.4 — what one code-scanning upload DID, in the terms a proof transcript quotes.</summary>
public sealed class GithubSarifPass
{
    /// <summary>Open bugs that named a place in the tree and became results.</summary>
    public int Reported { get; set; }

    /// <summary>Open bugs with no citation a resolver could stand behind. Counted, never hidden: the
    /// difference between this and <see cref="Reported"/> is the honest reach of the feature.</summary>
    public int WithoutLocation { get; set; }

    /// <summary>The compressed payload GitHub was handed, in bytes. GitHub's own ceiling is 10 MB
    /// gzipped; a conductor ledger is three orders of magnitude under it, and saying the number is
    /// how that stays true.</summary>
    public int PayloadBytes { get; set; }

    public string? SarifId { get; set; }
    public string? StatusUrl { get; set; }

    /// <summary>pending / complete / failed, as GitHub reports it AFTER the 202. The 202 alone is a
    /// receipt, not an ingestion.</summary>
    public string? ProcessingStatus { get; set; }

    /// <summary>Sentences that are true and worth reading but are not failures — the private-repo
    /// caveat above all.</summary>
    public List<string> Notes { get; } = [];

    public List<string> Errors { get; } = [];

    public bool Ok => Errors.Count == 0;

    public string Summary()
    {
        var n = Reported.ToString(CultureInfo.InvariantCulture);
        var skipped = WithoutLocation.ToString(CultureInfo.InvariantCulture);
        var head = $"sarif: {n} located, {skipped} without a file and line";
        if (SarifId is not null) head += $", id {SarifId} ({ProcessingStatus ?? "pending"})";
        if (Errors.Count > 0) head += $", {Errors.Count.ToString(CultureInfo.InvariantCulture)} error(s)";
        return head;
    }
}

/// <summary>
/// DV6.4 — pushes the bug ledger to GitHub code scanning.
///
/// <para><b>The caveat this class exists to state.</b> Code scanning is free on PUBLIC repositories
/// and, on a private one, needs GitHub Advanced Security. That is not a conductor rule and conductor
/// cannot work around it; what conductor can do is say so before the call, and translate GitHub's
/// 403 into that sentence instead of leaving an owner staring at a status code.</para>
///
/// <para>The scope story differs by visibility too, and the difference is why the preflight reads
/// the repository first: a public repo is reachable with the <c>public_repo</c> that <c>repo</c>
/// already contains, while a private one needs <c>security_events</c> — the KS9.3 shape, one command
/// to grant and named in the refusal.</para>
/// </summary>
public sealed class GithubSarifSync(GithubClient client, string repo)
{
    public const string PrivateScope = "security_events";
    public const string GrantCommand = "gh auth refresh -s " + PrivateScope;

    public const string AdvancedSecurityNote =
        "code scanning is free on PUBLIC repositories; a PRIVATE repository needs GitHub Advanced Security " +
        "(GitHub Code Security) and refuses the upload with 403 without it.";

    /// <summary>Renders and uploads. <paramref name="dryRun"/> renders and reports and sends
    /// nothing, which is what a proof of the document alone wants.</summary>
    public async Task<GithubSarifPass> PushAsync(
        SarifPayload payload, string commitSha, string gitRef, string tokenSource, bool dryRun,
        int statusAttempts = 6, TimeSpan? statusDelay = null, CancellationToken ct = default)
    {
        var pass = new GithubSarifPass
        {
            Reported = payload.Findings.Count,
            WithoutLocation = payload.WithoutLocation,
            PayloadBytes = (await GithubClient.GzipBase64Async(payload.Json, ct).ConfigureAwait(false)).Length,
        };

        if (pass.Reported == 0)
        {
            pass.Notes.Add("no open bug carries a file and line; nothing to upload.");
            return pass;
        }

        if (dryRun)
        {
            pass.Notes.Add("dry run: the document was rendered and not sent.");
            return pass;
        }

        if (!await PreflightAsync(pass, tokenSource, ct).ConfigureAwait(false)) return pass;

        var (upload, error) = await client
            .UploadSarifAsync(repo, payload.Json, commitSha, gitRef, ct).ConfigureAwait(false);
        if (error is not null || upload is null)
        {
            pass.Errors.Add(Explain(error ?? "no receipt"));
            return pass;
        }

        pass.SarifId = upload.Id;
        pass.StatusUrl = upload.Url;
        await SettleAsync(pass, upload.Id, statusAttempts, statusDelay ?? TimeSpan.FromSeconds(2), ct)
            .ConfigureAwait(false);
        return pass;
    }

    /// <summary>
    /// Reads the repository and states the caveat that applies to it. The ONLY hard stop here is a
    /// repository that cannot be read at all; everything else is a note, and GitHub answers.
    ///
    /// <para><b>Why the missing scope is not a refusal, unlike KS9.3's.</b> The obvious gate — a
    /// private repo without <c>security_events</c> is refused by name, the shape the project board
    /// uses — was BUILT and then removed on a measurement. A POST of a real payload to a private
    /// repository with a token carrying <c>repo</c> and NOT <c>security_events</c> answers
    /// <c>403 "Code scanning is not enabled for this repository"</c>: the entitlement wall, saying
    /// nothing about the token. The scope requirement is therefore unproven on this path, and a
    /// client-side refusal built on it would deny an owner whose organisation HAS Advanced Security
    /// a call that may well succeed. Notes cost nothing and are true; a refusal built on a guess is
    /// the failure this project has paid for before.</para>
    /// </summary>
    private async Task<bool> PreflightAsync(GithubSarifPass pass, string tokenSource, CancellationToken ct)
    {
        var (info, error) = await client.GetRepoAsync(repo, ct).ConfigureAwait(false);
        if (error is not null || info is null)
        {
            pass.Errors.Add($"could not read {repo}: {error ?? "no answer"}");
            return false;
        }

        if (!info.Private)
        {
            pass.Notes.Add($"{repo} is public — code scanning is free here, no Advanced Security needed.");
            return true;
        }

        pass.Notes.Add($"{repo} is PRIVATE — {AdvancedSecurityNote}");
        var (scopes, scopeError) = await client.ProbeScopesAsync(ct).ConfigureAwait(false);
        if (scopeError is not null || scopes is null) return true;
        if (GithubProjects.Scopes(scopes).Contains(PrivateScope, StringComparer.OrdinalIgnoreCase)) return true;

        pass.Notes.Add(
            $"GitHub documents '{PrivateScope}' for a private upload and this token ({tokenSource}) carries " +
            $"[{scopes}]. attempting anyway — if the 403 below is about the TOKEN, the owner grants it " +
            $"once: {GrantCommand}");
        return true;
    }

    /// <summary>Asks what became of the upload until GitHub stops saying "pending". A document
    /// GitHub rejects fails HERE and nowhere else, so a pass that never asked would report a
    /// rejected SARIF as a success.</summary>
    private async Task SettleAsync(
        GithubSarifPass pass, string sarifId, int attempts, TimeSpan delay, CancellationToken ct)
    {
        var wait = delay;
        for (var i = 0; i < Math.Max(1, attempts); i++)
        {
            var (status, error) = await client.SarifStatusAsync(repo, sarifId, ct).ConfigureAwait(false);
            if (error is not null)
            {
                pass.Errors.Add($"upload {sarifId} accepted but its status could not be read: {Explain(error)}");
                return;
            }

            pass.ProcessingStatus = status?.ProcessingStatus;
            if (status is null) return;
            if (string.Equals(status.ProcessingStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var why = status.Errors is { Count: > 0 } errors ? string.Join("; ", errors) : "no reason given";
                pass.Errors.Add($"GitHub rejected the document: {why}");
                return;
            }
            if (!string.Equals(status.ProcessingStatus, "pending", StringComparison.OrdinalIgnoreCase)) return;
            if (i + 1 < attempts && wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Turns the two failures that actually happen into the sentence that names the cause.
    /// A bare 403 sends every reader to the scope page, and for a private repo the scope is not the
    /// problem.</summary>
    private static string Explain(string error) =>
        error.StartsWith("403", StringComparison.Ordinal)
            ? $"{error} — {AdvancedSecurityNote} if the repository IS public or does have it, the token may be " +
              $"missing '{PrivateScope}': {GrantCommand}"
            : error;
}

/// <summary>The rendered document and the two counts that describe its reach.</summary>
public sealed record SarifPayload(string Json, IReadOnlyList<SarifBugFinding> Findings, int WithoutLocation);
