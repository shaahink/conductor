namespace Conductor.Core.Courier;

/// <summary>PK3.3 / D4 - at a session boundary, a live run tells the machine's courier which project it
/// is, once, so a note about this plan is filed without anybody running <c>courier allow</c>.
///
/// <para>Once per run process, and only once it has been ANSWERED: a courier that is down or still
/// starting is asked again at the next boundary, while an answer - added, already there, or refused
/// by name - is final and is written to the run log. A machine with no operative courier is not asked
/// at all.</para></summary>
/// <param name="stateHomeRoot">The machine's state home, or null for the resolved one.</param>
public sealed class CourierIntroduction(string? stateHomeRoot = null)
{
    private bool _answered;

    /// <summary>Whether a courier has answered this run's hello.</summary>
    public bool Answered => _answered;

    /// <summary>Says hello if nobody has answered yet. Never throws: a boundary is not the place for a
    /// courier to stop a run.</summary>
    /// <param name="plan">The plan's name.</param>
    /// <param name="repo">The run's checkout.</param>
    /// <param name="runId">The run, for the entry's <c>by</c> marker.</param>
    /// <param name="log">The run log.</param>
    /// <returns>The courier's answer, or null when nothing was asked or nothing answered.</returns>
    public async Task<CourierAck?> AtBoundaryAsync(string plan, string repo, string? runId, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (_answered || string.IsNullOrWhiteSpace(plan) || string.IsNullOrWhiteSpace(repo)) return null;
        if (!CourierPrecedence.Configured(stateHomeRoot)) return null;

        using var client = CourierClient.TryOpen(stateHomeRoot, out _);
        if (client is null) return null;

        var ack = await client.IntroduceAsync(new CourierHello(Path.GetFullPath(repo), plan, runId)).ConfigureAwait(false);
        if (ack.Unanswered) return null;

        _answered = true;
        log(ack.Accepted
            ? "courier: " + ack.Detail
            : "courier would not file notes for this run: " + ack.Detail + " - allow it by hand: conductor courier allow --repo <path> --plan <name>");
        return ack;
    }
}
