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
    /// <summary>Facts: outcome, duration, progress, landed, gates, result, cost, report.</summary>
    public const string SessionEnd = """
<b>{outcome}</b>{duration}
{progress}
{landed}
gates: {gates}
{result}
{cost}
{report}
""";

    /// <summary>Facts: outcome, duration, checkpoints, skipped, cost, report. The order is the ask —
    /// outcome, cost, checkpoint count, duration, report — rather than the engine build string the
    /// old message led with.</summary>
    public const string RunComplete = """
<b>{outcome}</b>{duration}
{checkpoints}
{skipped}
{cost}
{report}
""";

    /// <summary>Facts: batch, artifact, progress. Rendered as the CAPTION on the artifact itself, so
    /// it is bounded by Telegram's 1024-character caption limit rather than the message limit.</summary>
    public const string Evidence = """
<b>evidence</b>{batch}
• {artifact}
{progress}
""";

    /// <summary>Facts: count, noun, lines. What a batch too large to attach is announced as.</summary>
    public const string EvidenceOverflow = """
<b>evidence</b> — {count} further {noun}, not attached
{lines}
""";
}
