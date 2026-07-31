using System.Text;

namespace Conductor.Core;

/// <summary>
/// SC2.4 — reading a file somebody else is still writing.
///
/// <para>Every log this program asks an operator to read is LIVE: <c>conductor.log</c> and the rolling
/// <c>conductor-*.json</c> are held open by the running engine, and a <c>bg</c> child's log is held
/// open by the shell redirecting into it. <c>File.ReadAllLines</c> / <c>File.ReadLines</c> open with
/// <c>FileShare.Read</c>, and on Windows a share mode must also permit the access the EXISTING handle
/// holds — the writer holds Write, which <c>FileShare.Read</c> does not permit. So the read fails with
/// a sharing violation for exactly the case the command exists to serve: <c>conductor log</c> threw,
/// and <c>conductor bg logs</c> printed "Cannot read log ... being used by another process" (bug 1).</para>
///
/// <para><c>FileShare.ReadWrite</c> is the fix and the whole fix: it tells Windows this reader tolerates
/// a concurrent writer. It cannot tear a line — the log writers here append whole lines and flush —
/// and a torn trailing line is already tolerated by every caller.</para>
/// </summary>
public static class SharedFileRead
{
    /// <summary>Reads every line of a file that may be open for writing elsewhere. Streams rather than
    /// slurping so a multi-megabyte rolling log does not materialise twice.</summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        using var fs = Open(path);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return line;
    }

    /// <summary>Materialised form of <see cref="ReadLines"/>, for callers that need a count or an index.</summary>
    public static IReadOnlyList<string> ReadAllLines(string path) => ReadLines(path).ToList();

    /// <summary>The shared handle itself, for callers that seek (the incremental SSE tails).</summary>
    public static FileStream Open(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
}
