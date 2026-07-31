using Conductor.Core.Events;

namespace Conductor.Core.Http;

/// <summary>
/// SC7.2 — the per-session digest on the wire (devcontext #10's worked example, as data). Ranked and
/// flattened here rather than in the client: two readers sorting a map by count independently is two
/// chances to disagree about what the same session did.
/// </summary>
public sealed record SessionDigestDto(
    int ToolCalls,
    int DistinctTools,
    IReadOnlyList<DigestCountDto> Mix,
    IReadOnlyList<DigestCountDto> FilesTouched,
    int FileWrites,
    IReadOnlyList<string> Claims,
    IReadOnlyList<string> BackgroundJobs,
    IReadOnlyList<string> Commands)
{
    /// <summary>Projects a stored digest, or null when there is none. Ordering is
    /// <see cref="SessionDigest.Ranked"/>'s: count descending, then name, stable across renders.</summary>
    public static SessionDigestDto? From(SessionDigest? digest)
    {
        if (digest == null || digest.IsEmpty) return null;
        return new SessionDigestDto(
            ToolCalls: digest.ToolCalls,
            DistinctTools: digest.DistinctTools,
            Mix: [.. SessionDigest.Ranked(digest.Mix).Select(p => new DigestCountDto(p.Key, p.Value))],
            FilesTouched: [.. SessionDigest.Ranked(digest.FilesTouched).Select(p => new DigestCountDto(p.Key, p.Value))],
            FileWrites: digest.FileWrites,
            Claims: digest.Claims,
            BackgroundJobs: digest.BackgroundJobs,
            Commands: digest.Commands);
    }
}

public sealed record DigestCountDto(string Name, int Count);
