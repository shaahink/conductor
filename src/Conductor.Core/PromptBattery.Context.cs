using System.Globalization;
using System.Text;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// KS7.5 — a bounded map of the repo, so a session's first three turns are not an inventory.
/// </summary>
/// <remarks>
/// <para><b>Stated honestly, because the measurement says so.</b> A composed prompt in this repo is
/// 4.4k–6.6k tokens against a 135k–195k mean turn — 3–4% of it. This battery makes the prompt BIGGER.
/// It can only pay for itself by preventing exploration turns, and an exploration turn that reads ten
/// directory listings costs far more than the ~300 bytes below. That is the bet; it is a bet about
/// session behaviour, not an arithmetic saving, and nobody should claim it as one.</para>
/// <para>So the map is deliberately the smallest thing that answers "where does code live here" —
/// top-level source directories with file counts, and where tests are. Not a file tree: a file tree
/// of this repo is 900 entries and would be the worst of both worlds, paid on every turn AND too
/// coarse to answer a real question.</para>
/// <para>Deterministic given the tree, and it never walks into <c>.git</c>, <c>bin</c>, <c>obj</c> or
/// <c>node_modules</c> — a battery that takes 4 seconds to render has already lost the argument.</para>
/// </remarks>
public sealed class RepoMapBattery : IPromptBattery
{
    private static readonly string[] Skip = [".git", "bin", "obj", "node_modules", ".vs", "dist", "target"];

    private readonly string _section;

    public RepoMapBattery(string repoRoot, int maxEntries = 12)
    {
        _section = Build(repoRoot, maxEntries);
    }

    public string Name => "repo-map";

    public string Section => _section;

    public bool IsEmpty => _section.Length == 0;

    private static string Build(string repoRoot, int maxEntries)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return "";

        try
        {
            var rows = new List<(string Dir, int Files)>();
            foreach (var dir in Directory.EnumerateDirectories(repoRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || Skip.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                var files = CountSources(dir);
                if (files > 0) rows.Add((name, files));
            }

            if (rows.Count == 0) return "";

            var sb = new StringBuilder();
            foreach (var (dir, files) in rows.OrderByDescending(r => r.Files).Take(maxEntries))
                sb.AppendLine($"- `{dir}/` — {files.ToString(CultureInfo.InvariantCulture)} source files");

            sb.Append("Search inside these rather than enumerating the tree; delegate a wide sweep to a subagent.");
            return sb.ToString();
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    /// <summary>Source files under a directory, skipping build output. Capped: the count is a sense of
    /// scale, and walking a 40,000-file <c>node_modules</c> to say "big" helps nobody.</summary>
    private static int CountSources(string dir)
    {
        var count = 0;
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0 && count < 5000)
        {
            var current = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || Skip.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push(sub);
            }

            count += Directory.EnumerateFiles(current).Count();
        }

        return count;
    }
}

/// <summary>
/// KS7.5 — what "done" means for the checkpoint in flight, recapped from the board rather than from
/// the agent's memory of the ritual.
/// </summary>
/// <remarks>
/// This one is not a bet. Sessions in this project have repeatedly written DONE in prose, filled the
/// tracker's checkpoint table, or described the work in a result block — none of which moves anything,
/// because the tracker's rows are a GENERATED VIEW of the database and the claim verb is the only
/// channel. The failure is not ignorance of the rule; the rule is in the prompt. It is that by the
/// time a session closes, the rule is thousands of turns back in its context and the acceptance
/// criteria are further back still. So this battery restates both, from the live board, in the same
/// place every session — with the exact command, pre-filled with the id.
/// </remarks>
public sealed class DefinitionOfDoneBattery : IPromptBattery
{
    private readonly string _section;

    public DefinitionOfDoneBattery(IReadOnlyList<TaskItem> checkpoints, string stageId, int maxBytes = 500)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        _section = Build(checkpoints, stageId, maxBytes);
    }

    public string Name => "definition-of-done";

    public string Section => _section;

    public bool IsEmpty => _section.Length == 0;

    private static string Build(IReadOnlyList<TaskItem> checkpoints, string stageId, int maxBytes)
    {
        // The card in flight, or the next one waiting. An in-progress card wins: it is what this
        // session is already holding.
        var active = checkpoints.FirstOrDefault(c =>
                         string.Equals(c.StageId, stageId, StringComparison.Ordinal) &&
                         string.Equals(c.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                     ?? checkpoints.FirstOrDefault(c =>
                         string.Equals(c.StageId, stageId, StringComparison.Ordinal) &&
                         string.Equals(c.Status, "todo", StringComparison.OrdinalIgnoreCase));
        if (active is null) return "";

        var sb = new StringBuilder();
        sb.AppendLine($"`{active.TaskId}` is what this session closes. It is done when:");
        var acceptance = string.IsNullOrWhiteSpace(active.Context) ? active.Title : active.Context;
        sb.AppendLine(Trim(acceptance, maxBytes - 200));
        sb.AppendLine($"Claim it with `conductor task --done {active.TaskId} --evidence <path>` — that command IS the claim.");
        sb.Append("Prose in the handoff, a filled tracker row and a SESSION-RESULT move nothing.");

        var s = sb.ToString();
        return s.Length > maxBytes ? s[..maxBytes].TrimEnd() + "…" : s;
    }

    private static string Trim(string s, int max)
    {
        var flat = s.Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return flat.Length > max && max > 0 ? flat[..max].TrimEnd() + "…" : flat;
    }
}
