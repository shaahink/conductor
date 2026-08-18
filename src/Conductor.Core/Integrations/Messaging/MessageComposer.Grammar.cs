using System.Globalization;

using Conductor.Models;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>KS11.3 / CHAPAR CH-4 and CH-5 — the GRAMMAR: the introduction a chat gets before its
/// first push, and the three lines every push after it is made of.
///
/// <para>A page of its own because it is a different job from the push bodies next door. Those
/// answer "what does this event say"; this answers "what shape does anything this run says take" —
/// headline, then what proves it, then the numbers, with the figures in monospace so they line up
/// from one message to the next.</para></summary>
public sealed partial class MessageComposer
{
    /// <summary>KS11.3 / CHAPAR CH-4 — the bot's first message to a chat, in that chat's voice.
    ///
    /// <para>Three questions, because a chat that cannot answer them cannot read anything that comes
    /// after: what this run IS (the plan, its stage map, its budget ceiling), what will arrive here
    /// and when, and exactly what this chat may ask for. The last one is composed from
    /// <see cref="SurfaceCommands"/> rather than written out, so the promise and the gate that
    /// enforces it cannot drift apart.</para></summary>
    public Task<string> OnboardingAsync(ChatProfile profile, bool twoWay)
    {
        var builtIn = profile == ChatProfile.Admin ? NotifyDefaults.OnboardingAdmin : NotifyDefaults.OnboardingObserver;
        return ComposeAsync($"onboarding-{ChatProfiles.Name(profile)}", builtIn,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = EscapeHtml(PlanName()),
                ["plan"] = EscapeHtml(PlanShapeLine()),
                ["budget"] = Telemetry(BudgetFacts()),
                ["arrivals"] = ArrivalsLine(profile),
                ["asks"] = SurfaceCommands.AskLine(profile, twoWay),
            });
    }

    /// <summary>The plan at a glance: how much work, and the road through it. Clipped rather than
    /// paginated — an onboarding message that opens with forty stage ids is one nobody reads.</summary>
    private string PlanShapeLine()
    {
        var parts = new List<string>(2);

        var ids = _plan.Stages.Select(st => st.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count > 0)
        {
            var shown = string.Join(" → ", ids.Take(StagesInOnboarding));
            if (ids.Count > StagesInOnboarding) shown += $" → … (+{ids.Count - StagesInOnboarding})";
            parts.Add($"{ids.Count} stage{(ids.Count == 1 ? "" : "s")}: {shown}");
        }

        try
        {
            var track = _progress.Read(_plan, CancellationToken.None);
            if (track.Checkpoints.Count > 0)
                parts.Add($"{track.Checkpoints.Count(c => c.IsDone)}/{track.Checkpoints.Count} checkpoints done");
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }

        return string.Join(" · ", parts);
    }

    private const int StagesInOnboarding = 6;

    /// <summary>What this chat will actually receive, said before the first one arrives. Identical
    /// for both profiles because it IS identical — CH-3 gives an observer the run's whole story and
    /// closes only what they may ASK for — and saying so is what stops a reader assuming they are
    /// being shown a filtered version.</summary>
    private static string ArrivalsLine(ChatProfile profile)
    {
        const string story = "A message when a session ends, when evidence lands, and when the run "
                           + "finishes or parks for a decision. Each one says what landed, what proves "
                           + "it, and what it has cost so far.";
        return profile == ChatProfile.Admin
            ? story + " A decision the run needs from you arrives with buttons."
            : story + " Nothing is filtered — this is the same story the run's owner gets.";
    }

    private string PlanName() => string.IsNullOrWhiteSpace(_plan.Name) ? "conductor" : _plan.Name.Trim();

    // ── KS11.3 / CH-5: the three lines every push is made of ──

    /// <summary>The proof half of the grammar: the gate verdict, and the artifact that shows the
    /// work. Evidence used to sit at the bottom of the result block, below the gaps, where it read
    /// as an afterthought rather than as the thing that makes the headline checkable.</summary>
    private static string ProofLine(string gates, IReadOnlyList<string> evidence)
    {
        var parts = new List<string>(2);
        if (gates.Length > 0) parts.Add("gates " + EscapeHtml(gates));
        if (evidence.Count > 0) parts.Add("evidence " + EscapeHtml(string.Join(", ", evidence)));
        return parts.Count == 0 ? "" : "proof: " + string.Join(" · ", parts);
    }

    /// <summary>The telemetry half: the run’s own numbers, in monospace so the figures line up
    /// between one push and the next instead of drifting with the width of the words around them.
    /// <para>Empty in, empty out — <see cref="NotifyTemplate"/> drops a blank line, and an empty
    /// code element would be a blank box instead.</para></summary>
    private static string Telemetry(string facts) =>
        facts.Length == 0 ? "" : "<code>" + EscapeHtml(facts) + "</code>";

    /// <summary>Progress, money and tokens for one session’s push.</summary>
    private string TelemetryFacts(string? stageId, decimal? sessionCost, decimal? score)
    {
        var parts = new List<string>(4);

        var progress = ProgressLine(stageId);
        if (progress.Length > 0) parts.Add(progress);
        parts.Add(sessionCost is { } c
            ? MoneyLine.ForSession(c, _state.TotalCostUsd, CostCeiling())
            : MoneyLine.ForRun(_state.TotalCostUsd, CostCeiling()));
        if (TokenLine() is { Length: > 0 } tokens) parts.Add(tokens);
        if (score is { } s) parts.Add(FormattableString.Invariant($"score {s:0}/100"));

        return string.Join(" · ", parts);
    }

    /// <summary>What an onboarding message says about money and tokens: the ceiling this run is
    /// governed by and what it has spent of it. Labelled <c>budget</c> rather than <c>cost</c> —
    /// nothing has happened yet from this reader's point of view, and a first message that opens
    /// with a bill reads as one.</summary>
    private string BudgetFacts()
    {
        var parts = new List<string> { "budget " + MoneyLine.Spend(_state.TotalCostUsd, CostCeiling()) };
        if (TokenLine() is { Length: > 0 } tokens) parts.Add(tokens);
        return string.Join(" · ", parts);
    }

    /// <summary>The same, for a push about the whole run rather than one session.</summary>
    private string RunTelemetryFacts()
    {
        var parts = new List<string> { MoneyLine.ForRun(_state.TotalCostUsd, CostCeiling()) };
        if (TokenLine() is { Length: > 0 } tokens) parts.Add(tokens);
        return string.Join(" · ", parts);
    }

    /// <summary>Tokens, cache reads included — <see cref="RunState.TotalTokens"/> is the one place
    /// that decides what "tokens" means. Nothing at all when the run has spent none, because a
    /// telemetry line reading <c>0 tokens</c> tells a reader the meter is broken.</summary>
    private string TokenLine()
    {
        var total = _state.TotalTokens;
        if (total <= 0) return "";
        return "tokens " + (total >= 1_000_000
            ? FormattableString.Invariant($"{total / 1_000_000.0:0.#}M")
            : total >= 1_000
                ? FormattableString.Invariant($"{total / 1_000.0:0.#}k")
                : total.ToString(CultureInfo.InvariantCulture));
    }
}
