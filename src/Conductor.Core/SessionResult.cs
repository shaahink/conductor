using System.Text;

namespace Conductor.Core;

/// <summary>
/// K5.1 — the one format conductor owns for what a session reports back, and the one parser every
/// consumer reads it through.
/// <para>Until this type existed the SESSION-RESULT paragraph was stored once and then mutilated four
/// separate ways: the session record cut it at 700 characters, Telegram cut the already-cut copy at
/// 700 again, the advisor prompt took 1200, <c>RecentFailureBattery</c> pasted 600 bytes of it into
/// the next prompt and <c>REPORT.md</c> blockquoted whatever was left. Every one of those cuts landed
/// mid-word, because none of them knew where a field ended — there were no fields.</para>
/// <para>The format is deliberately small, because the engine cannot make an agent obey a format,
/// only prefer one:</para>
/// <code>
/// SESSION-RESULT: headline of at most fifteen words
/// - outcome bullet (at most three)
/// artefacts: path or link, path or link
/// evidence: .conductor/evidence/K5/thing.md
/// gaps: what is still open, or "none"
/// </code>
/// <para>Prose belongs in the handover, not here.</para>
/// <para><b>Degrading is a feature.</b> A legacy one-paragraph result, a malformed one, a verifier's
/// JSON verdict, or an empty string all parse without throwing into
/// <see cref="IsStructured"/> = <c>false</c>, and every renderer then reproduces exactly what that
/// consumer did before this type existed. Nothing here can make an old result worse.</para>
/// </summary>
public sealed class SessionResult
{
    /// <summary>The marker an agent prints to open its result. Matched case-insensitively.</summary>
    public const string Marker = "SESSION-RESULT:";

    /// <summary>A headline longer than this many words is a paragraph; the surplus is dropped.</summary>
    public const int MaxHeadlineWords = 15;

    /// <summary>A headline is also character-capped, so one 4 KB "word" cannot become the headline.</summary>
    public const int MaxHeadlineChars = 160;

    /// <summary>At most three outcome bullets survive; the rest are counted, not printed.</summary>
    public const int MaxOutcomes = 3;

    /// <summary>Per-bullet, per-artefact and per-evidence character cap.</summary>
    public const int MaxItemChars = 240;

    /// <summary>Cap on the gaps field, which is the one place a sentence is allowed.</summary>
    public const int MaxGapsChars = 400;

    /// <summary>At most this many artefact and evidence entries each.</summary>
    public const int MaxListItems = 8;

    /// <summary>Hard ceiling on the canonical rendering that gets stored on the session record.</summary>
    public const int MaxCanonicalChars = 2400;

    /// <summary>What an unstructured result is cut at — the pre-K5.1 behaviour, unchanged.</summary>
    public const int LegacyMaxChars = 700;

    private SessionResult(
        string headline,
        IReadOnlyList<string> outcomes,
        int outcomeOverflow,
        IReadOnlyList<string> artefacts,
        IReadOnlyList<string> evidence,
        string gaps,
        bool isStructured,
        bool hasMarker,
        string raw)
    {
        Headline = headline;
        Outcomes = outcomes;
        OutcomeOverflow = outcomeOverflow;
        Artefacts = artefacts;
        Evidence = evidence;
        Gaps = gaps;
        IsStructured = isStructured;
        HasMarker = hasMarker;
        Raw = raw;
    }

    /// <summary>The headline, clipped to <see cref="MaxHeadlineWords"/> words. For a legacy result
    /// this is the first sentence, so even unstructured text has something short to show.</summary>
    public string Headline { get; }

    /// <summary>At most <see cref="MaxOutcomes"/> outcome bullets, marker stripped.</summary>
    public IReadOnlyList<string> Outcomes { get; }

    /// <summary>How many bullets were dropped for exceeding <see cref="MaxOutcomes"/>.</summary>
    public int OutcomeOverflow { get; }

    /// <summary>Changed artefacts — paths, commits, links — as the agent listed them.</summary>
    public IReadOnlyList<string> Artefacts { get; }

    /// <summary>Evidence paths the agent claimed.</summary>
    public IReadOnlyList<string> Evidence { get; }

    /// <summary>What is explicitly NOT done. Empty when the agent said nothing.</summary>
    public string Gaps { get; }

