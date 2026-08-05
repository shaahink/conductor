using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Conductor.Core.Evidence;

/// <summary>
/// K5.3 — evidence as a thing the engine knows about, rather than a free-text field.
/// <para>Before this, <c>conductor task --done &lt;id&gt; --evidence &lt;string&gt;</c> stored a
/// string and <c>AuditCommand</c> scanned two directories for <c>*.txt</c> at replay time. That was
/// the whole of it: no event, no registry, no kinds, and nothing that could notice a PNG. The case
/// that motivated this is the owner's: conductor builds a website, the agent screenshots it, and a
/// SECOND agent had to be hired to notice the images and forward them.</para>
/// <para>The free-text field still works and is still stored on the claim. An artifact is what the
/// engine learns IN ADDITION, when that string — or a file appearing in a watched directory — turns
/// out to be a real file on disk.</para>
/// </summary>
/// <param name="Path">Repo-relative when the file is inside the repo, else absolute. Forward slashes.</param>
/// <param name="Kind">See <see cref="EvidenceKinds"/> — image and video are first-class, not "other".</param>
/// <param name="CheckpointId">The checkpoint this evidences, when it can be established.</param>
/// <param name="StageId">The owning stage, usually the directory the artifact sits in.</param>
/// <param name="SessionNumber">The session that produced it, when known.</param>
/// <param name="Sha256">Content hash — the identity of the bytes, so a re-registration is not a duplicate
/// and an edited file is honestly a new artifact.</param>
/// <param name="Bytes">Size on disk, for a surface that has to decide whether to send it.</param>
/// <param name="CreatedUtc">When the engine first saw it.</param>
/// <param name="Source">claim | watcher — how the engine came to know.</param>
public sealed record EvidenceArtifact(
    string Path,
    string Kind,
    string? CheckpointId,
    string? StageId,
    int? SessionNumber,
    string Sha256,
    long Bytes,
    DateTimeOffset CreatedUtc,
    string Source)
{
    /// <summary>Identity of an artifact: the bytes at a location. Two claims naming the same
    /// unchanged file are one artifact; the same path with different bytes is a new one.</summary>
    public string Key => Path + "@" + Sha256;
}

/// <summary>The kind vocabulary. Non-text kinds are first-class here because a screenshot is the case
/// K5.3 exists for — "text or other" would have made the motivating case the fallback.</summary>
public static class EvidenceKinds
{
    public const string Image = "image";
    public const string Video = "video";
    public const string Audio = "audio";
    public const string Text = "text";
    public const string Data = "data";
    public const string Archive = "archive";
    public const string Binary = "binary";

    public static string FromPath(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".tiff" => Image,
            ".mp4" or ".mov" or ".webm" or ".mkv" or ".avi" => Video,
            ".mp3" or ".wav" or ".m4a" or ".ogg" or ".flac" => Audio,
            ".md" or ".txt" or ".log" or ".diff" or ".patch" or "" => Text,
            ".json" or ".csv" or ".tsv" or ".xml" or ".yaml" or ".yml" or ".html" or ".htm" => Data,
            ".zip" or ".tar" or ".gz" or ".7z" or ".rar" => Archive,
            _ => Binary,
        };
    }

    /// <summary>True for kinds a chat can render inline rather than as a file — K5.4's photo path.</summary>
    public static bool IsVisual(string kind) =>
        string.Equals(kind, Image, StringComparison.Ordinal) ||
        string.Equals(kind, Video, StringComparison.Ordinal);
}

