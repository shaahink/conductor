using System.Globalization;

namespace Conductor.Planning;

/// <summary>
/// SC3.1 — the authoring-time half of <see cref="WorkflowEngine.EvaluateCondition"/>. That evaluator
/// ends in <c>_ =&gt; true</c>: an unknown expression is permissive, so <c>runIf: "!gatesgreen"</c>
/// (wrong case) or <c>"gates.green"</c> (wrong shape) silently means "always run" and a typo inverts
/// control flow with no diagnostic (devcontext #4). Permissive at runtime is a deliberate safety
/// choice; permissive at authoring time is not — so <see cref="Validate"/> mirrors the evaluator's
/// parse exactly and <c>PlanConfig</c> rejects the plan at load, naming the vocabulary.
/// <para>The mirror is load-bearing: anything <see cref="Validate"/> accepts must reach a real branch
/// of the evaluator rather than its permissive default, and anything it rejects must be what would
/// have hit that default. <c>ConditionVocabularyTests</c> measures both halves against the evaluator
/// itself rather than trusting these lists to stay in step by inspection.</para>
/// </summary>
public static class ConditionVocabulary
{
    /// <summary>Bare boolean variables, matched ordinally — exactly the cases of the evaluator's switch.</summary>
    public static readonly IReadOnlyList<string> BooleanTokens =
    [
        "verifier.passed", "circuit.broken", "gatesGreen", "hasCommits", "stalled", "stageComplete",
    ];

    /// <summary>Variables that carry a number, usable on the left of a comparison — exactly the cases
    /// of the evaluator's <c>ResolveNumeric</c>.</summary>
    public static readonly IReadOnlyList<string> NumericTokens =
    [
        "verifier.score", "stage.attempts", "newlyDoneCount",
    ];

    /// <summary>Comparison operators, in the order the evaluator scans for them (first match wins,
    /// which is why the two-character forms come first).</summary>
    public static readonly IReadOnlyList<string> Operators = [">=", "<=", ">", "<", "==", "!="];

    /// <summary>One line naming everything an author may write, for the error message that refuses
    /// a plan — a rejection that does not say what IS allowed just moves the guessing.</summary>
    public static string Describe() =>
        "boolean tokens: " + string.Join(", ", BooleanTokens) +
        "; numeric tokens (compare against a number with " + string.Join(" ", Operators) + "): " +
        string.Join(", ", NumericTokens) +
        "; a leading ! negates. Tokens are case-sensitive.";

    /// <summary>Returns null when the expression is one the evaluator really understands, otherwise
    /// the reason it does not — phrased to slot into "workflow X step Y runIf 'Z' &lt;reason&gt;".</summary>
    public static string? Validate(string? expr)
    {
        var e = (expr ?? "").Trim();
        if (e.Length == 0)
            return "is empty — remove the field or give it a condition";

        // Negation, exactly as the evaluator recurses — including for junk like "!= 5", which the
        // evaluator also strips before failing to match anything.
        if (e.StartsWith('!'))
            return Validate(e[1..]);

        foreach (var op in Operators)
        {
            var idx = e.IndexOf(op, StringComparison.Ordinal);
            if (idx < 0) continue;

            var left = e[..idx].Trim();
            var right = e[(idx + op.Length)..].Trim();

            if (!NumericTokens.Contains(left, StringComparer.Ordinal))
                return $"compares '{left}', which is not a numeric token";
            if (!double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return $"compares against '{right}', which is not a number";
            return null;
        }

        return BooleanTokens.Contains(e, StringComparer.Ordinal)
            ? null
            // Deliberately not "would evaluate to TRUE": measured, the permissive default makes a
            // BARE unknown token constant-true (the step always runs) and a NEGATED one constant-
            // false (the step never runs). What both share is that the condition stops depending on
            // the run at all — say that, rather than a sign that is right half the time.
            : "is not a known token — at runtime it would ignore the run entirely and always give the same answer";
    }
}
