using System.Text;
using System.Text.RegularExpressions;

namespace Conductor.Core;

/// <summary>
/// Keeps <c>.conductor/lessons.md</c>: a bounded, deduped list of one-line RULES extracted from what
/// sessions reported, newest first.
/// <para>K1.3 rewrote this. Until then it was a diary — the reflection step pasted the first 500
/// characters of each session's SESSION-RESULT under a dated heading, so the file was a set of
/// truncated near-duplicates of the handovers, and <c>LessonsBattery</c> pasted three of them into
/// every following prompt. That is cache-read rent on prose that teaches nothing: a status paragraph
/// naming commits and gate counts is worth nothing to the session that reads it next week.</para>
/// <para>It also repeated itself. <c>TrimToCap</c> re-parsed the content it had ALREADY prepended the
/// new entry to and then emitted that entry a second time, so any append that crossed the byte cap
/// duplicated itself — which is why the SF7-38 entry appears twice in this repo's own file. The
/// rules format has one writer and one cap, so the shape that produced it is gone.</para>
/// <para>What lands here now is only sentences that state a RULE (see <see cref="ExtractRules"/>).
/// A session that reported nothing rule-shaped contributes nothing, and the file stays empty rather
/// than filling with narrative — an empty battery is strictly better than a misleading one.</para>
/// <para>The file is rotating runtime state, not a record: a pre-K1.3 file contributes no rules and
/// is rewritten in the new format by the first append.</para>
/// </summary>
public sealed class LessonsManager
{
    private const string FileName = "lessons.md";
    private const string HeaderLine = "# Lessons (rules extracted from session results, newest first, deduped)";
    private const string UpdatedPrefix = "> **Last updated:** ";
    private const string RuleMarker = "- [";

