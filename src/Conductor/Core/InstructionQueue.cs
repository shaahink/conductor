using System.Text;
using System.Text.Json;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// Read/write the `.conductor/queue/` instruction chain. Instructions are persisted as files so
/// they survive crashes; consumed files are renamed `.done` rather than deleted so the chain is
/// never silently broken. <c>--prev</c> / <c>--next</c> front-matter links instructions into an
/// ordered workflow for the agent.
/// </summary>
public static class InstructionQueue
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Dir(PlanConfig plan) => Path.Combine(plan.StateDir, "queue");

    public sealed record Entry(string File, string Slug, string Text, DateTime CreatedUtc, string? Prev, string? Next);

    /// <summary>Write a new instruction to the queue (creates the directory if missing).</summary>
    public static Entry Write(PlanConfig plan, string text, string? prev)
    {
        Directory.CreateDirectory(Dir(plan));
        var now = DateTime.UtcNow;
        var existing = List(plan);
        var num = existing.Count > 0 ? existing.Max(e => int.TryParse(e.File.Split('-')[0], out var n) ? n : 0) + 1 : 1;
        var slug = Sanitize(text).PadLeft(2, '0');
        var name = $"{num:000}-{slug}.json";
        var path = Path.Combine(Dir(plan), name);
        var entry = new Entry(name, slug, text, now, prev, null);
        // link previous entry forward
        if (prev != null)
        {
            var prevPath = Path.Combine(Dir(plan), prev);
            if (File.Exists(prevPath))
            {
                try
                {
                    var prevDoc = JsonDocument.Parse(File.ReadAllText(prevPath));
                    var prevRoot = prevDoc.RootElement;
                    var mutable = JsonSerializer.Deserialize<Dictionary<string, object>>(prevRoot.GetRawText(), Opts) ?? new(StringComparer.Ordinal);
                    mutable["next"] = name;
                    File.WriteAllText(prevPath, JsonSerializer.Serialize(mutable, Opts));
                }
                catch { /* best effort */ }
            }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new { text, createdUtc = now, prev, next = (string?)null }, Opts));
        return entry;
    }

    /// <summary>All active (not-yet-consumed) instructions, in creation order.</summary>
    public static List<Entry> List(PlanConfig plan)
    {
#pragma warning disable MA0045 // sync method — instruction queue is read synchronously from the control loop
        var dir = Dir(plan);
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "*.json")
            .Where(f => !f.EndsWith(".done.json", StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var r = doc.RootElement;
                    return new Entry(
                        Path.GetFileName(f),
                        Path.GetFileName(f).Split('-', 2).Last().Replace(".json", ""),
                        r.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                        r.TryGetProperty("createdUtc", out var c) ? c.GetDateTime() : DateTime.UtcNow,
                        r.TryGetProperty("prev", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                        r.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null);
                }
                // A malformed/locked queue entry is skipped (→ null, filtered below) rather than
                // breaking the whole queue read; genuine programmer errors still propagate.
                catch (Exception ex) when (ex is IOException or JsonException) { return null; }
            })
            .Where(e => e != null)
            .OrderBy(e => e!.File, StringComparer.Ordinal)
            .Select(e => e!)
            .ToList();
#pragma warning restore MA0045
    }

    /// <summary>Mark all currently-active instructions as consumed (rename to .done). Call after a session prompt consumes them.</summary>
    public static void ConsumeAll(PlanConfig plan)
    {
        var dir = Dir(plan);
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            if (f.EndsWith(".done.json", StringComparison.OrdinalIgnoreCase)) continue;
            var done = f.Replace(".json", ".done.json");
            // Best-effort consume: if the rename races another mark (already .done) it is a no-op, so
            // an instruction is never re-injected — the chain stays intact either way.
            try { File.Move(f, done); } catch (IOException) { /* already renamed/locked — safe to skip */ }
        }
    }

    /// <summary>Render active instructions into a prompt section (or empty string if none).
    /// The agent's pre-ritual prompt instructs it to check this — here we guarantee the text is in the prompt.
    /// <para>SC4.4: the header states the RANK, not just the content. Position alone was not enough —
    /// an injection can only outrank the evidence it corrects if the prompt says so out loud.</para></summary>
    public static string PromptSection(PlanConfig plan)
    {
        var items = List(plan);
        if (items.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("📋 **QUEUED INSTRUCTIONS** — human-injected, and they OUTRANK everything below them in this prompt (gate output, verifier findings, batteries, prior handoff). Where this section and a block below disagree, this section wins. Consume in order:");
        sb.AppendLine();
        foreach (var (i, item) in items.Select((e, i) => (i, e)))
            sb.AppendLine($"{i + 1}. [{item.Slug}] {item.Text}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>SC4.4: the banner stamped onto a fix prompt's <c>gateFailures</c> block when instructions
    /// are queued for that session, so the human correction and the engine's stale evidence never stand
    /// as peers (devcontext #15: the correction rendered 113 lines BELOW the evidence it corrected, and
    /// the agent worked the evidence). Nothing is deleted — the gate output stays readable as history.</summary>
    public static string SupersedeStamp(int queuedCount)
    {
        var plural = queuedCount == 1 ? "instruction is" : "instructions are";
        return $"> ⚠ **SUPERSEDED — read the QUEUED INSTRUCTIONS at the top of this prompt first.** {queuedCount} human {plural} queued for this session and they outrank this gate output: where the two disagree, the instructions win and the block below is history, not your task list.";
    }

    private static string Sanitize(string text)
    {
        var words = text.Trim().Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Take(5)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0);
        var slug = string.Join("-", words);
        return slug.Length == 0 ? "note" : slug.ToLowerInvariant();
    }
}
