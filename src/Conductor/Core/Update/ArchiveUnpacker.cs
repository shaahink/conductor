using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Conductor.Core.Update;

/// <summary>
/// SC8.3 — turning a downloaded release archive into two files on disk that have been proven to be
/// what they claim. Everything here is offline and deterministic, which is what makes the risky half
/// of <c>conductor update</c> testable without a network.
/// </summary>
public static class ArchiveUnpacker
{
    /// <summary>The checksum manifest the release workflow attaches. Releases published before SC8.3
    /// do not have one; that is a stated downgrade in the update output, never a silent skip.</summary>
    public const string ChecksumAssetName = "SHA256SUMS.txt";

    /// <summary>Lower-case hex SHA-256 of a file, streamed.</summary>
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Pulls one file's expected digest out of a <c>sha256sum</c>-format manifest
    /// (<c>&lt;hex&gt;  &lt;name&gt;</c>). Tolerates the <c>*</c> binary marker and a path prefix on
    /// the name, because both shapes turn up depending on which tool wrote the file.</summary>
    public static string? ExpectedSha(string? manifest, string assetName)
    {
        if (string.IsNullOrWhiteSpace(manifest)) return null;
        foreach (var raw in manifest.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var space = line.IndexOfAny([' ', '\t']);
            if (space <= 0) continue;
            var hex = line[..space].Trim();
            var name = line[(space + 1)..].Trim().TrimStart('*', ' ');
            name = name.Replace('\\', '/');
            var leaf = name[(name.LastIndexOf('/') + 1)..];
            if (string.Equals(leaf, assetName, StringComparison.OrdinalIgnoreCase) && LooksLikeSha256(hex))
                return hex.ToLowerInvariant();
        }
        return null;
    }

    private static bool LooksLikeSha256(string hex) => hex.Length == 64 && hex.All(Uri.IsHexDigit);

    /// <summary>Unpacks a <c>.zip</c> or <c>.tar.gz</c> into a fresh directory. Both BCL extractors
    /// refuse entries that escape the destination, so a hostile archive cannot write outside it.</summary>
    public static void Extract(string archivePath, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true);
            return;
        }
        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archivePath);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, destinationDir, overwriteFiles: true);
            return;
        }
        throw new InvalidOperationException(
            $"unsupported archive '{Path.GetFileName(archivePath)}' — expected .zip or .tar.gz");
    }

    /// <summary>Finds a named file anywhere under <paramref name="root"/>. The archives are flat, but
    /// <c>tar -C stage .</c> produces a leading <c>./</c> and a future layout change should not make
    /// the updater unable to find its own binary.</summary>
    public static string? Find(string root, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Restores the executable bit the zip/tar round trip may have dropped. A no-op on
    /// Windows, and never fatal: a file that cannot be chmod'd fails loudly at the exec-verify step
    /// with a far better message than a permissions exception here.</summary>
    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    /// <summary>A human-readable size, for the one line the operator reads while waiting.</summary>
    public static string Size(long bytes) => bytes >= 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.#} MB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB");
}
