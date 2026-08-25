using System.Text;
using Conductor.Core.Integrations;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>DV1.1 — the report's channel block. Its own file because <c>Reporter.cs</c> stands at the
/// 500-line architecture ceiling: KS5.4 set the precedent when doctor's budget check grew, and the
/// rule there was extract, not append.</summary>
public static partial class Reporter
{
    /// <summary>DV1.1 — the outbound channels, in the header, where the run's other facts are.
    ///
    /// <para>The edge run's github mirror was enabled with no token; it said so twice, to
    /// <c>.conductor/conductor.log</c>, and this file — the AFK progress view, the one an operator
    /// actually reads — said nothing at all, for twenty-three sessions. One roll-up line always, so
    /// "the report does not mention github" and "github is fine" stop looking the same, and a loud
    /// line per broken channel carrying the fix.</para>
    ///
    /// <para>Started-ness is not passed: <see cref="Build"/> is static and is called by
    /// <c>conductor report</c> from outside the engine as readily as from inside it, so it has no
    /// honest answer, and <see cref="ChannelHealthProbe"/> does not claim one.</para></summary>
    private static void AppendChannels(StringBuilder sb, PlanConfig plan)
    {
        var channels = ChannelHealthProbe.Collect(plan);
        sb.AppendLine($"**Channels:** {ChannelHealthProbe.SummaryLine(channels)}");
        foreach (var c in ChannelHealthProbe.Loud(channels))
        {
            var fix = c.FixCommand.Length > 0 ? $"`{c.FixCommand}`" : c.Fix;
            sb.AppendLine($"**⚠ Channel {c.Word.ToUpperInvariant()} — {c.Channel}:** {c.Detail} · fix: {fix}");
        }
    }
}
