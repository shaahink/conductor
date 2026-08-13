using Conductor.Models;

namespace Conductor.Core.Store;

/// <summary>
/// KS0.2 — what the <c>runs.status</c> column is allowed to say, and which of those sayings mean the
/// run is over.
/// <para>The column had exactly two writers before this checkpoint: <c>InitializeRun</c>, which sets
/// the literal <c>running</c>, and <c>RecordRunEnd</c>, which sets <c>State.Status.ToString()</c> —
/// so a finished run said <c>Completed</c> in PascalCase while a live one said <c>running</c> in
/// lower. Nothing read the difference because nothing else ever wrote the column: a run that ended
/// <c>NeedsHuman</c> or <c>Paused</c> said <c>running</c> for ever (FU-F1-06, open since
/// 2026-07-10), which is where this machine's four phantom rows come from.</para>
/// <para>Now that parks are written too, the vocabulary has to be one vocabulary, because
/// <see cref="StateRepair"/> asks this column whether a store may be written and
/// <c>history</c> renders it. It is lower_snake, and <see cref="IsTerminal"/> is the only place that
/// decides what "over" means.</para>
/// </summary>
public static class RunRecord
{
    /// <summary>The run is over and will not be resumed: closed by hand through the CLI.</summary>
    public const string Closed = "closed";

    /// <summary>What a run in this state should say in <c>runs.status</c>.
    /// <para><b>Only the parks get their own word.</b> <c>Idle</c>, <c>Waiting</c>, <c>Backoff</c> and
    /// <c>VerifyingGates</c> all stay <c>running</c> on purpose: an engine in any of them is alive
    /// and about to spawn a session, and a run whose row stopped saying <c>running</c> while an
    /// engine holds it is a run the repair pass would believe it may write
    /// (<see cref="StateRepair"/>). The states that survive the engine's exit are the states worth
    /// recording, and they are exactly the ones FU-F1-06 names.</para></summary>
    public static string StatusText(RunStatus status) => status switch
    {
        RunStatus.Paused => "paused",
        RunStatus.NeedsHuman => "needs_human",
        RunStatus.AwaitingOwner => "awaiting_owner",
        RunStatus.Completed => "completed",
        RunStatus.Aborted => "aborted",
        _ => "running",
    };

    /// <summary>Is this row finished? Case- and vocabulary-tolerant on purpose: rows written before
    /// KS0.2 carry <c>Completed</c> and <c>Aborted</c> in PascalCase, and they mean what they say.
    /// Everything not on this list — including the empty string a corrupt row can carry — counts as
    /// unfinished, because the cost of guessing wrong is writing a store an engine is using.</summary>
    public static bool IsTerminal(string? status) =>
        status is not null
        && (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("aborted", StringComparison.OrdinalIgnoreCase)
            || status.Equals(Closed, StringComparison.OrdinalIgnoreCase));

    /// <summary>The statuses <c>conductor run close</c> will write. A record can be closed as
    /// <c>closed</c> (the honest default: it stopped, and nobody is claiming it finished its work),
    /// or as <c>completed</c>/<c>aborted</c> when the operator knows which it was.</summary>
    public static readonly IReadOnlyList<string> CloseableAs = [Closed, "completed", "aborted"];
}
