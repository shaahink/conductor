using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Core.Inbox;

/// <summary>DV3.3 — one stretch of speech as the transcriber heard it, with how sure it was.
///
/// <para>The confidence is the point. A voice note is the one input to this system that arrives
/// already degraded: the owner said a word, a model guessed at it, and the guess is what an
/// autonomous agent will read three weeks later. Findings §1.6 asks for the doubt to be VISIBLE —
/// "so a reader (human or agent) can see which words not to trust" — which means it has to survive
/// into the stored note, not sit in a log line nobody opens.</para></summary>
/// <param name="StartSeconds">Offset into the audio. Kept so a doubtful phrase can be found again
/// in the file that is still on disk beside it.</param>
/// <param name="EndSeconds">Where it ends.</param>
/// <param name="Text">What the model heard, verbatim and untrimmed of its doubt.</param>
/// <param name="Confidence">0..1, or null when the command said nothing about it. NULL IS NOT
/// CONFIDENT: a command that reports no confidence gets no marks at all rather than marks that
/// would be invented here.</param>
public sealed record TranscriptSegment(
    double StartSeconds,
    double EndSeconds,
    string Text,
    double? Confidence = null);

/// <summary>DV3.3 — the result of running the configured local command over one audio file.
///
/// <para>Two texts, deliberately: <see cref="Text"/> is what was heard, and <see cref="Marked"/> is
/// what a reader is shown. They differ only where the transcriber was unsure, and they are both
/// kept — the sidecar file holds the segments and their numbers, so a reader who wants to argue
/// with a mark can.</para></summary>
/// <param name="Text">Every segment joined, in order.</param>
/// <param name="Segments">The segments, with their confidences where the command gave them.</param>
/// <param name="Language">What the command said it detected, or null.</param>
public sealed record Transcript(string Text, IReadOnlyList<TranscriptSegment> Segments, string? Language = null)
{
    /// <summary>Opens a stretch the transcriber was unsure of. ASCII and short: it goes into a
    /// prompt, into a terminal and into a text editor, and a marker that renders as a box in any of
    /// those is a marker that gets ignored.</summary>
    public const string DoubtOpen = "[?: ";

    /// <summary>Closes it.</summary>
    public const string DoubtClose = "]";

    /// <summary>Below this a segment is marked. Empirical, from the model this machine runs:
    /// faster-whisper's <c>avg_logprob</c> for clean speech sits around -0.2 to -0.5, which is
    /// exp() of roughly 0.6 to 0.8; a segment under 0.45 is one where the model was picking between
    /// candidates. Overridable per plan, because a different command's numbers mean different
    /// things.</summary>
    public const double DefaultConfidenceFloor = 0.45;

    /// <summary>Nothing heard — a file with no speech in it. Distinct from a FAILURE to transcribe,
    /// which is a sentence, not an empty transcript.</summary>
    public static Transcript Empty => new("", []);

    /// <summary>The mean confidence, weighted by how long each segment lasted, or null when the
    /// command reported none. Weighted rather than plain-averaged so one doubtful half-second
    /// cannot condemn a two-minute note, nor a run of confident "mm"s rescue it.</summary>
    public double? MeanConfidence
    {
        get
        {
            double weight = 0, sum = 0;
            foreach (var s in Segments)
            {
                if (s.Confidence is not { } c) continue;
                var w = Math.Max(0.001, s.EndSeconds - s.StartSeconds);
                sum += c * w;
                weight += w;
            }
            return weight > 0 ? sum / weight : null;
        }
    }

    /// <summary>How many segments fall below <paramref name="floor"/>.</summary>
    public int DoubtfulCount(double floor) =>
        Segments.Count(s => s.Confidence is { } c && c < floor);

