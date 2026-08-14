using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conductor.Core.Planning;

/// <summary>The mechanics under <see cref="PlanDocumentEditor"/>: a span-aware parse of the raw
/// file (comments kept as bytes we never touch), and the splicing of computed edits back into it.</summary>
public static partial class PlanDocumentEditor
{
    /// <summary>A node of the original document with its exact byte extent. Values carry no parsed
    /// content — semantics live in the model diff; this tree only answers "where".</summary>
    private sealed class SpanNode
    {
        public int Start;
        public int End; // exclusive
        public List<(string Name, int NameStart, SpanNode Value)>? Props; // object
        public List<SpanNode>? Items;                                     // array
    }

    private readonly record struct Edit(int Start, int End, byte[] Text, bool IsDeletion = false);

    private static SpanNode ParseSpans(byte[] utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Allow,
            AllowTrailingCommas = true,
        });
        if (!ReadSkippingComments(ref reader)) throw new JsonException("empty document");
        return ParseValue(ref reader);
    }

    private static bool ReadSkippingComments(ref Utf8JsonReader r)
    {
        while (r.Read())
        {
            if (r.TokenType != JsonTokenType.Comment) return true;
        }
        return false;
    }

    private static SpanNode ParseValue(ref Utf8JsonReader r)
    {
        var start = (int)r.TokenStartIndex;
        switch (r.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var props = new List<(string, int, SpanNode)>();
                while (true)
                {
                    if (!ReadSkippingComments(ref r)) throw new JsonException("unterminated object");
                    if (r.TokenType == JsonTokenType.EndObject)
                        return new SpanNode { Start = start, End = (int)r.TokenStartIndex + 1, Props = props };
                    var name = r.GetString() ?? "";
                    var nameStart = (int)r.TokenStartIndex;
                    if (!ReadSkippingComments(ref r)) throw new JsonException("property without a value");
                    props.Add((name, nameStart, ParseValue(ref r)));
                }
            }
            case JsonTokenType.StartArray:
            {
                var items = new List<SpanNode>();
                while (true)
                {
                    if (!ReadSkippingComments(ref r)) throw new JsonException("unterminated array");
                    if (r.TokenType == JsonTokenType.EndArray)
                        return new SpanNode { Start = start, End = (int)r.TokenStartIndex + 1, Items = items };
                    items.Add(ParseValue(ref r));
                }
            }
            default:
            {
                // ValueSpan is the raw token (for strings: the bytes between the quotes, escapes
                // unprocessed), so the extent is arithmetic, not a search.
                var len = r.HasValueSequence ? (int)r.ValueSequence.Length : r.ValueSpan.Length;
                var end = r.TokenType == JsonTokenType.String ? start + len + 2 : start + len;
                return new SpanNode { Start = start, End = end };
            }
        }
    }

    // ---------------------------------------------------------------- edit construction

    /// <summary>All missing properties of one object as ONE insertion after its last property — one
    /// anchor, one comma discipline, no way for two separate inserts to fight over the same comma.</summary>
    private static void InsertProps(SpanNode obj, List<(string Key, JsonNode? Value)> adds, byte[] utf8, string nl, List<Edit> edits)
    {
        string indent;
        int at;
        bool needLeadingComma;
        string? closer = null;
        if (obj.Props!.Count > 0)
        {
            var last = obj.Props[^1];
            indent = LineIndent(utf8, last.NameStart);
            var probe = SkipSpaces(utf8, last.Value.End);
            var trailingComma = probe < utf8.Length && utf8[probe] == (byte)',';
            at = trailingComma ? probe + 1 : last.Value.End;
            needLeadingComma = !trailingComma;
        }
        else
        {
            var outer = LineIndent(utf8, obj.Start);
            indent = outer + "  ";
            at = obj.Start + 1;
            needLeadingComma = false;
            closer = nl + outer;
        }

        var body = string.Join("," + nl + indent,
            adds.Select(a => JsonSerializer.Serialize(a.Key) + ": " + RenderText(a.Value, indent, nl)));
        var text = (needLeadingComma ? "," : "") + nl + indent + body + (closer ?? "");
        edits.Add(new Edit(at, at, Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>All appended items of one array as ONE insertion after its last item.</summary>
    private static void AppendItems(SpanNode arr, List<JsonNode?> items, byte[] utf8, string nl, List<Edit> edits)
    {
        string indent;
        int at;
        bool needLeadingComma;
        string? closer = null;
        if (arr.Items!.Count > 0)
        {
            var last = arr.Items[^1];
            indent = LineIndent(utf8, last.Start);
            var probe = SkipSpaces(utf8, last.End);
            var trailingComma = probe < utf8.Length && utf8[probe] == (byte)',';
            at = trailingComma ? probe + 1 : last.End;
            needLeadingComma = !trailingComma;
        }
        else
        {
            var outer = LineIndent(utf8, arr.Start);
            indent = outer + "  ";
            at = arr.Start + 1;
            needLeadingComma = false;
            closer = nl + outer;
        }

        var body = string.Join("," + nl + indent, items.Select(i => RenderText(i, indent, nl)));
        var text = (needLeadingComma ? "," : "") + nl + indent + body + (closer ?? "");
        edits.Add(new Edit(at, at, Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The deletion range for one member (an object property from its name, or an array
    /// item), extended over the separators that would otherwise dangle: its own blank line when it
    /// stood alone on one, its trailing comma, or — for a last member — the comma before it.</summary>
    private static Edit DeleteMember(byte[] utf8, int start, int end)
    {
        var lineStart = start;
        while (lineStart > 0 && utf8[lineStart - 1] != (byte)'\n' && IsSpace(utf8[lineStart - 1])) lineStart--;
        var ownsLine = lineStart == 0 || utf8[lineStart - 1] == (byte)'\n';

        var probe = SkipSpaces(utf8, end);
        if (probe < utf8.Length && utf8[probe] == (byte)',')
        {
            var e = probe + 1;
            if (ownsLine)
            {
                var eol = SkipSpaces(utf8, e);
                if (eol < utf8.Length && utf8[eol] == (byte)'\r') eol++;
                if (eol < utf8.Length && utf8[eol] == (byte)'\n')
                    return new Edit(lineStart, eol + 1, [], IsDeletion: true);
            }
            return new Edit(start, e, [], IsDeletion: true);
        }

        // Last member: take the comma that precedes it, wherever whitespace put it.
        var back = start - 1;
        while (back >= 0 && (IsSpace(utf8[back]) || utf8[back] is (byte)'\r' or (byte)'\n')) back--;
        if (back >= 0 && utf8[back] == (byte)',')
            return new Edit(back, end, [], IsDeletion: true);

        // Only member.
        return new Edit(ownsLine ? lineStart : start, end, [], IsDeletion: true);
    }

    // ---------------------------------------------------------------- splicing

    private static byte[] Splice(byte[] utf8, List<Edit> edits)
    {
        if (edits.Count == 0) return utf8;
        edits.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

        var merged = new List<Edit>(edits.Count);
        foreach (var e in edits)
        {
            if (merged.Count > 0 && e.Start < merged[^1].End)
            {
                var prev = merged[^1];
                if (prev.IsDeletion && e.IsDeletion)
                {
                    merged[^1] = prev with { End = Math.Max(prev.End, e.End) };
                    continue;
                }
                throw new InvalidOperationException("overlapping plan edits");
            }
            merged.Add(e);
        }

        var length = utf8.Length;
        foreach (var e in merged) length += e.Text.Length - (e.End - e.Start);
        var result = new byte[length];
        var pos = 0;
        var written = 0;
        foreach (var e in merged)
        {
            Array.Copy(utf8, pos, result, written, e.Start - pos);
            written += e.Start - pos;
            Array.Copy(e.Text, 0, result, written, e.Text.Length);
            written += e.Text.Length;
            pos = e.End;
        }
        Array.Copy(utf8, pos, result, written, utf8.Length - pos);
        return result;
    }

    // ---------------------------------------------------------------- rendering

    private static readonly JsonSerializerOptions RenderOpts = new() { WriteIndented = true };

    private static byte[] Render(JsonNode? value, string indent, string nl) =>
        Encoding.UTF8.GetBytes(RenderText(value, indent, nl));

    /// <summary>A value as inserted text: the serializer's own indented rendering, re-based onto the
    /// destination's indentation and newline style. The first line carries no indent — it lands at
    /// the splice point's column.</summary>
    private static string RenderText(JsonNode? value, string indent, string nl)
    {
        if (value is null) return "null";
        var lines = value.ToJsonString(RenderOpts).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return lines.Length == 1
            ? lines[0]
            : string.Join(nl, lines.Select((l, i) => i == 0 ? l : indent + l));
    }

    private static string LineIndent(byte[] utf8, int pos)
    {
        var ls = pos;
        while (ls > 0 && utf8[ls - 1] != (byte)'\n') ls--;
        var e = ls;
        while (e < utf8.Length && IsSpace(utf8[e])) e++;
        return Encoding.UTF8.GetString(utf8, ls, e - ls);
    }

    private static int SkipSpaces(byte[] utf8, int pos)
    {
        while (pos < utf8.Length && IsSpace(utf8[pos])) pos++;
        return pos;
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t';
}
