using System.Globalization;
using System.Text;

using Conductor.Core.Money;

using MoneyRow = Conductor.Core.Money.MoneyLine;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>KS11.5 / CHAPAR CH-6 — the figures a reader can ASK for: <c>/progress</c>, <c>/money</c>,
/// <c>/tokens</c>, and the daily digest recomposed in the same grammar.
///
/// <para><b>The one rule this file exists to keep.</b> None of these numbers is computed here. The
/// money and the tokens come from <see cref="MoneySection.Read"/> — the same call
/// <c>conductor money</c> and <c>REPORT.md</c> make, through <c>RunArchive</c> (SQLite
/// <c>Mode=ReadOnly</c>, so answering a phone cannot disturb the run) into
/// <see cref="MoneyAnalyzer"/>. The progress figures come from the same <see cref="IProgressProvider"/>
/// snapshot <c>/status</c> reads. A second arithmetic for the same question is how two surfaces come
/// to quote two different costs for one run, and the owner then has to work out which lied.</para>
///
/// <para>Billed dollars only. The engine has no price table by design and nothing here invents one:
/// every dollar below was reported by the provider and written to the <c>costs</c> table, and a
/// blended rate is those dollars over those tokens.</para></summary>
public sealed partial class MessageComposer
{
    /// <summary>What <c>/progress</c> answers: where the run is, stage by stage.
    ///
    /// <para><c>/status</c> already says the current stage in detail; this says the ROAD — every
    /// stage with its share of the checkpoints — which is the question a reader who has just been
    /// onboarded actually has, and the one a status line clipped to the current stage cannot answer.
    /// The counts are the same snapshot <c>/status</c> counts, so the two cannot disagree.</para></summary>
    public string ProgressText()
    {
        var track = Tracker();

        var sb = new StringBuilder();
        sb.AppendLine($"<b>{EscapeHtml(PlanName())} — progress</b>");

        if (track.Checkpoints.Count == 0)
        {
            sb.AppendLine();
            sb.Append("No checkpoints recorded yet — the tracker this run reads has no rows.");
            return sb.ToString();
        }

        sb.AppendLine();
        foreach (var stage in _plan.Stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Id)) continue;
            var rows = track.ForStage(stage.Id).ToList();
            if (rows.Count == 0) continue;