    /// <summary>True when the text carried at least one bullet or labelled field under a headline.
    /// False for legacy prose, verifier JSON, and anything malformed — every renderer falls back to
    /// the pre-K5.1 behaviour for those.</summary>
    public bool IsStructured { get; }

    /// <summary>True when <see cref="Marker"/> appeared at all.</summary>
    public bool HasMarker { get; }

    /// <summary>The result body as the agent wrote it (from the marker onward when there is one),
    /// trimmed. This is what the legacy renderers cut, exactly as they always did.</summary>
    public string Raw { get; }

    /// <summary>An empty result — no marker, no text.</summary>
    public static SessionResult Empty { get; } = new("", [], 0, [], [], "", false, false, "");

    /// <summary>
    /// Parse whatever the agent printed. Total: null, blank, prose, JSON and half-written formats all
    /// return a value, never an exception.
    /// </summary>
    public static SessionResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var idx = normalized.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        var hasMarker = idx >= 0;
        var raw = (hasMarker ? normalized[idx..] : normalized).Trim();
        var body = hasMarker ? raw[Marker.Length..] : raw;

        var headline = "";
        var outcomes = new List<string>();
        var overflow = 0;
        var artefacts = new List<string>();
        var evidence = new List<string>();
        var gaps = "";
        var sawField = false;

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (TryBullet(line, out var bullet))
            {
                sawField = true;
                if (bullet.Length == 0) continue;
                if (outcomes.Count < MaxOutcomes) outcomes.Add(Clip(bullet, MaxItemChars));
                else overflow++;
                continue;
            }

            if (TryLabel(line, out var label, out var value))
            {
                sawField = true;
                switch (label)
                {
                    case "artefacts":
                        AddList(artefacts, value);
                        break;
                    case "evidence":
                        AddList(evidence, value);
                        break;
                    case "gaps":
                        if (gaps.Length == 0) gaps = Clip(value, MaxGapsChars);
                        break;
                }
                continue;
            }

