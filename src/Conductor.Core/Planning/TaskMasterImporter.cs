using System.Text.Json;

namespace Conductor.Core.Planning;

/// <summary>KS3.5 — the Task-Master bridge. <c>tasks.json</c> comes in three shapes in the wild: a
/// bare array of tasks, <c>{"tasks":[…]}</c>, and the tagged form
/// <c>{"master":{"tasks":[…],"metadata":{…}}}</c> — all three read here, with no model call.
/// <para>A top-level task becomes a STAGE (<c>T1</c>) and its subtasks become that stage's
/// checkpoints (<c>T1.1</c>); a task with no subtasks still gets one row (<c>T1.1</c>) carrying its
/// own title, because a stage with no checkpoint is a stage nothing can claim. The file's own
/// <c>dependencies</c> become <c>dependsOn</c> — this is the one source of the three that states its
/// ordering, so it is the one that does not get a linear chain invented for it.</para></summary>
public static class TaskMasterImporter
{
    /// <summary>Content detection: JSON that actually carries a task list. Cheap parse, no throw —
    /// a document that is not JSON is simply not this format.</summary>
    public static bool Looks(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return false;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return FindTasks(doc.RootElement) is not null;
        }
        catch (JsonException) { return false; }
    }

    public static ImportResult? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        JsonElement tasks;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException) { return null; }

        using (doc)
        {
            if (FindTasks(doc.RootElement) is not { } found) return null;
            tasks = found;

            var stages = new List<(string Id, string Title, List<ImportedCheckpoint> Rows)>();
            var deps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var ordinal = 0;
            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind != JsonValueKind.Object) continue;
                ordinal++;
                var stageId = "T" + Digits(Text(task, "id"), ordinal);
                if (stages.Exists(s => string.Equals(s.Id, stageId, StringComparison.Ordinal))) continue;
                var title = ImportBridge.CleanTitle(Text(task, "title") ?? Text(task, "description") ?? stageId);
                var rows = new List<ImportedCheckpoint>();

                var subs = task.TryGetProperty("subtasks", out var sub) && sub.ValueKind == JsonValueKind.Array
                    ? sub.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object).ToList()
                    : [];
                if (subs.Count == 0)
                {
                    rows.Add(new ImportedCheckpoint { Id = $"{stageId}.1", Title = title, Status = StatusOf(task) });
                }
                else
                {
                    var subOrdinal = 0;
                    foreach (var s in subs)
                    {
                        subOrdinal++;
                        var rowId = $"{stageId}.{Digits(Text(s, "id"), subOrdinal)}";
                        if (rows.Exists(r => string.Equals(r.Id, rowId, StringComparison.OrdinalIgnoreCase))) continue;
                        rows.Add(new ImportedCheckpoint
                        {
                            Id = rowId,
                            Title = ImportBridge.CleanTitle(Text(s, "title") ?? Text(s, "description") ?? rowId),
                            Status = StatusOf(s),
                        });
                    }
                }

                stages.Add((stageId, title, rows));
                if (task.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Array)
                {
                    var list = d.EnumerateArray()
                        .Select(x => "T" + Digits(x.ValueKind == JsonValueKind.Number
                            ? x.GetRawText() : x.ValueKind == JsonValueKind.String ? x.GetString() : null, 0))
                        .Where(x => x.Length > 1)
                        .ToList();
                    if (list.Count > 0) deps[stageId] = list;
                }
            }

            if (stages.Count == 0) return null;
            // Only stages this file actually declares can be depended on — a dangling id would make
            // the plan unschedulable, and PlanConfig would be right to refuse it.
            var known = stages.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            return ImportBridge.Build(stages, id => deps.TryGetValue(id, out var list)
                ? [.. list.Where(known.Contains)]
                : null);
        }
    }

    /// <summary>The task array, wherever this dialect put it: the root array, <c>tasks</c>, or the
    /// first tag object that has one (<c>master</c> preferred, as Task-Master's own default tag).</summary>
    private static JsonElement? FindTasks(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out _))
                ? root : null;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("tasks", out var direct) && direct.ValueKind == JsonValueKind.Array) return direct;
        if (root.TryGetProperty("master", out var master) && master.ValueKind == JsonValueKind.Object
            && master.TryGetProperty("tasks", out var mt) && mt.ValueKind == JsonValueKind.Array) return mt;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object
                && prop.Value.TryGetProperty("tasks", out var tagged) && tagged.ValueKind == JsonValueKind.Array)
                return tagged;
        }
        return null;
    }

    private static string? Text(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    /// <summary>Task-Master ids are numbers, strings, or dotted strings ("1.2"). An engine stage id
    /// may hold digits only, so take them — and fall back to the document ordinal when a task carries
    /// no usable id at all, which keeps the import deterministic instead of dropping work.</summary>
    private static string Digits(string? raw, int ordinal)
    {
        var digits = new string((raw ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length > 0 ? digits : ordinal > 0 ? ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    }

    /// <summary>A finished task imports as DONE so a re-import of a half-run board does not re-open
    /// work; everything else is left for the tracker's default (TODO).</summary>
    private static string? StatusOf(JsonElement task)
        => Text(task, "status") is { Length: > 0 } s
           && (s.Equals("done", StringComparison.OrdinalIgnoreCase) || s.Equals("completed", StringComparison.OrdinalIgnoreCase))
            ? "DONE" : null;
}
