using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Ui;

/// <summary>
/// Parses free-form agent reasoning into the Goal / Hypothesis / Evidence / Action frame (B4.5).
/// Pure and allocation-light so the thinking pane can render a structured digest instead of a wall
/// of prose. When a reasoning block carries none of the recognised markers the raw text is kept, so
/// unstructured thinking still renders (no information is dropped).
/// </summary>
public static class StructuredThinking
{
    /// <summary>One reasoning block reduced to the four named facets. <see cref="HasStructure"/> is
    /// false when the parser found no markers — callers then fall back to <see cref="Raw"/>.</summary>
    public readonly record struct Thought(string? Goal, string? Hypothesis, string? Evidence, string? Action, string Raw)
    {
        public bool HasStructure => Goal is not null || Hypothesis is not null || Evidence is not null || Action is not null;
    }

    // Marker = a facet keyword at a line/sentence boundary followed by ':' or '-'. Case-insensitive.
    // ExplicitCapture + RegexTimeout match the codebase convention (MA0009/MA0023).
    private static readonly Regex Marker = new(
        @"(?:^|[\s.;\-*\u2022])(?<facet>goal|hypothesis|evidence|action)\s*[:\-]\s*",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        ProgressConventions.RegexTimeout);

    private static readonly Regex WhitespaceRuns = new(
        @"\s{2,}", RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

    public static Thought Parse(string? text)
    {
        var raw = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        raw = WhitespaceRuns.Replace(raw, " ");
        if (raw.Length == 0) return new Thought(null, null, null, null, raw);

        var matches = Marker.Matches(raw);
        if (matches.Count == 0) return new Thought(null, null, null, null, raw);

        string? goal = null, hyp = null, evi = null, act = null;
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var facet = m.Groups["facet"].Value.ToLowerInvariant();
            var start = m.Index + m.Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : raw.Length;
            var value = raw[start..end].Trim().TrimEnd('.', ';', ',', ' ');
            if (value.Length == 0) continue;
            switch (facet)
            {
                case "goal": goal ??= value; break;
                case "hypothesis": hyp ??= value; break;
                case "evidence": evi ??= value; break;
                case "action": act ??= value; break;
            }
        }
        return new Thought(goal, hyp, evi, act, raw);
    }
}

