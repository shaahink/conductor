using System.Text.Json;

namespace Conductor.Models;

/// <summary>
/// The plan's two MODEL-CONSULT blocks and the refusals that keep them honest: the advisor, whose
/// answer moves the run, and KS4.5's judge, whose answer is only ever recorded. They live together,
/// away from the rest of the schema, because they fail in exactly the same ways — an unknown key that
/// looks like it does something, a CLI spawned with no question, an envelope nothing unwraps — and a
/// fix to one is nearly always owed to the other. Split out of PlanConfig.cs when the judge's arrival
/// took that file past the architecture ratchet's 500-line ceiling.
/// </summary>
public sealed partial class PlanConfig
{
    /// <summary>KS4.5 — the judge block, judged on the advisor's terms. Same failure modes, same
    /// refusals: an unknown key is named rather than ignored (bug 7), an argless or promptless CLI is
    /// refused because it would burn its whole timeout answering nothing (SC3.4), and an unknown
    /// output kind is refused because the envelope would reach the parser still wrapped. The one
    /// difference is that a judge is off by default, so a plan without the block pays nothing.</summary>
    private static void ValidateJudge(JudgeConfig? j, List<string> errors)
    {
        if (j is null) return;

        foreach (var key in (j.UnknownFields?.Keys ?? Enumerable.Empty<string>()).OrderBy(k => k, StringComparer.Ordinal))
        {
            var hint = key.Equals("threshold", StringComparison.OrdinalIgnoreCase)
                       || key.Equals("minScore", StringComparison.OrdinalIgnoreCase)
                       || key.Equals("blockOnFail", StringComparison.OrdinalIgnoreCase)
                ? " There is no judge threshold and there will not be one: the judge is evidence, never verdict (KS4.5) — a score it produced cannot fail a session, so a key that looks like a bar would be a lie about what this block does."
                : "";
            errors.Add($"plan.judge.{key} is not a judge field — nothing reads it, so it cannot do what it looks like it does. " +
                       $"Known fields: {string.Join(", ", JudgeConfig.KnownFields)}.{hint}");
        }

        if (!j.Enabled) return; // a disabled judge is never spawned

        // SC3.3: focus is authored prose spliced into a prompt, so a name that looks like a variable
        // is not one and reaches the judge verbatim as a broken instruction.
        ValidateProse("plan.judge.focus", j.Focus, errors);

        if (string.IsNullOrWhiteSpace(j.Command))
            errors.Add("plan.judge.command is empty — name the CLI that reviews, or set judge.enabled false");

        if (j.Args.Count == 0)
            errors.Add("plan.judge.args is empty — a CLI spawned with no arguments is handed no question: it waits on " +
                       $"stdin until judge.timeoutMinutes expires and answers nothing. Use [\"{string.Join("\", \"", JudgeConfig.DefaultArgs)}\"] " +
                       "(the shipped default), or set judge.enabled false");
        else if (!j.Args.Any(x => x.Contains("{prompt}", StringComparison.Ordinal)))
            errors.Add("plan.judge.args carries no {prompt} placeholder — the judge would be spawned without the work it is being asked to review");

        if (!AdvisorConfig.IsKnownOutput(j.Output))
            errors.Add($"plan.judge.output is '{j.Output}' — use {string.Join(", ", AdvisorConfig.OutputKinds)}. An unknown kind is passed " +
                       "through raw, so a JSON envelope reaches the parser still wrapped and every review reads as unparseable");

        if (j.TimeoutMinutes < 1)
            errors.Add($"plan.judge.timeoutMinutes must be >= 1 (was {j.TimeoutMinutes}) — a zero timeout kills the judge before it can answer");
    }

    private static void ValidateAdvisor(AdvisorConfig? a, List<string> errors)
    {
        if (a is null) return; // no advisor block is a supported choice — ambiguity takes the default

        // bug 7: an inert key that looks like agent.provider is a lie about which model answers.
        foreach (var key in (a.UnknownFields?.Keys ?? Enumerable.Empty<string>()).OrderBy(k => k, StringComparer.Ordinal))
        {
            var hint = key.Equals("provider", StringComparison.OrdinalIgnoreCase)
                ? " The advisor has no provider adapter: advisor.command plus its args pick the CLI and the model, and advisor.output only says how to unwrap the answer."
                : "";
            errors.Add($"plan.advisor.{key} is not an advisor field — nothing reads it, so it cannot do what it looks like it does. " +
                       $"Known fields: {string.Join(", ", AdvisorConfig.KnownFields)}.{hint}");
        }

        if (!a.Enabled) return; // a disabled advisor is never spawned, so its invocation is moot

        if (string.IsNullOrWhiteSpace(a.Command))
            errors.Add("plan.advisor.command is empty — name the CLI that answers, or set advisor.enabled false");

        if (a.Args.Count == 0)
            errors.Add("plan.advisor.args is empty — a CLI spawned with no arguments is handed no question: it waits on " +
                       $"stdin until advisor.timeoutMinutes expires and answers nothing. Use [\"{string.Join("\", \"", AdvisorConfig.DefaultArgs)}\"] " +
                       "(the shipped default), or set advisor.enabled false");
        else if (!a.Args.Any(x => x.Contains("{prompt}", StringComparison.Ordinal)))
            errors.Add("plan.advisor.args carries no {prompt} placeholder — the advisor would be spawned without the question it is being asked");

        if (!AdvisorConfig.IsKnownOutput(a.Output))
            errors.Add($"plan.advisor.output is '{a.Output}' — use {string.Join(", ", AdvisorConfig.OutputKinds)}. An unknown kind is passed " +
                       "through raw, so a JSON envelope reaches the parser still wrapped and every answer reads as unparseable");

        if (a.TimeoutMinutes < 1)
            errors.Add($"plan.advisor.timeoutMinutes must be >= 1 (was {a.TimeoutMinutes}) — a zero timeout kills the advisor before it can answer");
    }

    /// <summary>SF0.1 / bug 6: the general form of the advisor block's bug-7 check — a key the type
    /// does not declare is named and refused rather than parsed into a bucket nobody reads. The hint
    /// exists because "unknown key" is not the useful half of the message; "here is the key that
    /// really does this" is.</summary>
    private static void ValidateInertKeys(string where, Dictionary<string, JsonElement>? unknown,
        IReadOnlyList<string> known, Func<string, string> hint, List<string> errors)
    {
        foreach (var key in (unknown?.Keys ?? Enumerable.Empty<string>()).OrderBy(k => k, StringComparer.Ordinal))
        {
            errors.Add($"{where}.{key} is not a field here — nothing reads it, so it cannot do what it " +
                       $"looks like it does. Known fields: {string.Join(", ", known)}.{hint(key)}");
        }
    }

    /// <summary>The one inert key both deleted blocks shared, and the only one worth a sentence: a
    /// pinned model that never reached the agent is a plan claiming one model answered while another
    /// one did.</summary>
    private static string InertModelHint(string key) =>
        key.Equals("model", StringComparison.OrdinalIgnoreCase)
            ? " A session's model comes from pipeline.roles.<role>.model, else stage.agent.model, else plan.agent.model —" +
              " set it in one of those."
            : "";
}
