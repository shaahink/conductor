namespace Conductor.Core.Http;

/// <summary>M5.3 native console: one raw stdout line from the current session's agent process, streamed
/// over <c>GET /console/current</c>. <see cref="Seq"/> is the 1-based line index within the session log,
/// so a reconnecting client passes <c>?since=</c> to resume without replaying what it already saw.</summary>
public sealed record ConsoleLineDto(long Seq, string Text);
