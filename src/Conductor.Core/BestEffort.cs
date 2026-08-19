using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Conductor.Core;

/// <summary>
/// A cleanup step whose failure is genuinely not actionable: closing a response the client already hung
/// up on, stopping a listener that is already disposed, deleting a scratch file that is already gone.
/// </summary>
/// <remarks>
/// KS6.1. This codebase wrote the same policy fifteen times as <c>catch (Exception) { /* best effort */ }</c>
/// — the same decision, with no name and no trace. When one of those swallows fired there was nothing in
/// any log to say so, which is how a shutdown that half-worked looked identical to one that worked.
/// <para/>
/// RCS1075 is the rule that found them. The fix a rule like that deserves is one place where the policy
/// lives, not fifteen narrower catch clauses: narrowing each site would have traded a silent swallow for a
/// crash on the exception nobody predicted, in the one code path that runs while the process is already
/// going down.
/// <para/>
/// The failure is still tolerated — that is the whole point of the call — but it is recorded at Debug
/// along with the expression that failed, so a strange shutdown leaves something to read.
/// </remarks>
public static class BestEffort
{
    /// <summary>Runs <paramref name="step"/>, tolerating any failure and logging it at Debug.</summary>
    /// <param name="step">The cleanup step. Its source text is captured for the log line.</param>
    /// <param name="logger">Where the tolerated failure is recorded. Null means nowhere — use it only
    /// where no logger has been threaded to the call site yet.</param>
    /// <param name="stepText">Compiler-supplied source text of <paramref name="step"/>; never passed by hand.</param>
    public static void Run(
        Action step,
        ILogger? logger = null,
        [CallerArgumentExpression(nameof(step))] string? stepText = null)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "best effort step failed: {Step}", stepText);
        }
    }
}