            var done = rows.Count(r => r.IsDone || r.IsSkipped);
            var here = string.Equals(stage.Id, _state.CurrentStage, StringComparison.OrdinalIgnoreCase);
            var mark = done == rows.Count ? "done" : here ? "here" : "    ";
            sb.AppendLine($"<code>{EscapeHtml($"[{mark}] {stage.Id,-6} {done}/{rows.Count}")}</code>"
                        + $"  {EscapeHtml(Clip(StageTitle(stage.Id), ProgressTitleMaxChars))}");
        }

        var inFlight = track.Checkpoints.Where(c => c.IsInProgress).ToList();
        if (inFlight.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<b>In flight</b>");
            foreach (var row in inFlight)
                sb.AppendLine($"  {EscapeHtml(row.Id)} — {EscapeHtml(Clip(row.Title, ProgressTitleMaxChars))}");
        }

        if (_state.AttentionReason is { Length: > 0 } reason)
        {
            sb.AppendLine();
            sb.AppendLine(EscapeHtml(reason + Staleness.Since(_state.AttentionSinceUtc)));
        }

        sb.AppendLine();
        sb.Append(Telemetry(PulledFacts()));
        return sb.ToString();
    }

    private const int ProgressTitleMaxChars = 56;

    /// <summary>What <c>/money</c> answers: billed dollars, where they went, and what they bought.
    ///
    /// <para>Read through <see cref="MoneySection.Read"/> so the figure a phone quotes is the figure
    /// <c>conductor money</c> quotes for the same database, to the cent. When the database has no
    /// spend on record the answer says so rather than printing a table of zeros — and still carries
    /// the run's own counter, because a reader asking about money is owed a number.</para></summary>
    public string MoneyText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>{EscapeHtml(PlanName())} — money</b>");
        sb.AppendLine();

        var money = Money();
        if (money is null)
        {
            sb.AppendLine("Nothing billed on record yet for this run.");
            sb.AppendLine();
            sb.Append(Telemetry(PulledFacts()));
            return sb.ToString();
        }

        var total = money.Total;
        sb.AppendLine("Billed " + EscapeHtml(MoneyLine.Spend(total.Cost, CostCeiling()))
                    + EscapeHtml($" · {total.Sessions} session{(total.Sessions == 1 ? "" : "s")}"));

        if (total.CostPerCheckpoint is { } perCheckpoint)
            sb.AppendLine(EscapeHtml(
                $"{MoneyLine.Usd(perCheckpoint)} per delivered checkpoint ({total.Checkpoints} closed by these sessions)"));

        if (money.Categories.Count > 0)
            sb.AppendLine("Where it goes: " + EscapeHtml(string.Join(" · ", money.Categories.Select(Lane(total.Cost)))));

        if (total.CostPerMillionTokens is { } blended)
            sb.AppendLine("Blended " + EscapeHtml(MoneyLine.Usd(blended)) + " per million tokens — billed dollars over "
                        + "billed tokens, not a price list.");

        // The run's own counter and the database are two records of one truth, and they are allowed
        // to differ mid-session: the loop folds a session's cost when it ENDS, and out-of-process
        // spend is absorbed at the boundary after that. Saying so beats a reader spotting a
        // difference between this answer and the last push and having to guess which one is broken.
        var counter = _state.TotalCostUsd;
        if (Math.Abs(counter - total.Cost) >= 0.01m)
            sb.AppendLine(EscapeHtml($"The run's own counter says {MoneyLine.Usd(counter)} — "
                + (counter > total.Cost ? "a session in flight is not billed to the record yet." : "spend recorded outside the run loop reaches the record first.")));

        sb.AppendLine();
        sb.Append(Telemetry(PulledFacts()));
        return sb.ToString();
    }

    /// <summary>One spending lane as the money answer lists it: what it is, what it cost, and what
    /// share of the bill that is.</summary>
    private static Func<MoneyRow, string> Lane(decimal total) => lane =>
        total > 0m
            ? $"{lane.Label} {MoneyLine.Usd(lane.Cost)} ({Percent((double)(lane.Cost / total))})"
            : $"{lane.Label} {MoneyLine.Usd(lane.Cost)}";

    /// <summary>What <c>/tokens</c> answers: every token this run has spent, and the cache-read share
    /// that makes the bill make sense.
    ///
    /// <para>The share is the headline because in an era like this one it is around 98%: a reader who
    /// sees 30M tokens and no split concludes the run wrote thirty million tokens of code. Same
    /// <see cref="MoneyRun"/> as <c>/money</c>, so the two answers cannot quote different totals.</para></summary>
    public string TokensText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>{EscapeHtml(PlanName())} — tokens</b>");
        sb.AppendLine();

        var money = Money();
        if (money is null || money.Total.Tokens <= 0)
        {
            sb.AppendLine("No tokens recorded on this run's database yet.");
            sb.AppendLine();
            sb.Append(Telemetry(PulledFacts()));
            return sb.ToString();
        }

        var total = money.Total;
        sb.AppendLine(EscapeHtml($"{Tokens(total.Tokens)} tokens over {total.Sessions} session{(total.Sessions == 1 ? "" : "s")}"));
        sb.AppendLine(EscapeHtml($"Cache reads {Tokens(total.CacheReadTokens)} ({Percent(total.CacheReadShare)}) — "
            + "the prompt being re-sent, charged at the cache rate."));
        sb.AppendLine(EscapeHtml($"Input {Tokens(total.InputTokens)} · output {Tokens(total.OutputTokens)}"));

        if (total.TokensPerCheckpoint is { } perCheckpoint)
            sb.AppendLine(EscapeHtml(
                $"{Tokens((long)Math.Round(perCheckpoint, MidpointRounding.AwayFromZero))} per delivered checkpoint"));

        sb.AppendLine();
        sb.Append(Telemetry(PulledFacts()));
        return sb.ToString();
    }

    // ────────────────────────────── the shared reads ──────────────────────────────

    /// <summary>This run's money, read the way <c>conductor money</c> reads it. Null when there is no
    /// database, no run id, or no billed row yet.</summary>
    private MoneyRun? Money() => MoneySection.Read(_plan, _state.RunId);

    /// <summary>The tracker snapshot, tolerated the same way every other pulled view tolerates it: a
    /// tracker that cannot be read answers as an empty one rather than taking the reply down.</summary>
    private TrackerSnapshot Tracker()
    {
        try { return _progress.Read(_plan, CancellationToken.None); }
        catch (IOException) { return new TrackerSnapshot(); }
        catch (InvalidOperationException) { return new TrackerSnapshot(); }
    }

    /// <summary>The telemetry line every PULLED answer ends with, built from the same fragments the
    /// pushes use — so the numbers on a message the run sent and on an answer a reader asked for are
    /// the same numbers, written the same way.</summary>
    private string PulledFacts()
    {
        var parts = new List<string>(3);
        if (ProgressLine(null) is { Length: > 0 } progress) parts.Add(progress);
        parts.Add(MoneyLine.ForRun(_state.TotalCostUsd, CostCeiling()));
        if (TokenLine() is { Length: > 0 } tokens) parts.Add(tokens);
        return string.Join(" · ", parts);
    }

    private string StageTitle(string stageId)
    {
        var title = _plan.Stages.FirstOrDefault(s =>
            string.Equals(s.Id, stageId, StringComparison.OrdinalIgnoreCase))?.Title;
        return string.IsNullOrWhiteSpace(title) ? stageId : title.Trim();
    }

    /// <summary>Tokens at the magnitude a reader can hold: 3.5M, 240k, 900. The same rule
    /// <see cref="TokenLine"/> uses, because a run cannot have two token vocabularies.</summary>
    private static string Tokens(long total) => total >= 1_000_000
        ? FormattableString.Invariant($"{total / 1_000_000.0:0.#}M")
        : total >= 1_000
            ? FormattableString.Invariant($"{total / 1_000.0:0.#}k")
            : total.ToString(CultureInfo.InvariantCulture);

    private static string Percent(double share) =>
        (share * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
