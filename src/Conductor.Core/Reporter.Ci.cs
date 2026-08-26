using System.Text;

using Conductor.Core.Integrations;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>CH1.3 — the report's CI block. Its own file for the reason
/// <c>Reporter.Channels.cs</c> is: <c>Reporter.cs</c> stands at the 500-line architecture ceiling,
/// and the rule there is extract, not append.</summary>
public static partial class Reporter
{
    /// <summary>CH1.3 — whether CI runs the battery this run's gates just ran, in the header, beside
    /// the channels.
    ///
    /// <para>The Divan era's local battery was green for 23 checkpoints while CI's windows leg was
    /// red on every commit of the era, and this file — the AFK progress view, the one an operator
    /// actually reads — said nothing, because nothing compared them. One roll-up line ALWAYS, on
    /// DV1.1's rule: "the report does not mention CI" and "CI runs the same battery" must not look
    /// the same. A loud line only when they have actually drifted, carrying the edit that closes
    /// it.</para></summary>
    private static void AppendCi(StringBuilder sb, PlanConfig plan)
    {
        var rows = CiAgreementProbe.Collect(plan);
        sb.AppendLine($"**CI battery:** {ChannelHealthProbe.SummaryLine(rows)}");
        foreach (var c in ChannelHealthProbe.Loud(rows))
            sb.AppendLine($"**⚠ CI {c.Word.ToUpperInvariant()} — {c.Channel}:** {c.Detail} · fix: {c.Fix}");
    }
}
