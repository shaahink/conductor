namespace Conductor.Core;

/// <summary>
/// Thrown when a composed prompt still carries a placeholder nothing resolved. Its own type, so the
/// run loop can park on exactly this and nothing else (SC3.3) — a broken prompt is an authoring
/// problem a human fixes, not a crash to take the engine down with.
/// </summary>
public sealed class PromptCompositionException : InvalidOperationException
{
    public PromptCompositionException() { }
    public PromptCompositionException(string message) : base(message) { }
    public PromptCompositionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Fails a prompt that still contains an unresolved <c>{placeholder}</c> after rendering.
/// </summary>
/// <remarks>
/// A silent miss ships broken instructions to the agent and nobody notices. The live proof: the verifier
/// template contained <c>{plan.VerifierThreshold}</c>, which was never a template variable, so every
/// verifier was told its bar was literally "≥{plan.VerifierThreshold}". Prompts are code; an unbound name
/// is a compile error, not a formatting quirk.
/// <para>SC3.3: what counts as a placeholder — and the <c>{{word}}</c> escape that says "a literal
/// brace" — is defined once in <see cref="Conductor.Planning.PromptPlaceholders"/>, because the plan
/// validator has to refuse at authoring time exactly what this refuses at render time.</para>
/// </remarks>
public static class PromptValidator
{
    public static void ThrowIfUnresolved(string rendered, string templateName)
    {
        var leftovers = PromptPlaceholders.Tokens(rendered);
        if (leftovers.Count == 0) return;

        throw new PromptCompositionException(
            $"Template '{templateName}' has unresolved placeholder(s): {string.Join(", ", leftovers)}. " +
            "Either the name is misspelled or the variable is not supplied by PromptBuilder.Vars(). " +
            "An unresolved placeholder would be sent to the agent verbatim — " +
            $"write {PromptPlaceholders.Escaped(leftovers[0])} (doubled braces) if the prose means a literal brace.");
    }
}
