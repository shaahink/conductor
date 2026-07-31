using System.Text.RegularExpressions;

namespace Conductor.Planning;

/// <summary>
/// The single definition of what a <c>{placeholder}</c> is, and of the <c>{{word}}</c> escape that
/// means "a literal brace, not a placeholder".
/// </summary>
/// <remarks>
/// SC3.3. This lives beside <see cref="ConditionVocabulary"/> and for the same reason: two parts of
/// the engine have to agree about a token, so exactly one of them may define it. The plan validator
/// (<c>PlanConfig.CollectErrors</c>) refuses authored prose the renderer could not resolve, and the
/// renderer (<c>PromptBuilder</c>/<c>PromptValidator</c>) refuses a template that still carries one
/// after substitution. They disagreed by construction before this type existed, which is how a
/// literal brace in one stage's <c>notes</c> passed <c>doctor</c> and then killed a 13-hour run at
/// the stage boundary with the refusal on stderr only.
/// <para>The rules, stated once:</para>
/// <list type="bullet">
/// <item>In a TEMPLATE, <c>{name}</c> is a placeholder and <c>{{name}}</c> is a literal brace.</item>
/// <item>In a VALUE substituted into a template — stage notes, a tracker handoff, gate output, the
/// tail of an agent's own transcript — every brace is prose. Values are data: nothing recursively
/// expands them, so a brace inside one can never be a placeholder and must never be fatal.</item>
/// </list>
/// Both are implemented by rewriting the protected spans to private-use sentinels before
/// substitution and restoring them afterwards, so a protected brace is invisible to both the
/// substitution pass and the leftover scan, and survives to the agent exactly as written.
/// </remarks>
public static partial class PromptPlaceholders
{
    // U+E000/U+E001 are Unicode private-use: they carry no meaning of their own and cannot occur in
    // prose that means anything. Any that arrive in untrusted input are stripped before protection
    // so nothing can smuggle a brace past the scan.
    private const char OpenSentinel = '\uE000';
    private const char CloseSentinel = '\uE001';

    /// <summary>A placeholder is <c>{name}</c> / <c>{name.with.dots}</c> — deliberately NOT matching
    /// <c>{"json": ...}</c> or <c>{}</c>, both of which legitimately appear in prompt bodies (the
    /// verifier is asked to emit a JSON object).</summary>
    [GeneratedRegex(@"\{[a-zA-Z][a-zA-Z0-9_.]*\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenRegex();

    /// <summary>The escape: <c>{{word}}</c> is prose that wants a brace, not an unbound name.</summary>
    [GeneratedRegex(@"\{\{(?<name>[a-zA-Z][a-zA-Z0-9_.]*)\}\}", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EscapeRegex();

    /// <summary>Template-side protection: hide every <c>{{word}}</c> escape so substitution and the
    /// leftover scan both pass over it, and it restores to a literal <c>{word}</c>.</summary>
    public static string ProtectEscapes(string text)
        => EscapeRegex().Replace(StripSentinels(text), m => OpenSentinel + m.Groups["name"].Value + CloseSentinel);

    /// <summary>Value-side protection: in data, an escaped and an unescaped brace token mean the same
    /// thing — the text as written. Both are held and restored verbatim.</summary>
    public static string ProtectValue(string value)
        => TokenRegex().Replace(ProtectEscapes(value), m => OpenSentinel + m.Value[1..^1] + CloseSentinel);

    /// <summary>Turns protected spans back into the literal braces they stand for. Runs last, after
    /// the leftover scan, so a restored brace is never mistaken for an unresolved placeholder.</summary>
    public static string Restore(string text)
        => text.Replace(OpenSentinel, '{').Replace(CloseSentinel, '}');

    /// <summary>Distinct placeholder tokens still present in <paramref name="text"/>, in the order
    /// they appear. Anything protected is already invisible here.</summary>
    public static IReadOnlyList<string> Tokens(string text)
        => TokenRegex().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Placeholder tokens in AUTHORED PROSE (a stage's <c>notes</c>, <c>promptExtra</c>) that
    /// nothing will ever resolve. Prose is substituted as a value, so a name that looks like a
    /// variable is not one — it reaches the agent verbatim as a broken instruction. Escapes are
    /// honoured, so deliberate prose braces are not findings.</summary>
    public static IReadOnlyList<string> UnresolvableIn(string? prose)
        => string.IsNullOrEmpty(prose) ? [] : Tokens(ProtectEscapes(prose));

    /// <summary>The escaped form of a token, for a refusal that shows the author the way out:
    /// <c>{model}</c> → <c>{{model}}</c>.</summary>
    public static string Escaped(string token) => "{" + token + "}";

    private static string StripSentinels(string text)
        => text.Contains(OpenSentinel) || text.Contains(CloseSentinel)
            ? text.Replace(OpenSentinel.ToString(), "", StringComparison.Ordinal)
                  .Replace(CloseSentinel.ToString(), "", StringComparison.Ordinal)
            : text;
}
