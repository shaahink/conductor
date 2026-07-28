using System.Text.Json;

namespace Conductor.Core;

public sealed record VerifierVerdict(int Score, IReadOnlyList<string> Findings, string Verdict)
{
    public bool Passes(int threshold) => Score >= threshold;
}

public static class Verifier
{
    public static VerifierVerdict? Parse(string agentOutput)
    {
        if (string.IsNullOrWhiteSpace(agentOutput)) return null;

        // Scan for every balanced top-level {...} span rather than a single-level regex
        // (`\{[^{}]*"score"[^{}]*\}`): a verifier's findings routinely quote things like
        // `{model}`/`{planDoc}` placeholders, and a stray brace inside a finding string used
        // to break the old regex outright. If the agent writes more than one candidate object
        // (against instructions), the LAST one that parses wins — it's the agent's final say.
        VerifierVerdict? found = null;
        foreach (var candidate in FindBalancedJsonObjects(agentOutput))
        {
            if (!candidate.Contains("\"score\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                var score = root.TryGetProperty("score", out var s) && s.TryGetInt32(out var sv) ? sv : -1;
                if (score < 0 || score > 100) continue;

                var findings = new List<string>();
                if (root.TryGetProperty("findings", out var f) && f.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in f.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            findings.Add(item.GetString()!);
                }

                var verdict = root.TryGetProperty("verdict", out var v)
                    ? v.GetString() ?? (score >= 80 ? "PASS" : "FAIL")
                    : score >= 80 ? "PASS" : "FAIL";

                found = new VerifierVerdict(score, findings, verdict);
            }
            catch (JsonException) { /* not a valid candidate — keep scanning */ }
        }
        return found;
    }

    /// <summary>Finds every complete, balanced top-level <c>{...}</c> substring, tracking brace
    /// depth and string-literal state (with escape handling) so braces inside quoted text never
    /// throw off the match — unlike a regex built on <c>[^{}]*</c>.</summary>
    private static IEnumerable<string> FindBalancedJsonObjects(string text)
    {
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
