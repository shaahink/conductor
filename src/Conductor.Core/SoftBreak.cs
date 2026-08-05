using System.Globalization;
using System.Text.Json;

namespace Conductor.Core;

/// <summary>K1.2 — the cooperative rail, in one place, shared by the engine that raises it and the
/// <c>hook-budget</c> command that delivers it.
/// <para><b>What was measured.</b> Across the Sarban face run's 33 post-cap sessions: 11 rollovers,
/// and all 11 ended at 8.00–8.13M tokens — the hard ceiling. Not one stopped at the 6.0M nudge. The
/// nudge was announced ONCE, on the first tool call after the threshold, it named the fact of a limit
/// rather than the budget left, and nothing recorded whether it had been heard. A rail that converts
/// zero times out of eleven is not a rail.</para>
/// <para><b>What changed.</b> The notice is RE-STATED — on a token step and on a wall-clock interval,
/// whichever comes first — it quotes the budget that is actually left at the moment it is delivered
/// (the engine re-writes the signal as the session spends), it states the wrap-up order explicitly
/// with the reason the order is what it is, and each delivery is written back to a file the engine
/// folds into the session record. The next tuning pass reads a measurement instead of inferring
/// one.</para></summary>
public static partial class SoftBreak
{
    public const string SignalFileName = "soft-break";
    public const string DeliveredFileName = "soft-break.delivered";

    /// <summary>Re-state at least this often even if the session is spending slowly. A session that
    /// stalls on one long tool call must not out-wait the only cooperative exit it has.</summary>
    public static readonly TimeSpan RestateInterval = TimeSpan.FromMinutes(3);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The signal file the engine writes once the session crosses its soft threshold, and
    /// re-writes as it keeps spending. <paramref name="CeilingTokens"/> is 0 when the file could not
    /// be parsed — a signal from an older engine, say — which the notice reads as "numbers unknown"
    /// rather than as "no budget pressure".</summary>
    public sealed record Signal(
        long SpentTokens = 0,
        long CeilingTokens = 0,
        long ThresholdTokens = 0,
        string? CheckpointId = null,
        DateTime WrittenUtc = default)
    {
        public long RemainingTokens => Math.Max(0, CeilingTokens - SpentTokens);
        public bool HasNumbers => CeilingTokens > 0;
    }

    /// <summary>What the hook writes back after each delivery: the channel by which a separate,
    /// short-lived process tells the engine the nudge was actually put in front of the agent.</summary>
    public sealed record Delivery(
        int Count = 0,
        DateTime FirstUtc = default,
        long FirstAtTokens = 0,
        DateTime LastUtc = default,
        long LastAtTokens = 0);

    // Outcome — the measurement the session record carries — lives in SoftBreak.Outcome.cs.

    // ── the re-statement rule ──────────────────────────────────────────────────────────────────

    /// <summary>How much the session must spend before the notice is worth repeating: a twentieth of
    /// the margin between the nudge and the ceiling, so the rule scales with the budget instead of
    /// with a number someone picked for one plan. Ten restatements across the tail, at a couple of
    /// hundred tokens each — against a margin measured in millions.</summary>
    public static long RestateTokenStep(Signal signal) =>
        Math.Max(1, (signal.CeilingTokens - signal.ThresholdTokens) / 20);

    /// <summary>Whether the hook should put the notice in front of the agent again. Pure, so the
    /// rule can be tested without a session, a clock or a file.</summary>
    public static bool ShouldRestate(Signal signal, Delivery? previous, DateTime nowUtc, out string reason)
    {
        if (previous is null || previous.Count == 0) { reason = "first"; return true; }
        if (nowUtc - previous.LastUtc >= RestateInterval) { reason = "interval"; return true; }
        if (signal.HasNumbers && signal.SpentTokens - previous.LastAtTokens >= RestateTokenStep(signal))
        {
            reason = "tokens";
            return true;
        }
        reason = "recent";
        return false;
    }

    // ── the notice ─────────────────────────────────────────────────────────────────────────────