    /// <summary>The text a reader is shown: every segment joined, with the doubtful ones wrapped.
    ///
    /// <para>Wrapped INLINE rather than footnoted, because the reader who needs the warning is the
    /// one reading the sentence — a list of doubtful timestamps at the bottom is a warning nobody
    /// reads at the moment it matters.</para></summary>
    public string Marked(double floor = DefaultConfidenceFloor)
    {
        if (Segments.Count == 0) return Text;

        var sb = new StringBuilder();
        foreach (var segment in Segments)
        {
            var text = segment.Text.Trim();
            if (text.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            if (segment.Confidence is { } c && c < floor)
                sb.Append(DoubtOpen).Append(text).Append(DoubtClose);
            else
                sb.Append(text);
        }
        return sb.ToString();
    }

    /// <summary>The sidecar written beside the audio: everything, including the numbers the marks
    /// were derived from. This is the recoverable half of findings §1.6 — the audio survives a
    /// garbled transcript, and the segment table survives a mark somebody disagrees with.</summary>
    public string ToSidecarJson(double floor) => JsonSerializer.Serialize(new
    {
        text = Text,
        marked = Marked(floor),
        language = Language,
        confidence = MeanConfidence,
        confidenceFloor = floor,
        doubtful = DoubtfulCount(floor),
        segments = Segments.Select(s => new
        {
            start = Math.Round(s.StartSeconds, 2),
            end = Math.Round(s.EndSeconds, 2),
            text = s.Text,
            confidence = s.Confidence,
        }),
    }, SidecarJson);

    private static readonly JsonSerializerOptions SidecarJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Parses what the command printed.
    ///
    /// <para>Two shapes are accepted and the fallback is the important one. JSON in the documented
    /// contract (<c>{"text":…,"segments":[{"start","end","text","confidence"}]}</c>) gives marks;
    /// ANYTHING ELSE is taken as a plain transcript with no confidence at all. That is not
    /// leniency — <c>whisper.cpp -otxt</c>, a shell one-liner and a hand-rolled wrapper all print
    /// bare text, and refusing them would make the config key a lie about being command-agnostic.
    /// A command whose numbers we cannot read produces a transcript with no marks, which is honest;
    /// inventing a confidence for it would not be.</para></summary>
    public static Transcript Parse(string stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return Empty;
        if (trimmed[0] is not ('{' or '[')) return Plain(trimmed);

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Plain(trimmed);

            var segments = ReadSegments(root);
            var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? ""
                : string.Join(" ", segments.Select(s => s.Text.Trim()).Where(s => s.Length > 0));
            var language = root.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString()
                : null;
            return new Transcript(text.Trim(), segments, language);
        }
        catch (JsonException)
        {
            return Plain(trimmed);
        }
    }

    private static Transcript Plain(string text) =>
        new(text, [new TranscriptSegment(0, 0, text)]);

    private static IReadOnlyList<TranscriptSegment> ReadSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<TranscriptSegment>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var text = item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? ""
                : "";
            if (text.Trim().Length == 0) continue;
            list.Add(new TranscriptSegment(
                Number(item, "start"), Number(item, "end"), text, Confidence(item)));
        }
        return list;
    }

    private static double Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : 0;

    /// <summary>The confidence, either given as one or derived from faster-whisper's own
    /// <c>avg_logprob</c> — which is what every wrapper around that library has to hand and none of
    /// them normalise the same way. exp() of a mean log-probability is a probability.</summary>
    private static double? Confidence(JsonElement item)
    {
        if (item.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
            return Math.Clamp(c.GetDouble(), 0, 1);
        if (item.TryGetProperty("avg_logprob", out var lp) && lp.ValueKind == JsonValueKind.Number)
            return Math.Clamp(Math.Exp(lp.GetDouble()), 0, 1);
        return null;
    }

    /// <summary>A one-line summary for a log or an acknowledgement: how sure, over how many
    /// segments. Invariant culture, because it is read by a person on any machine and pinned by a
    /// test on this one.</summary>
    public string ConfidenceLine(double floor)
    {
        if (MeanConfidence is not { } mean) return "no confidence reported";
        var doubtful = DoubtfulCount(floor);
        var pct = (mean * 100).ToString("0", CultureInfo.InvariantCulture);
        return doubtful == 0
            ? string.Create(CultureInfo.InvariantCulture, $"confidence {pct}%")
            : string.Create(CultureInfo.InvariantCulture,
                $"confidence {pct}%, {doubtful} unsure stretch(es) marked {DoubtOpen}…{DoubtClose}");
    }
}
