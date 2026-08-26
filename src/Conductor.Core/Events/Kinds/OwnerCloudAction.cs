namespace Conductor.Core.Events;

/// <summary>DV5.1 — an owner action taken against a cloud session from the chat surface.
///
/// <para>Recorded because it is the one thing a cloud session leaves behind that this engine can
/// see. §2.4 item 3: a cloud session cannot reach the control plane and cannot claim a checkpoint,
/// so the run's own record of "the owner sent work somewhere conductor cannot watch" is this row and
/// nothing else — including the refusals, which are the more useful half when an owner asks later
/// why nothing happened.</para></summary>
public sealed record OwnerCloudAction : ConductorEvent
{
    /// <summary>followUp / refusedGit / refusedCreate / usage.</summary>
    public required string Action { get; init; }

    public required string Repo { get; init; }

    /// <summary>The cloud session this was about, when there was one. Named for the cloud rather
    /// than sharing <see cref="ConductorEvent.SessionId"/>: that field is a conductor session number,
    /// and the two are not the same thing in any sense.</summary>
    public string? CloudSessionId { get; init; }

    public string? Url { get; init; }

    /// <summary>Always the word "unknown". There is no per-turn telemetry for a machine conductor
    /// does not control (findings §2.4 item 1), and a zero here would read as free.</summary>
    public string Cost { get; init; } = "unknown";
}
