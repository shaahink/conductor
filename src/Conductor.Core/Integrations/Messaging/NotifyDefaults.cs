namespace Conductor.Core.Integrations.Messaging;

/// <summary>K5.4 — the built-in shape of every composed push, as the templates an owner overrides.
/// These ARE the templates: there is no second, hard-coded rendering path that the override merely
/// decorates, so what the owner edits is exactly what the engine ships.
/// <para>Every optional fact sits alone on its own line, because <see cref="NotifyTemplate"/> drops a
/// line that comes out blank. A fact that shares a line with a label — <c>gates: {gates}</c> — keeps
/// its label on purpose; a fact that must vanish entirely gets its own line.</para>
/// <para>The identity and context lines are NOT here. They are stamped on the wire, at the one point
/// every message passes through (FU-OWNER-11, K5.4), so no template can drop the two facts that make
/// a message attributable.</para></summary>
public static class NotifyDefaults
{
    /// <summary>KS11.3 / CHAPAR CH-5 — headline, then what landed, then what PROVES it, then the
    /// numbers. Facts: outcome, duration, landed, result, proof, telemetry, report — and the K5.4
    /// facts progress, gates and cost, kept so an owner override written against the old shape still
    /// renders instead of being refused.
    /// <para>The push used to be a status line plus clipped result text: it said what happened and
    /// never said what showed it, and the cost sat below the prose where a phone cuts it off. Now a
    /// checkpoint push reads standalone — what landed, what proves it, what it has cost.</para></summary>
    public const string SessionEnd = """
<b>{outcome}</b>{duration}
{landed}
{result}
{proof}
{telemetry}
{report}
""";

    /// <summary>Facts: outcome, duration, checkpoints, skipped, telemetry, report (and cost, kept
/// for overrides). The order is the ask —
    /// outcome, cost, checkpoint count, duration, report — rather than the engine build string the
    /// old message led with.</summary>
    public const string RunComplete = """
<b>{outcome}</b>{duration}
{checkpoints}
{skipped}
{telemetry}
{report}
""";

    /// <summary>Facts: batch, artifact, telemetry (and progress, kept for overrides). Rendered as
/// the CAPTION on the artifact itself, so
    /// it is bounded by Telegram's 1024-character caption limit rather than the message limit.</summary>
    public const string Evidence = """
<b>evidence</b>{batch}
• {artifact}
{telemetry}
""";

    /// <summary>KS11.3 / CHAPAR CH-4 — the bot's FIRST message, in the admin's voice. Facts: name,
    /// plan, budget, arrivals, asks.
    /// <para>No chat should ever receive its first push without having been told the rules. A chat
    /// added mid-run used to get a session-end message with no frame at all — what run, whose
    /// machine, why it is being told.</para></summary>
    public const string OnboardingAdmin = """
<b>{name}</b> — this chat is now the control surface for a conductor run.
{plan}
{budget}
<b>What arrives here</b>
{arrivals}
<b>What you can ask</b>
{asks}
""";

    /// <summary>KS11.3 / CHAPAR CH-4 — the same message in the observer's voice: a welcome to a
    /// project dashboard rather than a console. Facts: name, plan, budget, arrivals, asks.</summary>
    public const string OnboardingObserver = """
<b>{name}</b> — this chat is now following a conductor run.
An agent is working through a plan on its own; this is where it reports.
{plan}
{budget}
<b>What arrives here</b>
{arrivals}
<b>What you can ask</b>
{asks}
""";

    /// <summary>DV6.3 — the caption that rides the board page. Facts: headline, stale, owner,
    /// ledger, telemetry.
    /// <para>The second line is the one that has to be there: a document sitting in a chat has no way
    /// of saying how old it is, and a reader scrolling back a week would otherwise read last
    /// Tuesday's board as today's. It names the instant and the boundary, and the page repeats both
    /// inside itself for the copy that gets forwarded on.</para></summary>
    public const string Board = """
<b>{headline}</b>
{stale}
{owner}
{ledger}
{telemetry}
""";

    /// <summary>Facts: count, noun, lines. What a batch too large to attach is announced as.</summary>
    public const string EvidenceOverflow = """
<b>evidence</b> — {count} further {noun}, not attached
{lines}
""";

    /// <summary>DV1.2 — ONE owner-queue obligation, in CH-5's grammar. Facts: headline, unblocks,
    /// why, clears, telemetry.
    ///
    /// <para>The grammar maps exactly: the headline is what it IS, <c>unblocks</c> and <c>why</c> are
    /// what MAKES it the owner's rather than the engine's — CH-5's proof half — and <c>clears</c> is
    /// the ask. The command is the last line and it is in monospace, because it is the one part of
    /// the message a phone reader taps to copy.</para>
    ///
    /// <para>One obligation per message, never a digest. A digest of three is read as one thing to
    /// deal with later, and the two under the first one are the ones that get lost; it also makes
    /// "already announced" a property of an ITEM rather than of a batch, which is what lets the
    /// same item stay unsent on every later boundary.</para></summary>
    public const string OwnerQueueItem = """
<b>{headline}</b>
{unblocks}
{why}
{clears}
{telemetry}
""";
}
