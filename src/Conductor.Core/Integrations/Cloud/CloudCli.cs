namespace Conductor.Core.Integrations.Cloud;

/// <param name="TimedOut">The call was still running when the deadline passed. Distinct from a
/// non-zero exit: a cloud session that is still thinking is not a cloud session that failed, and the
/// owner is told which one happened.</param>
public sealed record CloudCliResult(int ExitCode, string Output, string StdErr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// <summary>DV5.1 — the one call this engine may make into the cloud surface: a message to a cloud
/// session that already exists.
///
/// <para>A seam and not a static, because every test in this stage drives the whole verb without a
/// network and without an Anthropic account, and because the create direction is refused before this
/// interface is ever reached — there is deliberately no <c>CreateAsync</c> here to be tempted by.</para></summary>
public interface ICloudCli
{
    /// <summary>Sends <paramref name="message"/> to the cloud session named by
    /// <paramref name="sessionId"/>, from <paramref name="repoDir"/>.</summary>
    Task<CloudCliResult> FollowUpAsync(string repoDir, string sessionId, string message,
        TimeSpan timeout, CancellationToken ct);
}

/// <summary>The real one: <c>claude -p "message" --cloud &lt;session-id&gt;</c>, which is the exact
/// invocation the CLI's own refusal message points at.</summary>
public sealed class ClaudeCloudCli : ICloudCli
{
    private readonly string _executable;

    public ClaudeCloudCli(string? executable = null) => _executable = executable ?? CloudCliFacts.Executable;

    public async Task<CloudCliResult> FollowUpAsync(string repoDir, string sessionId, string message,
        TimeSpan timeout, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(_executable, CloudCliFacts.FollowUpArgs(sessionId, message),
            repoDir, timeout, ct).ConfigureAwait(false);
        return new CloudCliResult(r.ExitCode, r.Output, r.StdErr, r.TimedOut);
    }
}
