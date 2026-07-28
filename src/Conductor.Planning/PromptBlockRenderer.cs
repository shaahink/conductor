using System.Text;

namespace Conductor.Planning;

/// <summary>
/// W2.3: the ONE place composed blocks become prompt text. The session prompt's task-scoped section
/// and <c>GET /prompt/blocks</c> both render through here, so "what the card detail shows" and "what
/// the agent receives" are the same bytes by construction rather than by two renderings agreeing.
/// </summary>
/// <remarks>
/// Before this, the card detail composed blocks (P3) while the session prompt built its own parallel
/// listing — the composition had exactly one consumer, the HTTP endpoint, and nothing tied it to the
/// prompt on disk. Criterion 3 ("the user can see the blocks a session prompt is built from, and what
/// the card detail shows IS what the agent receives") is only true if there is one renderer.
/// <para>Only the task-scoped (editable) blocks are rendered: persona, stage notes, injected knowledge
/// and the tool contract already reach the prompt through the template and the battery section, and
/// repeating them per card would say the same thing N times.</para>
/// </remarks>
public static class PromptBlockRenderer
{
    public const string SectionHeading = "## Work items in scope";

    private const string Preamble =
        "The open cards for the checkpoints this session claimed, exactly as their card detail shows\n" +
        "them. Deliver these; honour any context attached to them.";

    /// <summary>One card's task-scoped blocks — the exact lines the session prompt carries for it.</summary>
    public static string RenderCard(PromptComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var sb = new StringBuilder();
        var title = composition.Block(PromptBlockKind.TaskTitle)?.Content.Trim() ?? "";
        sb.Append("- **").Append(composition.TaskId);
        if (title.Length > 0) sb.Append(" — ").Append(title);
        sb.Append("**");

        var context = composition.Block(PromptBlockKind.TaskContext)?.Content;
        if (!string.IsNullOrWhiteSpace(context))
        {
            // Indented continuation lines keep a multi-line context attached to its bullet rather
            // than reading as new items.
            var normalised = context.Trim().ReplaceLineEndings("\n").Replace("\n", "\n  ", StringComparison.Ordinal);
            sb.Append("\n  ").Append(normalised);
        }
        return sb.ToString();
    }

    /// <summary>The whole task-scoped section for one session, or "" when nothing is in scope — a plan
    /// with no open cards keeps a prompt with no section, not an empty heading.</summary>
    public static string RenderSection(IEnumerable<PromptComposition> compositions)
    {
        ArgumentNullException.ThrowIfNull(compositions);
        var cards = compositions.Select(RenderCard).Where(c => c.Length > 0).ToList();
        if (cards.Count == 0) return "";
        return SectionHeading + "\n" + Preamble + "\n" + string.Join("\n", cards) + "\n";
    }
}
