using Conductor.Models;
using System.Text;

namespace Conductor.Core;

/// <summary>
/// A pluggable, bounded section injected into every session prompt (B8.5).
/// Each battery is opt-in per plan and must be deterministic + byte-bounded.
/// </summary>
public interface IPromptBattery
{
    /// <summary>Human-readable name, shown in logs and the rendered prompt header.</summary>
    string Name { get; }

    /// <summary>Rendered content for the prompt, bounded (≤ a few hundred bytes).
    /// Empty string = nothing injected.</summary>
    string Section { get; }

    /// <summary>True when this battery has no content to contribute (saves token budget).</summary>
    bool IsEmpty { get; }
}

/// <summary>
/// Composes multiple batteries in order, rendering each non-empty section with its
/// name as a header, inside a total budget of <see cref="_maxBytes"/>.
///
/// <para>DV2.2, bug #62 — the budget is shared PER BATTERY. It used to be applied to the
/// concatenation: every section was joined and the tail was cut at the cap, so a battery that
/// grew deleted whichever battery sat BEHIND it, whole. Measured on a 45-session run: the
/// <c>open bugs</c> section was present in prompts 026 and 032 and gone from 038 onward with
/// eleven bugs open the entire time, starved by the knowledge ledger in front of it. The
/// comment in <c>PromptBuilder.BatterySection</c> claims that ordering the ledger and the bugs
/// first protects them from the cap; ordering protects only the FIRST battery, and it was the
/// second one that died.</para>
///
/// <para>Now each battery gets an equal share of the budget, and any battery that wants less
/// than its share releases the surplus to the ones that want more — max-min fair share, so a
/// battery can lose room only to the budget, never to a neighbour. And when a section is
/// trimmed or dropped, <see cref="Render"/> SAYS SO in the rendered text: the failure this
/// replaces was silent, and a session cannot tell a short section from a cut one by looking.</para>
/// </summary>
public sealed class BatteryGroup
{
    private readonly List<IPromptBattery> _batteries;
    private readonly int _maxBytes;

    /// <summary>Room held back so the "what got cut" notice can never itself be the thing that
    /// gets cut. Only held back when something is actually short of room, and never more than a
    /// quarter of the budget — a tiny cap has no room to explain itself and says so by staying
    /// silent rather than by spending its whole allowance on the apology.</summary>
    private const int NoticeReserve = 220;

    /// <summary>Below this many characters of body a section carries no usable information, so it
    /// is dropped and named rather than rendered as a heading over an ellipsis.</summary>
    private const int MinBody = 80;

    public static BatteryGroup Empty { get; } = new(Array.Empty<IPromptBattery>());

    public BatteryGroup(IEnumerable<IPromptBattery> batteries, int maxBytes = 2048)
    {
        _batteries = batteries.ToList();
        _maxBytes = maxBytes;
    }

    /// <summary>Rendered prompt section combining all non-empty batteries, or empty string.</summary>
    public string Render()
    {
        var blocks = _batteries.Where(b => !b.IsEmpty)
            .Select(b => (b.Name, Text: Block(b.Name, b.Section)))
            .ToList();
        if (blocks.Count == 0) return "";

        if (blocks.Sum(b => b.Text.Length) <= _maxBytes)
            return string.Concat(blocks.Select(b => b.Text)).TrimEnd();

        var reserve = Math.Min(NoticeReserve, _maxBytes / 4);
        var shares = FairShares(blocks.Select(b => b.Text.Length).ToList(), Math.Max(0, _maxBytes - reserve));

        var sb = new StringBuilder();
        var trimmed = new List<string>();
        var dropped = new List<string>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var fitted = Fit(blocks[i].Text, shares[i]);
            if (fitted is null) { dropped.Add(blocks[i].Name); continue; }
            if (fitted.Length < blocks[i].Text.Length) trimmed.Add(blocks[i].Name);
            sb.Append(fitted);
        }

        var notice = Notice(trimmed, dropped, reserve);
        if (notice.Length == 0) return sb.ToString().TrimEnd();
        return (sb.ToString().TrimEnd() + Environment.NewLine + Environment.NewLine + notice).TrimEnd();
    }

    public bool IsEmpty => _batteries.All(b => b.IsEmpty);

    private static string Block(string name, string section) =>
        $"### {name}{Environment.NewLine}{section}{Environment.NewLine}{Environment.NewLine}";

    /// <summary>Max-min fair share: hand every battery an equal slice, let the ones that want less
    /// than their slice take what they want and return the rest, repeat until either everybody is
    /// satisfied or everybody left wants more than the pot can give — then split the pot evenly.
    /// Deterministic, order-independent, and it cannot hand one battery another's room.</summary>
    private static int[] FairShares(IReadOnlyList<int> wants, int budget)
    {
        var shares = new int[wants.Count];
        var settled = new bool[wants.Count];
        var remaining = budget;
        var open = wants.Count;

        while (open > 0)
        {
            var each = remaining / open;
            var progressed = false;
            for (var i = 0; i < wants.Count; i++)
            {
                if (settled[i] || wants[i] > each) continue;
                shares[i] = wants[i];
                settled[i] = true;
                remaining -= wants[i];
                open--;
                progressed = true;
            }
            if (progressed) continue;

            for (var i = 0; i < wants.Count; i++)
                if (!settled[i]) shares[i] = remaining / open;
            break;
        }
        return shares;
    }

    /// <summary>Cut one block to its allowance at a line boundary, or return null when the
    /// allowance leaves no room for a body worth reading. The heading is never rendered without
    /// content under it.</summary>
    private static string? Fit(string block, int allowance)
    {
        if (block.Length <= allowance) return block;

        var tail = Environment.NewLine + "…" + Environment.NewLine + Environment.NewLine;
        var head = block.IndexOf('\n') + 1;
        var room = allowance - tail.Length;
        if (head <= 0 || room < head + MinBody) return null;

        var cut = block.LastIndexOf('\n', Math.Min(room, block.Length - 1));
        if (cut < head) cut = room;
        return block[..cut].TrimEnd() + tail;
    }

    /// <summary>The line that makes the cut visible. Names what was trimmed and what was dropped,
    /// falls back to counts when the names would not fit, and stays silent only when the budget is
    /// too small to hold even that.</summary>
    private static string Notice(IReadOnlyList<string> trimmed, IReadOnlyList<string> dropped, int reserve)
    {
        if (trimmed.Count == 0 && dropped.Count == 0) return "";

        // Dropped is named before trimmed: a section that is missing entirely is the more dangerous
        // of the two to mistake for "there was nothing to say".
        var parts = new List<string>();
        if (dropped.Count > 0) parts.Add("DROPPED ENTIRELY: " + string.Join(", ", dropped));
        if (trimmed.Count > 0) parts.Add("trimmed: " + string.Join(", ", trimmed));

        var full = "_(these context sections did not all fit the `batteries.maxBytes` budget — "
                 + string.Join("; ", parts)
                 + ". What is above is not the whole picture; raise `batteries.maxBytes` in the plan.)_";
        if (full.Length <= reserve) return full;

        var terse = FormattableString.Invariant(
            $"_({trimmed.Count} context section(s) trimmed, {dropped.Count} dropped — raise `batteries.maxBytes`.)_");
        return terse.Length <= reserve ? terse : "";
    }
}
