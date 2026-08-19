namespace Conductor.Core;

/// <summary>
/// Finding the model's JSON in the model's prose. Shared because two parsers now need it and the
/// second one must not learn the same lesson again: a single-level regex (<c>\{[^{}]*"score"[^{}]*\}</c>)
/// breaks on the braces a review routinely quotes — a <c>{model}</c> placeholder inside a finding
/// string used to lose the whole verdict.
/// </summary>
internal static class JsonScan
{
    /// <summary>Every complete, balanced top-level <c>{...}</c> substring, tracking brace depth and
    /// string-literal state (with escapes) so braces inside quoted text never throw off the match.</summary>
    public static IEnumerable<string> BalancedObjects(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var results = new List<string>();
        var depth = 0;
        var start = -1;
        var inString = false;
        var escape = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{':
                    if (depth == 0) start = i;
                    depth++;
                    break;
                case '}':
                    if (depth > 0)
                    {
                        depth--;
                        if (depth == 0 && start >= 0)
                        {
                            results.Add(text[start..(i + 1)]);
                            start = -1;
                        }
                    }
                    break;
            }
        }
        return results;
    }
}