    /// <summary>Sentences that state a rule, as opposed to a status. Deliberately narrow: the cost of
    /// missing one is an absent line, the cost of a false positive is prose in every later prompt.</summary>
    private static readonly Regex RuleCue = new(
        @"\b(never|always|must|cannot|can't|do not|don't|avoid|beware|gotcha|trap|lesson|rule|watch out|make sure|only ever)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <summary>At most this many rules from one session, so a verbose result cannot flood the file.</summary>
    private const int MaxRulesPerSession = 3;

    /// <summary>A rule longer than this is not a rule; it is cut on a word boundary.</summary>
    private const int MaxRuleChars = 200;

    private readonly string _dirPath;
    private readonly int _maxBytes;
    private readonly int _maxRules;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    public LessonsManager(string conductorDir, int maxBytes = 8192, TimeProvider? time = null, int maxRules = 20)
    {
        _dirPath = conductorDir;
        _maxBytes = Math.Max(1024, maxBytes);
        _maxRules = Math.Max(1, maxRules);
        _time = time ?? TimeProvider.System;
    }

    private string FilePath => Path.Combine(_dirPath, FileName);

    /// <summary>Extracts the rules from one session's report and merges them in, newest first, capped
    /// by count and by bytes. A rule already present is NOT written again, whichever session said it —
    /// the same lesson learned twice is one lesson. Thread-safe. Writes nothing at all when the text
    /// carries no rule, so a status-only session leaves the file (and the next prompt) untouched.</summary>
    public void Append(string stage, int session, string difficultyText)
    {
        var fresh = ExtractRules(difficultyText);
        if (fresh.Count == 0) return;

        lock (_gate)
        {
            Directory.CreateDirectory(_dirPath);
            var existing = File.Exists(FilePath) ? ParseRules(File.ReadAllText(FilePath, Encoding.UTF8)) : [];
            var seen = new HashSet<string>(existing.Select(r => DedupKey(r.Text)), StringComparer.Ordinal);

            var merged = new List<Rule>();
            foreach (var rule in fresh)
            {
                if (!seen.Add(DedupKey(rule))) continue;
                merged.Add(new Rule($"{stage}-{session}", rule));
            }
            if (merged.Count == 0) return;   // everything this session said is already on file
            merged.AddRange(existing);

            var content = Render(merged, _time.GetUtcNow());
            // Both caps applied to the SAME list before it is written — the old code re-parsed its own
            // output to trim it, which is how an entry ended up on file twice.
            while (merged.Count > _maxRules
                   || (merged.Count > 1 && Encoding.UTF8.GetByteCount(content) > _maxBytes))
            {
                merged.RemoveAt(merged.Count - 1);
                content = Render(merged, _time.GetUtcNow());
            }

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, content, Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    /// <summary>Returns the full content of lessons.md, or empty string if none exists.</summary>
    public string ReadContent()
    {
        if (!File.Exists(FilePath)) return "";
        try
        {
            return File.ReadAllText(FilePath, Encoding.UTF8);
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    /// <summary>The N newest rules, rendered for prompt injection. Empty when there are none, so the
    /// battery contributes no header for an empty file.</summary>
    public string ReadRecent(int maxEntries)
    {
        var rules = ParseRules(ReadContent());
        if (rules.Count == 0 || maxEntries <= 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("Rules earlier sessions paid for:");
        foreach (var r in rules.Take(maxEntries))
            sb.AppendLine(Line(r));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Count of rules currently on file (for testing).</summary>
    internal int EntryCount() => ParseRules(ReadContent()).Count;

    /// <summary>Pulls the rule-shaped sentences out of a session's report.
    /// <para>A SESSION-RESULT is mostly status — what landed, what is red, what is next — and none of
    /// that helps a later session. What does help is the sentence that says what NOT to do again, so
    /// only sentences carrying a rule cue survive, one line each, at most
    /// <see cref="MaxRulesPerSession"/> of them.</para></summary>
    internal static IReadOnlyList<string> ExtractRules(string? text)
    {
        var rules = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return rules;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            foreach (var raw in SplitSentences(line))
            {
                var candidate = Tidy(raw);
                // Short fragments are headings and list labels, not rules.
                if (candidate.Length < 20 || !RuleCue.IsMatch(candidate)) continue;
                candidate = Clip(candidate);
                if (!seen.Add(DedupKey(candidate))) continue;
                rules.Add(candidate);
                if (rules.Count == MaxRulesPerSession) return rules;
            }
        }
        return rules;
    }

    private sealed record Rule(string Source, string Text);

    private static string Line(Rule r) => $"{RuleMarker}{r.Source}] {r.Text}";

    private static string Render(IEnumerable<Rule> rules, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append(HeaderLine).Append("\n\n");
        sb.Append(UpdatedPrefix).Append(now.ToString("o")).Append("\n\n");
        foreach (var r in rules) sb.Append(Line(r)).Append('\n');
        return sb.ToString();
    }

    private static List<Rule> ParseRules(string content)
    {
        var rules = new List<Rule>();
        if (string.IsNullOrEmpty(content)) return rules;

        foreach (var raw in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(RuleMarker, StringComparison.Ordinal)) continue;
            var close = line.IndexOf(']', RuleMarker.Length);
            if (close < 0) continue;
            var source = line[RuleMarker.Length..close];
            var body = line[(close + 1)..].Trim();
            if (body.Length > 0) rules.Add(new Rule(source, body));
        }
        return rules;
    }

    /// <summary>Splits a line into sentences. Crude on purpose — an over-eager split costs a shorter
    /// rule, an under-eager one costs a longer line, and neither is worth a sentence tokenizer.</summary>
    private static IEnumerable<string> SplitSentences(string line)
    {
        var start = 0;
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] is not ('.' or '!' or '?') || !char.IsWhiteSpace(line[i + 1])) continue;
            yield return line[start..(i + 1)];
            start = i + 1;
        }
        if (start < line.Length) yield return line[start..];
    }

    private static string Tidy(string s)
    {
        // Markdown emphasis and list markers are formatting, not content; backticks stay because an
        // identifier is the most useful thing a rule can name.
        var t = s.Replace("**", "", StringComparison.Ordinal).Trim();
        t = Regex.Replace(t, @"^(?:[-*+]|\d+[.)])\s+", "",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
        t = Regex.Replace(t, @"^#+\s*", "", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return Regex.Replace(t, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();
    }

    private static string Clip(string s)
    {
        if (s.Length <= MaxRuleChars) return s;
        var cut = s.LastIndexOf(' ', MaxRuleChars - 1);
        if (cut < MaxRuleChars / 2) cut = MaxRuleChars - 1;
        return s[..cut].TrimEnd(',', ';', ' ') + "…";
    }

    private static string DedupKey(string rule)
        => Regex.Replace(rule.ToLowerInvariant(), @"[^a-z0-9 ]", "", RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)).Trim();
}
