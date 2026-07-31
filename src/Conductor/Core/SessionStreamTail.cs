using Conductor.Core.Providers;

namespace Conductor.Core;

/// <summary>
/// SC5.4: the readable tail of an agent's raw stream (<c>logs/session-NNN.jsonl</c>).
///
/// That file is provider NDJSON — one claude <c>stream-json</c> envelope per line, each carrying a
/// whole assistant message. Tailing it raw prints tens of kilobytes per line and is unusable as a
/// "what is this session doing right now" answer, which is the only reason anyone runs
/// <c>bg logs</c> on an agent pid.
///
/// So it is folded by the SAME <see cref="IAgentProvider"/> that parses the live session feed —
/// which means this view cannot drift from the Face's: a provider that learns a new envelope teaches
/// both at once. A line the provider yields nothing for still prints, truncated, so a format change
/// degrades to raw rather than to silence.
/// </summary>
public static class SessionStreamTail
{
    /// <summary>How many raw lines to fold to produce <c>tail</c> events. One envelope usually yields
    /// one or two events, so this is generous; it exists to bound the work on a session stream that
    /// can reach tens of megabytes, not to be exact.</summary>
    internal static int RawWindowFor(int tail) => Math.Max(200, tail * 4);

    /// <summary>Fold the end of <paramref name="streamPath"/> into at most <paramref name="tail"/>
    /// display lines, newest last.</summary>
    /// <param name="provider">The plan's own provider, so the vocabulary matches the live feed.</param>
    public static IReadOnlyList<string> Render(string streamPath, IAgentProvider provider, int tail)
    {
        ArgumentNullException.ThrowIfNull(provider);
        // The agent holds this file open for append while it runs — a share mode File.ReadAllLines
        // does not ask for (SC2.4 bug 1, the same trap `bg logs` already hit on bg-log files).
        // Streamed through a ring rather than materialised: one envelope here carries a whole
        // assistant message, so a long session's stream runs to tens of megabytes.
        var keep = RawWindowFor(tail);
        var window = new Queue<string>(keep);
        foreach (var line in SharedFileRead.ReadLines(streamPath))
        {
            window.Enqueue(line.Trim());
            if (window.Count > keep) window.Dequeue();
        }

        var folded = new List<string>();
        var state = new AgentStreamState((kind, text) => folded.Add($"{kind,-6} {Collapse(text)}"));
        foreach (var line in window)
        {
            if (line.Length == 0) continue;
            // AgentSession tees the child's stderr into the same file behind this marker; it is not
            // provider JSON and must not be parsed as any.
            if (line.StartsWith("[stderr]", StringComparison.Ordinal)) { folded.Add(Collapse(line)); continue; }
            var before = folded.Count;
            try { provider.ParseLine(line, state); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // A parser that throws on one envelope must not blank the whole tail.
                folded.Add(Collapse(line));
                continue;
            }
            if (folded.Count == before) folded.Add($"{"·",-6} {Collapse(line)}");
        }

        return folded.Count <= tail ? folded : folded.Skip(folded.Count - tail).ToList();
    }

    /// <summary>One display line: no embedded newlines, bounded width. The providers already truncate
    /// their own payloads; this is the backstop for the raw-line fallbacks.</summary>
    private static string Collapse(string s)
    {
        var one = s.ReplaceLineEndings(" ").Trim();
        return one.Length <= 220 ? one : one[..219] + "…";
    }
}
