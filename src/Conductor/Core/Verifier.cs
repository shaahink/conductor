using System.Text.Json;
using System.Text.RegularExpressions;
using Conductor.Models;

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

        var m = Regex.Match(agentOutput, "\\{[^{}]*\"score\"[^{}]*\\}",
            RegexOptions.Singleline, ProgressConventions.RegexTimeout);
        if (!m.Success) return null;

        try
        {
            using var doc = JsonDocument.Parse(m.Value);
            var root = doc.RootElement;
            var score = root.TryGetProperty("score", out var s) && s.TryGetInt32(out var sv) ? sv : -1;
            if (score < 0 || score > 100) return null;

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

            return new VerifierVerdict(score, findings, verdict);
        }
        catch (JsonException) { return null; }
    }
}
