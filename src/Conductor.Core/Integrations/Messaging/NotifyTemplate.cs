using System.Text;
using Conductor.Planning;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>K5.4 — the shape of a push is the owner's to change. Every message this engine sends was
/// a <c>StringBuilder</c> in a method: to reorder a line, drop the gates, or put the cost first, an
/// owner had to edit C# and rebuild the engine that is driving their run.
/// <para><c>plan.templatesDir</c> already exists for the agent prompts, so this is the same idea in
/// the same place: drop <c>&lt;templatesDir&gt;/notify/&lt;event&gt;.md</c> and it is used instead of
/// the built-in. The prompt loader resolves templates by NAME (<c>session.md</c>, <c>fix.md</c>,
/// <c>packs/*.md</c>), so a file under <c>notify/</c> is invisible to it and cannot be mistaken for a
/// prompt — which matters, because an unresolved placeholder in a PROMPT template is fatal by design
/// and one here must never be.</para>
/// <para>Two rules, and they are the whole language. A placeholder is <c>{name}</c>, in the same
/// grammar the prompts use (<see cref="PromptPlaceholders"/>), so <c>{{name}}</c> is a literal brace.
/// And <b>a line that is blank after substitution is dropped</b> — which is why every optional fact
/// sits alone on its own line: no orphaned "gates:" label with nothing after it.</para></summary>
public static class NotifyTemplate
{
    /// <summary>Where an override for <paramref name="eventName"/> would live, under the plan's
    /// templates directory. Null when the plan sets no templates directory at all.</summary>
    public static string? OverridePath(string? planDir, string? templatesDir, string eventName) =>
        string.IsNullOrWhiteSpace(templatesDir) || string.IsNullOrWhiteSpace(planDir)
            ? null
            : Path.Combine(planDir, templatesDir, "notify", eventName + ".md");

    /// <summary>Renders <paramref name="eventName"/> from the owner's override when there is a usable
    /// one, else from <paramref name="builtIn"/>.
    /// <para>"Usable" is checked, not assumed: an override naming a fact this event does not have
    /// would render a message with a literal <c>{whatever}</c> in it, so it is REFUSED and the
    /// built-in is used instead. The refusal goes to <paramref name="log"/> — a notification path
    /// that throws takes the run's only voice with it, which is the opposite of the point.</para></summary>
    public static async Task<string> RenderAsync(string eventName, string builtIn,
        IReadOnlyDictionary<string, string> facts, string? planDir, string? templatesDir,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var template = builtIn;
        var path = OverridePath(planDir, templatesDir, eventName);
        if (path != null && await TryReadOverrideAsync(path, facts, log).ConfigureAwait(false) is { } owned)
            template = owned;

        return DropBlankLines(Substitute(template, facts));
    }

    private static async Task<string?> TryReadOverrideAsync(string path,
        IReadOnlyDictionary<string, string> facts, Action<string>? log)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return null;
            text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"notify template {path} could not be read ({ex.Message}) — using the built-in");
            return null;
        }

        var protectedText = PromptPlaceholders.ProtectEscapes(text);
        var unknown = PromptPlaceholders.Tokens(protectedText)
            .Where(t => !facts.ContainsKey(t[1..^1]))
            .ToList();
        if (unknown.Count > 0)
        {
            log?.Invoke($"notify template {path} names {string.Join(", ", unknown)}, which this event "
                      + $"does not provide — using the built-in. Available: {string.Join(", ", facts.Keys.Order(StringComparer.Ordinal))}");
            return null;
        }
        return protectedText;
    }

    private static string Substitute(string template, IReadOnlyDictionary<string, string> facts)
    {
        var sb = new StringBuilder(PromptPlaceholders.ProtectEscapes(template));
        foreach (var (name, value) in facts)
            sb.Replace("{" + name + "}", PromptPlaceholders.ProtectValue(value ?? ""));
        return PromptPlaceholders.Restore(sb.ToString());
    }

    /// <summary>The second rule. An optional fact on its own line disappears with the line; a fact
    /// that shares a line with a label keeps the label, which is what makes <c>gates: {gates}</c> and
    /// <c>{result}</c> behave differently on purpose.</summary>
    private static string DropBlankLines(string rendered)
    {
        var kept = rendered.Split('\n').Where(l => l.Trim().Length > 0);
        return string.Join("\n", kept).TrimEnd();
    }
}
