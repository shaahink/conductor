using System.Text;

namespace Conductor.Core;

/// <summary>PK4.2 / D7 - the words a claim carried for the room that the room has NOT heard, because the
/// verdict has not confirmed the claim: gates red, or verification still to come. The session that
/// wrote them is gone; this is how the next one learns they are held rather than lost, and that posting
/// them by hand would be exactly the claim-time card D7 retired.</summary>
public sealed class HeldWordsBattery : IPromptBattery
{
    private readonly string? _section;

    public HeldWordsBattery(IReadOnlyList<Models.TaskItem> checkpoints, int maxBytes = 900)
    {
        var held = checkpoints.Where(c => c.Status == "done" && !c.Confirmed && c.Tell.Length > 0).ToList();
        if (held.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("Words for the room are HELD - claimed with --tell, not yet confirmed, so no card has been posted:");
        foreach (var c in held) sb.AppendLine($"- {c.CheckpointId}: {c.Tell}");
        sb.Append("The engine posts each card when the verdict confirms its claim. Do not post them yourself; "
            + "a re-claim with --tell replaces the words.");
        var s = sb.ToString();
        _section = s.Length > maxBytes ? s[..maxBytes] + "…" : s;
    }

    public string Name => "held-words";
    public string Section => _section ?? "";
    public bool IsEmpty => _section is null;
}