    /// <summary>What the agent is actually told. Phrased as the next action rather than as a warning:
    /// "you are near a limit" invites either ignoring it or downing tools immediately, and the whole
    /// value of the cooperative rail is the third option — finish the piece in hand, write it down,
    /// stop. The wrap-up ORDER is stated with its reason, because the reason is what makes an agent
    /// under time pressure follow it: a claim survives a hard stop and an uncommitted handoff does
    /// not.</summary>
    public static string Notice(Signal signal, int noticeNumber)
    {
        var head = noticeNumber <= 1
            ? "CONDUCTOR - SESSION TOKEN BUDGET NEARLY SPENT."
            : $"CONDUCTOR - SESSION TOKEN BUDGET NEARLY SPENT (notice {noticeNumber}; the earlier one still stands).";

        var budget = signal.HasNumbers
            ? $"Remaining: about {Tok(signal.RemainingTokens)} of this session's {Tok(signal.CeilingTokens)} " +
              $"ceiling - {Pct(signal)} left. When it runs out the orchestrator ends the session where it " +
              "stands: committed work is kept, anything still only in your head is not."
            : "This session has used most of the tokens allotted to it and will be ended by the " +
              "orchestrator when they run out. Committed work is kept, anything still only in your " +
              "head is not.";

        var focus = signal.CheckpointId is { Length: > 0 } cp
            ? $"The checkpoint in your hands is {cp}."
            : "";

        return $"""
        {head}

        {budget}
        {focus}

        Wrap up now, in THIS ORDER, and do nothing else. The order is not arbitrary - it runs from
        what survives a hard stop to what does not:
        1. CLAIM FIRST. `conductor task --done <id> --evidence <path>` for anything finished. The
           claim lands in the database the moment you run it, so it is the one thing that survives
           being cut off mid-sentence. Prose in a file is not a claim.
        2. THEN THE HANDOFF. Overwrite the tracker's handoff block for the next session: what you
           finished, what is half-done and exactly where, what is red, and the single next action.
        3. THEN COMMIT AND PUSH. Uncommitted work is lost work.
        4. THEN print your `SESSION-RESULT:` paragraph and end the session.

        Start nothing new - no new checkpoint, no refactor, no "while I'm here" fix, no exploratory
        reading. Ending here is the expected outcome and costs you nothing: the next session picks up
        from your handoff on a fresh, cheap context. Stopping cleanly now is worth more than one more
        edit made in a hurry.
        """;
    }

    private static string Tok(long t) => t >= 1_000_000
        ? (t / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M tokens"
        : t >= 1_000
            ? (t / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k tokens"
            : t.ToString(CultureInfo.InvariantCulture) + " tokens";

    private static string Pct(Signal s) =>
        (100.0 * s.RemainingTokens / Math.Max(1, s.CeilingTokens)).ToString("0", CultureInfo.InvariantCulture) + "%";

    // ── files ──────────────────────────────────────────────────────────────────────────────────

#pragma warning disable MA0045 // small local files, written from the poll loop and a short-lived hook
    public static void WriteSignal(string stateDir, Signal signal)
    {
        try { File.WriteAllText(Path.Combine(stateDir, SignalFileName), JsonSerializer.Serialize(signal, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Null when there is no signal at all. A signal that exists but cannot be parsed comes
    /// back with no numbers rather than as nothing: the agent still gets told to wrap up.</summary>
    public static Signal? ReadSignal(string stateDir)
    {
        var path = Path.Combine(stateDir, SignalFileName);
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Signal>(text, Json) ?? new Signal();
        }
        catch (JsonException) { return new Signal(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static Delivery? ReadDelivery(string stateDir)
    {
        var path = Path.Combine(stateDir, DeliveredFileName);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Delivery>(File.ReadAllText(path), Json);
        }
        catch (JsonException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Folds one delivery into the record and writes it back. Returns what was written so
    /// the caller can number the notice it is about to print.</summary>
    public static Delivery RecordDelivery(string stateDir, Delivery? previous, Signal signal, DateTime nowUtc)
    {
        var next = previous is null || previous.Count == 0
            ? new Delivery(1, nowUtc, signal.SpentTokens, nowUtc, signal.SpentTokens)
            : previous with { Count = previous.Count + 1, LastUtc = nowUtc, LastAtTokens = signal.SpentTokens };
        try { File.WriteAllText(Path.Combine(stateDir, DeliveredFileName), JsonSerializer.Serialize(next, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return next;
    }
#pragma warning restore MA0045
}
