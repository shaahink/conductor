namespace Conductor.Core.Integrations.Messaging;

/// <summary>K5.4 — Telegram rejects a <c>sendMessage</c> whose text exceeds 4096 characters with
/// HTTP 400 and sends NOTHING. Until this existed the engine's only defence was to clip the result
/// summary to 900 characters and hope the rest of the message stayed small; a long gates line, a
/// long landed line and a long evidence list together could still cross the limit, and the whole
/// push would vanish with a warning in the run log.
/// <para>Splitting naively is worse than not splitting: <c>parse_mode=HTML</c> means a cut inside
/// <c>&lt;b&gt;</c> or inside <c>&amp;amp;</c> makes Telegram reject the chunk, and a cut BETWEEN an
/// open tag and its close makes it reject the second one. So a cut is only taken where the tag depth
/// is zero and the scanner is inside neither a tag nor an entity.</para></summary>
public static class HtmlChunker
{
    /// <summary>Telegram's documented limit for <c>sendMessage</c> text.</summary>
    public const int TelegramMaxChars = 4096;

    /// <summary>Telegram's documented limit for a <c>sendPhoto</c>/<c>sendDocument</c> caption — a
    /// quarter of the message limit, which is why an evidence caption is composed short rather than
    /// clipped from a message body.</summary>
    public const int TelegramMaxCaptionChars = 1024;

    /// <summary>Splits <paramref name="text"/> into chunks no longer than <paramref name="max"/>,
    /// each of which is independently valid HTML for Telegram's parser. Returns a single-element list
    /// unchanged when the text already fits, which is the overwhelmingly common case.</summary>
    public static IReadOnlyList<string> Split(string text, int max = TelegramMaxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
        if (text.Length <= max) return new[] { text };

        var chunks = new List<string>();
        var rest = text.AsSpan();
        while (rest.Length > max)
        {
            var cut = FindCut(rest, max);
            chunks.Add(rest[..cut].ToString().TrimEnd());
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0) chunks.Add(rest.ToString());
        return chunks;
    }

    /// <summary>The best index in <c>(0, max]</c> to cut at: the last line break that is safe, else
    /// the last word break that is safe, else the last safe index at all. A run of text with no safe
    /// index — a single tag longer than the limit, which our own composition cannot produce — falls
    /// back to a hard cut, because emitting an over-length chunk is a guaranteed 400.</summary>
    private static int FindCut(ReadOnlySpan<char> s, int max)
    {
        int lastNewline = -1, lastSpace = -1, lastSafe = -1;
        var depth = 0;

        for (var i = 0; i <= max; i++)
        {
            if (depth == 0 && !InsideMarkup(s, i))
            {
                lastSafe = i;
                if (i > 0 && s[i - 1] == '\n') lastNewline = i;
                else if (i > 0 && s[i - 1] == ' ') lastSpace = i;
            }
            if (i == max) break;
            if (s[i] == '<') depth += TagDelta(s, i);
        }

        if (lastNewline > 0) return lastNewline;
        if (lastSpace > 0) return lastSpace;
        return lastSafe > 0 ? lastSafe : max;
    }

    /// <summary>+1 for an opening tag, -1 for a closing one, 0 for anything else — including a bare
    /// <c>&lt;</c> that never closes, which HTML-escaped text cannot contain but a caller passing raw
    /// text can.</summary>
    private static int TagDelta(ReadOnlySpan<char> s, int i)
    {
        var end = s[i..].IndexOf('>');
        if (end < 0) return 0;
        return s[i + 1] == '/' ? -1 : 1;
    }

    /// <summary>Whether index <paramref name="i"/> falls inside a <c>&lt;…&gt;</c> tag or inside an
    /// <c>&amp;…;</c> entity — the two places a cut silently corrupts the markup rather than
    /// unbalancing it.</summary>
    private static bool InsideMarkup(ReadOnlySpan<char> s, int i)
    {
        for (var j = i - 1; j >= 0 && i - j <= 12; j--)
        {
            if (s[j] == '>' || s[j] == ';') return false;
            if (s[j] == '<') return true;
            if (s[j] == '&') return true;
        }
        return false;
    }
}
