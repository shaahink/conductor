using System.Text;

namespace Conductor.Core;

/// <summary>
/// Manages a bounded, rotating lessons file at <c>.conductor/lessons.md</c>.
/// Entries are appended newest-first; when the file exceeds <see cref="_maxBytes"/>,
/// the oldest entries are evicted. Designed for the reflection step (B8.1):
/// at session end, "what was hard" is distilled into a brief entry.
/// </summary>
public sealed class LessonsManager
{
    private const string FileName = "lessons.md";
    private const string HeaderLine = "# Lessons learned (auto-rotating, newest first)";
    private const string UpdatedPrefix = "> **Last updated:** ";
    private const string EntrySeparator = "---";
    private readonly string _dirPath;
    private readonly int _maxBytes;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    public LessonsManager(string conductorDir, int maxBytes = 8192, TimeProvider? time = null)
    {
        _dirPath = conductorDir;
        _maxBytes = Math.Max(1024, maxBytes);
        _time = time ?? TimeProvider.System;
    }

    private string FilePath => Path.Combine(_dirPath, FileName);

    /// <summary>Appends a lesson entry and evicts oldest if the file exceeds the byte cap. Thread-safe.</summary>
    public void Append(string stage, int session, string difficultyText)
    {
        var entryDate = _time.GetUtcNow();
        var entry = FormatEntry(stage, session, difficultyText, entryDate);

        lock (_gate)
        {
            Directory.CreateDirectory(_dirPath);
            var existing = File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8) : "";

            // New entry always goes first (newest-first ordering)
            var body = StripHeader(existing);
            var content = $"{HeaderLine}\n\n{UpdatedPrefix}{entryDate:o}\n\n{entry}";

            if (body.Length > 0)
                content += "\n" + body.TrimEnd();

            // Evict oldest entries if over the byte cap
            if (Encoding.UTF8.GetByteCount(content) > _maxBytes)
                content = TrimToCap(content, entry, entryDate);

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, content, Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    /// <summary>Returns the full content of lessons.md, or empty string if none exists.</summary>
    public string ReadContent()
    {
        if (!File.Exists(FilePath)) return "";
        try
        {
            var content = File.ReadAllText(FilePath, Encoding.UTF8);
            return content.Length > 0 ? content : "";
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    /// <summary>Returns up to N most recent entries as a rendered string for prompt injection.</summary>
    public string ReadRecent(int maxEntries)
    {
        var content = ReadContent();
        if (string.IsNullOrEmpty(content)) return "";

        var entries = ParseEntries(StripHeader(content));
        if (entries.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("Recent lessons from past sessions:");
        foreach (var e in entries.Take(maxEntries))
            sb.AppendLine(e.TrimEnd());
        return sb.ToString().TrimEnd();
    }

    /// <summary>Count of current entries (for testing).</summary>
    internal int EntryCount()
    {
        var content = ReadContent();
        return string.IsNullOrEmpty(content) ? 0 : ParseEntries(StripHeader(content)).Count;
    }

    private static string FormatEntry(string stage, int session, string text, DateTimeOffset date)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries);
        var sb = new StringBuilder();
        sb.AppendLine($"## {stage}-{session} — {date:yyyy-MM-dd HH:mm} UTC");
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) sb.AppendLine(trimmed);
        }
        sb.AppendLine();
        sb.Append(EntrySeparator);
        return sb.ToString().TrimEnd();
    }

    private static string StripHeader(string content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        var lines = content.Split('\n');
        var start = 0;
        for (var i = 0; i < lines.Length && i < 6; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("# ", StringComparison.Ordinal)
                || line.StartsWith("> **", StringComparison.Ordinal)
                || line.Length == 0)
            {
                start = i + 1;
            }
            else
                break;
        }
        if (start >= lines.Length) return "";
        return string.Join("\n", lines.Skip(start)).TrimEnd() + "\n";
    }

    private string TrimToCap(string content, string newestEntry, DateTimeOffset date)
    {
        var body = StripHeader(content);
        var allEntries = ParseEntries(body);
        if (allEntries.Count == 0) return content;

        var sb = new StringBuilder();
        sb.AppendLine(HeaderLine);
        sb.AppendLine();
        sb.Append(UpdatedPrefix);
        sb.Append(date.ToString("o"));
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine(newestEntry);

        foreach (var entry in allEntries)
        {
            var candidate = sb.ToString() + "\n" + entry.TrimEnd() + "\n";
            if (Encoding.UTF8.GetByteCount(candidate) > _maxBytes) break;
            sb.AppendLine();
            sb.AppendLine(entry.TrimEnd());
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static List<string> ParseEntries(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new List<string>();

        var entries = new List<string>();
        var segments = body.Split(new[] { $"\n{EntrySeparator}\n", $"\n{EntrySeparator}" },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var s in segments)
        {
            var trimmed = s.Trim();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                entries.Add(trimmed);
        }

        return entries;
    }
}