            // The first ordinary line is the headline; later ones are prose, which belongs in the
            // handover. They stay in Raw and are dropped from the structured view on purpose.
            if (headline.Length == 0) headline = ClipWords(line, MaxHeadlineWords, MaxHeadlineChars);
        }

        // The marker is required for the structured reading: a narrative that happens to contain a
        // markdown list is prose, and prose degrades to the pre-K5.1 cut rather than being filleted.
        var structured = hasMarker && sawField && headline.Length > 0;
        if (!structured)
            headline = ClipWords(FirstSentence(body), MaxHeadlineWords, MaxHeadlineChars);

        return new SessionResult(headline, outcomes, overflow, artefacts, evidence, gaps,
            structured, hasMarker, raw);
    }

    /// <summary>
    /// The canonical rendering that gets stored on the session record: the same fields, normalized,
    /// each one clipped on its own so no single field can eat the record. Starts with
    /// <see cref="Marker"/>, so every downstream marker search still finds it.
    /// </summary>
    public string ToCanonical()
    {
        if (!IsStructured) return Clip(Raw, LegacyMaxChars);

        var sb = new StringBuilder();
        sb.Append(Marker).Append(' ').Append(Headline);
        foreach (var o in Outcomes) sb.Append("\n- ").Append(o);
        if (OutcomeOverflow > 0) sb.Append("\n- (+").Append(OutcomeOverflow).Append(" more outcomes not shown)");
        if (Artefacts.Count > 0) sb.Append("\nartefacts: ").Append(string.Join(", ", Artefacts));
        if (Evidence.Count > 0) sb.Append("\nevidence: ").Append(string.Join(", ", Evidence));
        if (Gaps.Length > 0) sb.Append("\ngaps: ").Append(Gaps);
        return Clip(sb.ToString(), MaxCanonicalChars);
    }

    /// <summary>
    /// A rendering bounded by <paramref name="maxChars"/> that drops whole fields rather than cutting
    /// one mid-word — for Telegram and the advisor prompt, which used to blind-cut at 700 and 1200.
    /// A legacy result is cut exactly where it always was, because there is nothing to drop.
    /// </summary>
    public string ToCompact(int maxChars)
    {
        if (maxChars <= 0) return "";
        if (!IsStructured) return Clip(Raw, maxChars);

        var lines = new List<string> { Headline };
        foreach (var o in Outcomes) lines.Add("• " + o);
        if (Gaps.Length > 0) lines.Add("gaps: " + Gaps);
        if (Evidence.Count > 0) lines.Add("evidence: " + string.Join(", ", Evidence));
        if (Artefacts.Count > 0) lines.Add("artefacts: " + string.Join(", ", Artefacts));

        var sb = new StringBuilder();
        var dropped = false;
        foreach (var line in lines)
        {
            var addition = sb.Length == 0 ? line : "\n" + line;
            if (sb.Length + addition.Length > maxChars) { dropped = true; continue; }
            sb.Append(addition);
        }

        // Even the headline alone can overrun a very small budget; that one cut is unavoidable.
        if (sb.Length == 0) return Clip(Headline, maxChars);
        if (dropped && sb.Length + 1 <= maxChars) sb.Append('…');
        return sb.ToString();
    }

    /// <summary>
    /// The markdown block <c>REPORT.md</c> shows: a blockquoted headline with the fields as a list,
    /// instead of one blockquote containing a whole paragraph.
    /// </summary>
    public string ToMarkdown()
    {
        if (!IsStructured) return "> " + Raw.Replace("\n", "\n> ", StringComparison.Ordinal);

        var sb = new StringBuilder();
        sb.Append("> **").Append(Headline).Append("**");
        foreach (var o in Outcomes) sb.Append("\n> - ").Append(o);
        if (OutcomeOverflow > 0) sb.Append("\n> - (+").Append(OutcomeOverflow).Append(" more outcomes not shown)");
        if (Artefacts.Count > 0) sb.Append("\n>\n> artefacts: ").Append(string.Join(", ", Artefacts));
        if (Evidence.Count > 0) sb.Append("\n>\n> evidence: ").Append(string.Join(", ", Evidence));
        if (Gaps.Length > 0) sb.Append("\n>\n> gaps: ").Append(Gaps);
        return sb.ToString();
    }

    /// <summary>
    /// What the lessons ledger gets fed. A structured result hands over the parts that can carry a
    /// rule — the bullets and the gaps — and drops the headline, which is status by construction. A
    /// legacy result hands over the whole body, which is what <c>ReflectionStep</c> always passed.
    /// </summary>
    public string ForLessons()
    {
        if (!IsStructured) return Raw;

        var sb = new StringBuilder();
        foreach (var o in Outcomes) sb.Append(o).Append('\n');
        if (Gaps.Length > 0) sb.Append(Gaps).Append('\n');
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- internals

    private static bool TryBullet(string line, out string text)
    {
        text = "";
        if (line.Length < 2) return false;
        var c = line[0];
        if (c is not ('-' or '*' or '•')) return false;
        if (!char.IsWhiteSpace(line[1])) return false;
        text = line[2..].Trim();
        return true;
    }

    private static bool TryLabel(string line, out string label, out string value)
    {
        label = "";
        value = "";
        var colon = line.IndexOf(':');
        if (colon <= 0) return false;

        var head = line[..colon].Trim().ToLowerInvariant();
        label = head switch
        {
            "artefacts" or "artifacts" or "artefact" or "artifact" or "changed" => "artefacts",
            "evidence" => "evidence",
            "gaps" or "gap" or "open" => "gaps",
            _ => "",
        };
        if (label.Length == 0) return false;

        value = line[(colon + 1)..].Trim();
        return true;
    }

    private static void AddList(List<string> into, string value)
    {
        if (value.Length == 0) return;
        foreach (var part in value.Split(','))
        {
            var item = part.Trim();
            if (item.Length == 0) continue;
            if (into.Count >= MaxListItems) return;
            into.Add(Clip(item, MaxItemChars));
        }
    }

    private static string FirstSentence(string body)
    {
        var text = body.Trim();
        if (text.Length == 0) return "";
        var end = text.IndexOfAny(['.', '\n']);
        return end > 0 ? text[..end].Trim() : text;
    }

    private static string ClipWords(string s, int maxWords, int maxChars)
    {
        var text = s.Trim();
        if (text.Length == 0) return "";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = words.Length <= maxWords
            ? text
            : string.Join(" ", words.Take(maxWords)) + "…";
        return Clip(kept, maxChars);
    }

    private static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
