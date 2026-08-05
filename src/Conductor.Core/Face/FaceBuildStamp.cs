using System.Globalization;
using System.Text;

namespace Conductor.Core.Face;

/// <summary>
/// FU-OWNER-10 / SF3.3 — <b>which Face binary is this engine attached to?</b>
/// <para>The follow-up this answers, verbatim from the dogfooding round: proving a run was on the
/// newly installed build took four out-of-band checks — <c>Get-CimInstance Win32_Process</c> for the
/// image path, the file's mtime against the run's start, <c>conductor version</c>, and
/// <c>go version -m</c> on the Face binary. The engine's own stamp was always one property away
/// (<see cref="BuildInfo"/>); the Face's was not, because nothing in this process knows what a Go
/// binary was built from.</para>
/// <para><b>How it is read.</b> The Go toolchain embeds its build settings into the executable as
/// plain ASCII — a build from a git checkout carries <c>vcs.revision=&lt;40 hex&gt;</c>,
/// <c>vcs.time=&lt;RFC3339&gt;</c> and <c>vcs.modified=true|false</c>. That is measured, not assumed:
/// this repo's own <c>face-go/bin/conductor-face.exe</c> carries all three. So the stamp is a byte
/// scan for those markers, not a shell-out to a Go toolchain the operator's machine may not have.</para>
/// <para><b>What it will never do is invent one.</b> A binary with no VCS stamp (built from a source
/// archive, or with <c>-buildvcs=false</c>) reports its file date instead, said in words — never a
/// commit sha it did not read. The whole point of the field is to end a guess; a guessed value would
/// be strictly worse than the empty string.</para>
/// </summary>
public static class FaceBuildStamp
{
    /// <summary>Only the head of the file is scanned. Go's build-info blob lives in a read-only data
    /// section, well inside this window for a binary of the Face's size (~22 MB, stamp at ~10.5 MB),
    /// and an unbounded scan of an arbitrary path on a polled endpoint is not something to hand a
    /// caller. Larger than the whole binary is fine — the read is clamped to the file length.</summary>
    private const int ScanBytes = 48 * 1024 * 1024;

    private static readonly Lock Gate = new();
    private static (string Path, DateTime Written, long Length, string Stamp)? _cached;

    /// <summary>The short build identity of the Face binary this engine would launch, or "" when
    /// there is no built Face to find at all (in which case the Face is not what is on screen and
    /// the question does not arise). Cached against the file's path, size and write time, so a
    /// reinstall is picked up without re-scanning 10 MB on every poll.</summary>
    public static string Current()
    {
        var path = FaceLauncher.ResolveEntrypoint();
        if (path is null || !File.Exists(path)) return "";

        var info = new FileInfo(path);
        lock (Gate)
        {
            if (_cached is { } c && c.Path == path && c.Written == info.LastWriteTimeUtc && c.Length == info.Length)
                return c.Stamp;
            var stamp = Describe(path, info);
            _cached = (path, info.LastWriteTimeUtc, info.Length, stamp);
            return stamp;
        }
    }

    /// <summary>Drops the cached reading — for tests, and for anything that has just replaced the
    /// binary underneath a long-lived engine.</summary>
    public static void Clear()
    {
        lock (Gate) _cached = null;
    }

    /// <summary>The stamp for one file, uncached. Public so a test can point it at a synthesised
    /// binary rather than at whatever happens to be built on the machine running the suite.</summary>
    public static string Describe(string path, FileInfo? info = null)
    {
        info ??= new FileInfo(path);
        var (revision, modified) = ReadVcs(path);
        if (revision.Length == 0)
            return $"unstamped (built {info.LastWriteTimeUtc:yyyy-MM-dd HH:mm}Z)";
        var shortSha = revision.Length >= 12 ? revision[..12] : revision;
        return modified ? shortSha + ".dirty" : shortSha;
    }

    /// <summary>Scans <paramref name="path"/> for Go's embedded <c>vcs.revision</c> / <c>vcs.modified</c>
    /// settings. Returns ("", false) when the markers are absent or malformed — an unreadable file is
    /// an unstamped one, never an exception on a polled endpoint.</summary>
    internal static (string Revision, bool Modified) ReadVcs(string path)
    {
        byte[] buf;
        try
        {
            // A SafeFileHandle rather than a FileStream on purpose: this method is synchronous by
            // design (it is behind a cache, on a polled path), and a FileStream in scope makes the
            // analyzers — rightly — insist the whole call chain go async for a read that is a
            // memory copy in practice. FileShare.ReadWrite|Delete so a reinstall replacing the
            // binary underneath us fails to nothing worse than an unstamped reading.
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var len = (int)Math.Min(RandomAccess.GetLength(handle), ScanBytes);
            buf = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = RandomAccess.Read(handle, buf.AsSpan(read, len - read), read);
                if (n <= 0) break;
                read += n;
            }
            if (read < len) Array.Resize(ref buf, read);
        }
        catch (IOException) { return ("", false); }
        catch (UnauthorizedAccessException) { return ("", false); }

        var text = Encoding.Latin1.GetString(buf);
        var revision = ValueAfter(text, "vcs.revision=");
        // Only hex is accepted: the marker string could in principle appear in unrelated data, and a
        // non-sha "revision" on screen would be exactly the invented answer this class refuses to give.
        if (revision.Length is < 7 or > 64 || !revision.All(Uri.IsHexDigit)) return ("", false);
        var modified = ValueAfter(text, "vcs.modified=").Equals("true", StringComparison.OrdinalIgnoreCase);
        return (revision, modified);
    }

    /// <summary>The value following <paramref name="marker"/>, up to the first byte Go uses to
    /// delimit build settings (tab, newline, NUL) or any other non-printable.</summary>
    private static string ValueAfter(string haystack, string marker)
    {
        var i = haystack.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return "";
        var start = i + marker.Length;
        var end = start;
        while (end < haystack.Length && haystack[end] is > ' ' and < (char)127) end++;
        return haystack[start..end];
    }

    /// <summary>A one-line "which builds am I looking at" for a status strip: the engine's version
    /// and commit, and the Face's stamp when there is one. Formatted here rather than in the Face so
    /// the CLI, the wire and the TUI cannot drift into three spellings of the same fact.</summary>
    public static string Line(string engineVersion, string engineCommit, string faceBuild)
    {
        var sb = new StringBuilder("engine ").Append(engineVersion);
        if (!string.IsNullOrWhiteSpace(engineCommit) && engineCommit != BuildInfo.UnknownCommit)
            sb.Append('+').Append(engineCommit.Length >= 12 ? engineCommit[..12] : engineCommit);
        if (!string.IsNullOrWhiteSpace(faceBuild)) sb.Append(CultureInfo.InvariantCulture, $" · face {faceBuild}");
        return sb.ToString();
    }
}
