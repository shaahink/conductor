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

        // Scan for every balanced top-level {...} span rather than a single-level regex — see
        // JsonScan, which now carries that lesson for the judge's parser too. If the agent writes
        // more than one candidate object (against instructions), the LAST one that parses wins —
        // it's the agent's final say.
        VerifierVerdict? found = null;
        foreach (var candidate in JsonScan.BalancedObjects(agentOutput))
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
}
