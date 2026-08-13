using Conductor.Core.Accounting;

namespace Conductor.Core;

/// <summary>
/// KS5.2 — what an advisor consult produced AND what the provider billed for producing it.
/// <para>The two travel together because they arrive together and are lost together: the old
/// <c>Task&lt;string?&gt;</c> answer threw the result envelope away after peeling the text out of it, so
/// the only thing left to record spend from was the wall clock — which is how
/// <c>0.0005m * elapsed.TotalSeconds</c> came to be written into the <c>costs</c> table as if it were a
/// bill. An advisor that fails still costs money, so <see cref="Spend"/> can be present when
/// <see cref="Text"/> and <see cref="Verdict"/> are both null.</para>
/// </summary>
/// <param name="Text">The model's raw answer, unwrapped from the provider envelope. Null when the
/// advisor is off, timed out, or failed.</param>
/// <param name="Verdict">The parsed action, for the callers that asked for one. Null when the answer
/// was free-shape (a plan import) or unparseable.</param>
/// <param name="Spend">What the provider billed, or null when it reported nothing.</param>
public sealed record AdvisorReply(string? Text, AdvisorVerdict? Verdict, SpendReceipt? Spend)
{
    /// <summary>The reply for an advisor that was never spawned: nothing said, nothing spent.</summary>
    public static AdvisorReply None { get; } = new(null, null, null);
}
