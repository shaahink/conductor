namespace Conductor.Core.Integrations.Messaging;

/// <summary>PK4.2 / D7 — the session's words for a checkpoint card: <c>"&lt;title&gt; | &lt;two to four
/// sentences&gt;"</c>, handed to <c>conductor task --done --tell</c> and posted by the engine when the
/// verdict confirms the claim.
///
/// <para>Refused at the claim, never trimmed at the verdict. A card is a photo caption when it carries
/// the before/after pair, and Telegram cuts a caption at 1024 characters — so the words get a budget
/// that leaves the bar, the counts and the footer room inside it. Cutting a sentence in half after the
/// session has gone is the failure <c>report.ps1</c> never had, because the author was still there to
/// see the refusal.</para></summary>
public static class CardWords
{
    /// <summary>The separator between the title and the line.</summary>
    public const char Separator = '|';

    /// <summary>Longest title, in characters.</summary>
    public const int MaxTitle = 120;

    /// <summary>Longest line (the sentences), in characters. With the title, the bar and a footer the
    /// card still fits one 1024-character caption.</summary>
    public const int MaxLine = 700;

    /// <summary>The title and the line, or null when the words do not have that shape.</summary>
    public static (string Title, string Line)? Parse(string? words)
    {
        if (string.IsNullOrWhiteSpace(words)) return null;
        var cut = words.IndexOf(Separator, StringComparison.Ordinal);
        if (cut < 0) return null;
        var title = words[..cut].Trim();
        var line = words[(cut + 1)..].Trim();
        return title.Length == 0 || line.Length == 0 ? null : (title, line);
    }

    /// <summary>Why these words cannot be a card, in one sentence, or null when they can.</summary>
    public static string? Refusal(string? words)
    {
        if (Parse(words) is not { } parsed)
            return "--tell needs \"<title> | <two to four sentences>\": a title, a '|', then the sentences";
        if (parsed.Title.Length > MaxTitle)
            return $"--tell's title is {parsed.Title.Length} characters; a card title is at most {MaxTitle}";
        if (parsed.Line.Length > MaxLine)
            return $"--tell's sentences are {parsed.Line.Length} characters; a card carries at most {MaxLine} "
                + "(it has to fit one photo caption with the bar and the footer)";
        return null;
    }
}
