namespace Conductor.Core.Integrations.Cloud;

/// <param name="Action">What happened, for the event log: <c>followUp</c>, <c>refusedGit</c>,
/// <c>refusedCreate</c>, <c>usage</c>.</param>
/// <param name="Spawned">Whether the CLI was actually invoked. Every refusal path asserts this is
/// false — a preflight that refuses and then spawns anyway is the defect §6.8 is about.</param>
public sealed record CloudVerbResult(
    string Reply, string Action, string? SessionId = null, string? Url = null, bool Spawned = false)
{
    /// <summary>Always the word, never a number. §2.4 item 1.</summary>
    public string Cost => CloudCliFacts.UnknownCost;
}

/// <summary>DV5.1 — everything <c>/cloud</c> decides, with no chat and no channel in sight.
///
/// <para>The verb has two directions and the installed CLI only gives conductor one of them. The
/// FOLLOW-UP direction — a message to a cloud session that already exists — is headless and is
/// driven here. The CREATE direction is interactive-only on claude
/// <see cref="CloudCliFacts.MeasuredVersion"/>, so it is refused with the platform's own words plus
/// the exact command to run on a terminal; conductor does not fake a TTY to get around a refusal a
/// research-preview surface makes on purpose.</para>
///
/// <para>The git preflight (§6.8) gates the CREATE direction, because that is the direction that
/// clones from the remote. It does NOT gate a follow-up — a follow-up messages a workspace that was
/// cloned when the session was made, so refusing it for a dirty tree would be a false gate — but the
/// reply states the local git state anyway, so the owner knows what that session cannot see.</para></summary>
public sealed class CloudVerb
{
    /// <summary>Long enough for a cloud session to think, short enough that the owner is told
    /// something rather than left watching a chat that never answers.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly ICloudCli _cli;
    private readonly Func<string, CloudPreflightResult> _preflight;
    private readonly TimeSpan _timeout;

    public CloudVerb(ICloudCli? cli = null, Func<string, CloudPreflightResult>? preflight = null,
        TimeSpan? timeout = null)
    {
        _cli = cli ?? new ClaudeCloudCli();
        _preflight = preflight ?? CloudPreflight.Probe;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<CloudVerbResult> RunAsync(string repoDir, string projectName, string argument,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var arg = argument.Trim();
        var where = projectName.Length > 0 ? projectName : repoDir;

        if (arg.Length == 0) return Usage(repoDir, where);

        var split = arg.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (CloudSessionRef.TryParse(split[0]) is { } session)
        {
            var message = split.Length > 1 ? split[1].Trim() : "";
            return message.Length == 0
                ? new CloudVerbResult(
                    $"/cloud {session.Id} names a cloud session but says nothing to it.\n"
                    + $"Use: /cloud {session.Id} <what to ask it>", "refusedCreate", session.Id, session.Url)
                : await FollowUpAsync(repoDir, where, session, message, ct).ConfigureAwait(false);
        }

        return Create(repoDir, where, arg);
    }

    // ────────────────────────────── the direction that works ──────────────────────────────

    private async Task<CloudVerbResult> FollowUpAsync(string repoDir, string where, CloudSessionRef session,
        string message, CancellationToken ct)
    {
        var git = _preflight(repoDir);
        var result = await _cli.FollowUpAsync(repoDir, session.Id, message, _timeout, ct).ConfigureAwait(false);
        var named = session.WithUrlFrom(result.Output);

        var head = result.TimedOut
            ? $"Cloud session {named.Describe()} did not answer within {_timeout.TotalMinutes:0} minutes. It is still running; ask it again for the result."
            : result.Ok
                ? $"Cloud session {named.Describe()} answered:\n\n{Trim(result.Output)}"
                : $"Cloud session {named.Describe()} failed (exit {result.ExitCode}):\n\n{Trim(Pick(result))}";

        return new CloudVerbResult(
            head
            + $"\n\nCost: {CloudCliFacts.UnknownCost} — a cloud session reports no per-turn spend to this engine."
            + $"\nLocal {where}: {git.Detail}",
            "followUp", named.Id, named.Url, Spawned: true);
    }

    // ────────────────────────────── the direction that does not ──────────────────────────────

    private CloudVerbResult Create(string repoDir, string where, string task)
    {
        var git = _preflight(repoDir);
        if (!git.Ok)
            return new CloudVerbResult(
                $"/cloud refused for {where}: {git.Detail}\n\n"
                + "A cloud session clones from the remote, so it would run on the remote's commit and "
                + "not on what you are looking at. Commit and push, then ask again.",
                "refusedGit");

        return new CloudVerbResult(
            $"/cloud cannot start a new cloud session for {where}. This engine drives claude "
            + $"{CloudCliFacts.MeasuredVersion}, and starting one is interactive-only there:\n\n"
            + CloudCliFacts.RefusalWithoutTty
            + "\n\nRun this on a terminal, then send follow-ups from here with /cloud <session-id> <message>:\n\n"
            + CloudCliFacts.CreateCommand(task)
            + $"\n\n{where} is ready for it: {git.Detail}",
            "refusedCreate");
    }

    private CloudVerbResult Usage(string repoDir, string where)
    {
        var git = _preflight(repoDir);
        return new CloudVerbResult(
            $"/cloud talks to a cloud session about {where}.\n\n"
            + "  /cloud <session-id|url> <message>   send it a message (this works from here)\n"
            + "  /cloud <task>                       tells you the command to start one, which needs a terminal\n\n"
            + $"Git right now: {git.Detail}\n"
            + $"Cloud spend is always reported as {CloudCliFacts.UnknownCost}; there is no meter for it.",
            "usage");
    }

    /// <summary>Which stream actually said something, when the call failed.</summary>
    private static string Pick(CloudCliResult r) =>
        r.StdErr.Trim().Length > 0 ? r.StdErr : r.Output;

    /// <summary>A phone screen's worth. Truncation is announced, because a silently clipped answer
    /// from an agent is the same class of lie as a silently clipped session result (K5.1).</summary>
    private static string Trim(string text)
    {
        var t = (text ?? "").Trim();
        const int Cap = 1500;
        return t.Length <= Cap ? t : t[..Cap] + $"\n… (clipped at {Cap} characters; the full answer is in the session)";
    }
}
