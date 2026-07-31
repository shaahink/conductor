using Conductor.Core;
using Conductor.Core.Update;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// SC8.3 — doctor's answer to "am I running the engine I think I am, and is there a newer one?"
/// Doctor is where an operator already goes before a run, so it is where that belongs; the stale
/// engine question ("is the run using stale engine code") is the defect GAP-ANALYSIS records as
/// having burned three sessions.
///
/// <para>Split into its own file rather than growing <c>DoctorCommand.cs</c> past the 500-line
/// architecture ceiling — the ceiling is a measurement, not a suggestion.</para>
///
/// <para>Three constraints shape the check. It never FAILS: an offline machine is not a broken one,
/// and a stale engine is a warn at worst. It is cheap: a successful lookup is memoised for six hours
/// in a USER-level cache — not the run's <c>.conductor/</c>, so doctor keeps its promise never to
/// write run state — and a cold check gets 4 seconds, not the 30 the update verb takes. And it is
/// switchable off entirely, by <c>--no-update-check</c> or <c>CONDUCTOR_NO_UPDATE_CHECK</c>.</para>
/// </summary>
public sealed partial class DoctorCommand : AsyncCommand<DoctorSettings>
{
    /// <summary>A cold lookup's budget. Doctor advertises itself as a &lt;2s health check; four
    /// seconds once every six hours is the most this may cost, and an unreachable feed is an answer
    /// rather than a hang.</summary>
    private static readonly TimeSpan UpdateProbeTimeout = TimeSpan.FromSeconds(4);

    internal static async Task<Check> CheckUpdateAsync(DateTimeOffset now)
    {
        var running = BuildInfo.Current.Full;
        if (UpdateCheckCache.Disabled)
            return new Check("update", "ok", $"{running} — release checks are off ({UpdateCheckCache.DisableEnvVar})");

        if (!SemVer.TryParse(BuildInfo.Current.Version, out var current))
            return new Check("update", "warn",
                $"this binary reports '{BuildInfo.Current.Version}', which is not a semantic version — " +
                "nothing can be compared against a release");

        var cached = UpdateCheckCache.ReadFresh(now);
        GithubRelease? release;
        string? error = null;
        var age = "just now";
        if (cached is not null)
        {
            release = cached.AsRelease();
            age = cached.AgeText(now);
        }
        else
        {
            using var client = new ReleaseClient(UpdateProbeTimeout);
            (release, error) = await client.LatestAsync().ConfigureAwait(false);
            if (release is not null) UpdateCheckCache.Write(release, now);
        }

        var status = UpdateStatus.Decide(current, release, error);
        var feed = ReleaseClient.FeedIsOverridden ? $" [feed: {ReleaseClient.FeedUrl}]" : "";

        // An unanswerable question is reported as unanswered, never as "up to date": a laptop on a
        // plane must not be told its engine is current when nothing checked.
        if (!status.Known)
            return new Check("update", "ok", $"{running} — could not check for a newer release ({status.Detail}){feed}");

        return status.Available
            ? new Check("update", "warn",
                $"{running} — {status.Detail}; install it with `conductor update`{feed} (checked {age})")
            : new Check("update", "ok", $"{running} — {status.Detail} {status.Tag}{feed} (checked {age})");
    }
}
