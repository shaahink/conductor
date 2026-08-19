using System.Text.Json;

using Conductor.Core.Accounting;
using Conductor.Core.Providers;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>What a judge consult produced and what it cost. Same shape, and same reason, as
/// <see cref="AdvisorReply"/>: a review that failed to parse was still billed.</summary>
public sealed record JudgeReply(JudgeReview? Review, string? Text, SpendReceipt? Spend)
{
    /// <summary>The reply for a judge that was never spawned: nothing said, nothing spent.</summary>
    public static JudgeReply None { get; } = new(null, null, null);
}

/// <summary>
/// KS4.5 — the advisory second-model review. Anthropic's own harness guidance (a separate evaluator;
/// self-evaluation produces confident praise of mediocre work) and Amp's oracle, adopted on this
/// project's terms: the review is EVIDENCE, and the deterministic signals still decide.
/// <para>Nothing in this file is reachable from <see cref="Orchestration.SessionVerdict.Decide"/>, and
/// that is asserted rather than intended — see KS4_5JudgeTests.</para>
/// </summary>
public static class Judge
{
    /// <summary>The output contract handed to the judge, and the shape <see cref="Parse"/> reads.</summary>
    public const string OutputContract =
        "{\"verdict\":\"pass|fail|concerns\",\"score\":0-100,\"findings\":[\"...\"],\"summary\":\"one sentence\"}";

    /// <summary>Read a review out of whatever the model wrote around it. Null when there is no object
    /// carrying a verdict — an unparseable judge is recorded as unavailable, never defaulted into an
    /// opinion.</summary>
    public static JudgeReview? Parse(string? modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput)) return null;

        // Last one wins, like Verifier.Parse: if the model writes a worked example before its answer,
        // the answer is the one at the bottom.
        JudgeReview? found = null;
        foreach (var candidate in JsonScan.BalancedObjects(modelOutput))
        {
            if (!candidate.Contains("\"verdict\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                if (!root.TryGetProperty("verdict", out var v) || v.ValueKind != JsonValueKind.String) continue;
                var verdict = v.GetString();
                if (string.IsNullOrWhiteSpace(verdict)) continue;

                int? score = null;
                if (root.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number
                    && s.TryGetInt32(out var sv) && sv is >= 0 and <= 100) score = sv;

                var findings = new List<string>();
                if (root.TryGetProperty("findings", out var f) && f.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in f.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } line)
                            findings.Add(line);
                }

                var summary = root.TryGetProperty("summary", out var sum) && sum.ValueKind == JsonValueKind.String
                    ? sum.GetString() : null;

                found = new JudgeReview(verdict.Trim(), score, findings, summary);
            }
            catch (JsonException) { /* not a candidate — keep scanning */ }
        }
        return found;
    }

    /// <summary>Spawn the configured judge and read its review. Every failure — off, argless, timed
    /// out, thrown, unparseable — answers <see cref="JudgeReply.None"/> or a reply with a null review;
    /// none of them can change a verdict, because no caller of this method is allowed to.</summary>
    public static async Task<JudgeReply> ReviewAsync(PlanConfig plan, string prompt, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var j = plan.Judge;
        if (j is not { Enabled: true } || string.IsNullOrWhiteSpace(j.Command)) return JudgeReply.None;
        if (j.Args.Count == 0)
        {
            log?.Invoke($"judge not consulted: judge.args is empty, so '{j.Command}' would be spawned with no question — " +
                        $"set judge.args (default: {string.Join(" ", JudgeConfig.DefaultArgs)})");
            return JudgeReply.None;
        }

        try
        {
            var args = Advisor.ResolveArgs(j.Args, prompt);
            var r = await ProcessRunner.RunAsync(j.Command, args, plan.Repo, TimeSpan.FromMinutes(j.TimeoutMinutes))
                .ConfigureAwait(false);
            // The bill is read before the timeout is reported, for KS5.2's reason: a judge that ran
            // out of clock has still been charged for the tokens it burned getting there.
            var declared = AgentProviderFactory.Create(new AgentConfig { Command = j.Command, Output = j.Output });
            var ms = (long)r.Duration.TotalMilliseconds;
            var spend = BilledSpend.Read(declared, SpendCategory.Judge, r.Output, ms)
                        ?? BilledSpend.ReadFromCommand(j.Command, SpendCategory.Judge, r.Output, ms);
            if (r.TimedOut)
            {
                log?.Invoke("judge timed out — no review this session");
                return new JudgeReply(null, null, spend);
            }
            var text = Advisor.UnwrapEnvelope(r.Output, j.Output);
            return new JudgeReply(Parse(text), text, spend);
        }
        catch (Exception ex)
        {
            log?.Invoke($"judge failed: {ex.Message}");
            return JudgeReply.None;
        }
    }
}
