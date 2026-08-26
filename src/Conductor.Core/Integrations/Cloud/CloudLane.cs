using Conductor.Models;

namespace Conductor.Core.Integrations.Cloud;

/// <summary>What a cloud lane did. There is no <c>Verdict</c> here and there never will be: the lane
/// produces an OPINION and the referee stays on this machine.</summary>
public enum CloudLaneOutcome
{
    /// <summary>The flag is off, which is the default and the state of every plan that says nothing.
    /// Nothing was spawned.</summary>
    Disabled = 0,

    /// <summary>The repo is not in a state the CLI would review. Nothing was spawned.</summary>
    Refused = 1,

    /// <summary>A review came back. What it says is an opinion for a human, not a verdict.</summary>
    Reviewed = 2,

    /// <summary>The CLI ran and failed.</summary>
    Failed = 3,

    /// <summary>Still running out there when the deadline passed. §2.4 item 2: nothing out there will
    /// stop it, so the run says so and moves on.</summary>
    TimedOut = 4,
}

/// <param name="ArtifactPath">Where the review was written whole, or null. The payload is stored
/// UNPARSED on purpose — see <see cref="CloudLane"/>.</param>
/// <param name="Spawned">Whether the CLI was reached. Both no-spawn outcomes assert this is false.</param>
public sealed record CloudLaneResult(
    CloudLaneOutcome Outcome, string Summary, string? ArtifactPath = null, bool Spawned = false)
{
    /// <summary>Always the word. §2.4 item 1: there is no per-turn telemetry out there.</summary>
    public string Cost => CloudCliFacts.UnknownCost;

    /// <summary>KS5.2's shape, deliberately always null: a cloud lane has no receipt to hand the
    /// ledger, and <c>RunSpendLedger.Record(null, …)</c> is what turns that into "unknown, not zero"
    /// in the run's own log rather than a $0.00 row in its report.</summary>
    public Accounting.SpendReceipt? Spend => null;
}

/// <summary>DV5.2 / findings §2.3 CL-1 — cloud as a lane, local as the referee.
///
/// <para>Behind <see cref="CloudLaneConfig.Enabled"/>, DEFAULT OFF, and off is where every existing
/// plan already is. What it runs is <c>claude ultrareview</c>, because that is the only cloud surface
/// the installed CLI exposes to something without a terminal — DV5.1 measured <c>--cloud</c> refusing
/// non-interactive use three separate ways — and because a second-opinion review is precisely the
/// work CL-1 allows: it needs no conductor tools and it settles nothing.</para>
///
/// <para><b>The referee never moves.</b> Nothing here reaches the verdict engine, the gate battery or
/// the task-claim path, and that is a source-scanned invariant in <c>ArchitectureBoundaryTests</c>
/// rather than a promise in a comment. Every gate still runs on this machine; what comes back is an
/// artifact a person reads.</para>
///
/// <para><b>The payload is opaque.</b> The CLI's output is stored whole and never parsed into a
/// schema. DV5.1 already paid for guessing at a shape this engine had not observed, and a review
/// summarised by a parser that misread it is worse than one nobody summarised.</para></summary>
public sealed class CloudLane
{
    private readonly CloudLaneConfig? _config;
    private readonly ICloudCli _cli;
    private readonly Func<string, CloudPreflightResult> _preflight;

    public CloudLane(CloudLaneConfig? config, ICloudCli? cli = null,
        Func<string, CloudPreflightResult>? preflight = null)
    {
        _config = config;
        _cli = cli ?? new ClaudeCloudCli();
        _preflight = preflight ?? CloudPreflight.Probe;
    }

    /// <summary>Whether this plan has asked for the lane at all. Read before anything else is
    /// constructed, so a plan that never mentions the block cannot pay for it.</summary>
    public bool Enabled => _config is { Enabled: true };

    /// <summary>What stops a BUNDLED lane, as opposed to a cloned one.
    ///
    /// <para>Deliberately narrower than <see cref="CloudPreflightResult.Ok"/>. <c>/cloud</c>'s create
    /// direction needs the remote to have the commit, because a cloud SESSION clones from it; the
    /// review verb bundles the local branch instead, and the CLI's own refusal names only the local
    /// problem ("If you have local edits, stage or commit them first"). Gating on a pushed branch here
    /// would refuse work that would have succeeded — the same mistake as inventing a stricter session
    /// id than the CLI's. The remote state is still REPORTED; it is just not a gate.</para></summary>
    public static bool Blocks(CloudPreflightVerdict verdict) =>
        verdict is CloudPreflightVerdict.NothingToClone
                or CloudPreflightVerdict.DetachedHead
                or CloudPreflightVerdict.DirtyTree;

    public async Task<CloudLaneResult> RunAsync(string repoDir, string artifactDir, string label,
        CancellationToken ct)
    {
        if (_config is not { Enabled: true } config)
            return new CloudLaneResult(CloudLaneOutcome.Disabled,
                "the cloud lane is off (plan.cloud.enabled is not set); nothing was sent anywhere.");

        var git = _preflight(repoDir);
        if (Blocks(git.Verdict))
            return new CloudLaneResult(CloudLaneOutcome.Refused,
                $"cloud lane refused: {git.Detail}");

        var timeout = TimeSpan.FromMinutes(config.TimeoutMinutes);
        var r = await _cli.ReviewAsync(repoDir, config.Base, timeout, ct).ConfigureAwait(false);

        if (r.TimedOut)
            return new CloudLaneResult(CloudLaneOutcome.TimedOut,
                $"cloud lane did not answer within {config.TimeoutMinutes} minutes; it may still be "
                + $"running out there, and nothing here can stop it. Cost: {CloudCliFacts.UnknownCost}.",
                Spawned: true);

        if (!r.Ok)
            return new CloudLaneResult(CloudLaneOutcome.Failed,
                $"cloud lane failed (exit {r.ExitCode}): {Head(r.StdErr.Trim().Length > 0 ? r.StdErr : r.Output)}. "
                + $"Cost: {CloudCliFacts.UnknownCost}.",
                Spawned: true);

        Directory.CreateDirectory(artifactDir);
        var path = Path.Combine(artifactDir, $"cloud-review-{Safe(label)}.txt");
        AtomicFile.Write(path, r.Output);

        return new CloudLaneResult(CloudLaneOutcome.Reviewed,
            $"cloud lane returned a second opinion ({r.Output.Length} characters), stored whole and "
            + $"unparsed. It settles nothing — every gate still runs here. Cost: {CloudCliFacts.UnknownCost}."
            + $"\nLocal git: {git.Detail}",
            path, Spawned: true);
    }

    private static string Head(string text)
    {
        var t = (text ?? "").Trim().ReplaceLineEndings(" ");
        return t.Length <= 300 ? t : t[..300] + "…";
    }

    private static string Safe(string label)
    {
        var cleaned = new string([.. (label ?? "").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        return cleaned.Trim('-') is { Length: > 0 } ok ? ok : "lane";
    }
}
