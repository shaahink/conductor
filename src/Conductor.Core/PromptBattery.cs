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
/// name as a header. The total output is bounded to <see cref="_maxBytes"/> so no
/// single battery can dominate the prompt (B8.5 trap).
/// </summary>
public sealed class BatteryGroup
{
    private readonly List<IPromptBattery> _batteries;
    private readonly int _maxBytes;

    public static BatteryGroup Empty { get; } = new(Array.Empty<IPromptBattery>());

    public BatteryGroup(IEnumerable<IPromptBattery> batteries, int maxBytes = 2048)
    {
        _batteries = batteries.ToList();
        _maxBytes = maxBytes;
    }

    /// <summary>Rendered prompt section combining all non-empty batteries, or empty string.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var b in _batteries)
        {
            if (b.IsEmpty) continue;
            sb.AppendLine($"### {b.Name}");
            sb.AppendLine(b.Section);
            sb.AppendLine();
        }
        if (sb.Length == 0) return "";

        var result = sb.ToString().TrimEnd();
        if (result.Length > _maxBytes)
        {
            // Truncate at nearest paragraph boundary within budget
            var cutoff = _maxBytes;
            while (cutoff > 0 && result[cutoff] != '\n') cutoff--;
            result = cutoff > 100 ? result[..cutoff].TrimEnd() + "\n…" : result[.._maxBytes].TrimEnd() + "…";
        }
        return result;
    }

    public bool IsEmpty => _batteries.All(b => b.IsEmpty);
}
