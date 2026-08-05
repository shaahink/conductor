using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Core.Store;

/// <summary>
/// K3.1: <c>&lt;repo&gt;/.conductor/state-pointer.json</c> — the one thing the working tree still
/// says about the store, now that the store left it. Two jobs: it tells a human (and a
/// <c>doctor</c>) where this tree's history actually went, and it lets a SECOND working tree point
/// at the SAME run. The lanes plan turns on that second job — a lane worktree has its own repo
/// path, so it would otherwise derive its own slug and its own empty history.
/// <para>Machine-local by design: the path inside it is absolute and points under
/// <c>%LOCALAPPDATA%</c>, so it stays untracked (<c>.conductor/.gitignore</c> is a bare <c>*</c>
/// with an allowlist, and this file is not on it).</para>
/// </summary>
public sealed class StatePointer
{
    public const string SchemaNote =
        "K3.1 state pointer. runDb is the absolute path to this tree's run.db. Delete this file to fall back to the derived path.";

    [JsonPropertyName("runDb")] public string RunDb { get; set; } = "";
    [JsonPropertyName("plan")] public string? Plan { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>The pointed-at database path, or null when there is no pointer, it is unreadable, or
    /// it names nothing. Never throws: a corrupt pointer must degrade to the derived path, not take
    /// the CLI down.</summary>
    public static string? TryRead(string pointerPath)
    {
        try
        {
            if (!File.Exists(pointerPath)) return null;
            var p = JsonSerializer.Deserialize<StatePointer>(File.ReadAllText(pointerPath), Opts);
            return string.IsNullOrWhiteSpace(p?.RunDb) ? null : Path.GetFullPath(p.RunDb);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes (or refreshes) the pointer. Best-effort: a read-only working tree is not a
    /// reason to fail a command.</summary>
    public static bool TryWrite(string pointerPath, string runDb, string? plan = null, string? note = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);
            File.WriteAllText(pointerPath, JsonSerializer.Serialize(
                new StatePointer { RunDb = runDb, Plan = plan, Note = note ?? SchemaNote }, Opts));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