/// <summary>Reads a file into an artifact. Static and total: an unreadable or vanished file yields
/// null rather than throwing, because evidence registration must never be able to fail a claim.</summary>
public static class EvidenceReader
{
    /// <summary>A checkpoint id at the head of a file name — <c>K5.1-result-contract.md</c>. The
    /// convention this repo's own evidence directory has used since Sarban, so the id is recovered
    /// rather than demanded.</summary>
    /// <para>NAMED group, and it has to be: <see cref="RegexOptions.ExplicitCapture"/> switches
    /// unnamed groups off, so the original numbered form matched every file name and then handed back
    /// an empty string for all of them — the id was silently never recovered.</para>
    private static readonly Regex LeadingCheckpointId = new(
        @"^(?<id>[A-Za-z]{1,4}\d{1,3}[._]\d{1,3})",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    public static async Task<EvidenceArtifact?> ReadAsync(string fullPath, string repoRoot,
        string? checkpointId, int? sessionNumber, string source, TimeProvider? time = null,
        CancellationToken ct = default)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return null;

            var now = (time ?? TimeProvider.System).GetUtcNow();
            var rel = Relative(info.FullName, repoRoot);
            return new EvidenceArtifact(
                rel,
                EvidenceKinds.FromPath(info.Name),
                checkpointId ?? InferCheckpoint(info.Name),
                InferStage(rel),
                sessionNumber,
                await Sha256OfAsync(info, ct).ConfigureAwait(false),
                info.Length,
                now,
                source);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>Does this free-text evidence string point at a file that exists? The string is a
    /// claim's own words — it may be a path, a sentence, or a gate summary — so this answers without
    /// complaining, and a "no" leaves the claim exactly as it was.</summary>
    public static string? ResolvePath(string? evidence, string repoRoot, string stateDir)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return null;
        var text = evidence.Trim().Trim('`', '"', '\'');
        // A sentence, not a path. Cheap rejection before any filesystem call.
        if (text.Length > 400 || text.Contains('\n', StringComparison.Ordinal)) return null;

        foreach (var candidate in Candidates(text, repoRoot, stateDir))
        {
            try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string text, string repoRoot, string stateDir)
    {
        if (Path.IsPathRooted(text)) { yield return text; yield break; }
        yield return Path.Combine(repoRoot, text);
        yield return Path.Combine(stateDir, text);
    }

    internal static string Relative(string fullPath, string repoRoot)
    {
        try
        {
            var rel = Path.GetRelativePath(repoRoot, fullPath);
            var normalized = rel.Replace('\\', '/');
            return normalized.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel)
                ? fullPath.Replace('\\', '/')
                : normalized;
        }
        catch (ArgumentException) { return fullPath.Replace('\\', '/'); }
    }

    internal static string? InferCheckpoint(string fileName)
    {
        var m = LeadingCheckpointId.Match(fileName);
        return m.Success ? m.Groups["id"].Value.Replace('_', '.') : null;
    }

    /// <summary>The directory an artifact sits in is the stage, by the convention every evidence
    /// path in this repo already follows: <c>.conductor/evidence/&lt;stage&gt;/&lt;file&gt;</c>.</summary>
    internal static string? InferStage(string relativePath)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var dir = parts[^2];
        return dir.Length is > 0 and <= 8 && char.IsLetter(dir[0]) && dir.Any(char.IsDigit) ? dir : null;
    }

    /// <summary>Content identity. Read whole and hashed: an evidence artifact is a screenshot or a
    /// markdown file, and this runs once per artifact at a session boundary.
    /// <para>Over <see cref="MaxHashBytes"/> the file is identified by its size and last-write time
    /// instead, prefixed so the two can never be confused. Pulling a 2 GB recording into memory to
    /// decide whether a notification has already announced it is not a trade worth making.</para></summary>
    private static async Task<string> Sha256OfAsync(FileInfo info, CancellationToken ct)
    {
        if (info.Length > MaxHashBytes)
            return FormattableString.Invariant($"size-{info.Length}-{info.LastWriteTimeUtc.Ticks}");
        var bytes = await File.ReadAllBytesAsync(info.FullName, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>64 MB — above this, identity falls back to size and mtime.</summary>
    public const long MaxHashBytes = 64L * 1024 * 1024;
}
