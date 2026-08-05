using System.Text;

namespace Conductor.Core;

/// <summary>
/// SC2.4 — an incremental tail over an append-only text file.
///
/// <para>The three SSE streams re-read their ENTIRE backlog once a second and then threw almost all of
/// it away: <c>/transcript</c> deserialised every transcript line ever written, <c>/console</c> called
/// <c>File.ReadAllLinesAsync</c> on the whole session log. That is O(backlog) work per client per
/// second forever — on a long run the engine spends its idle time re-reading megabytes to discover it
/// has nothing new to send, and the cost grows with exactly the thing a long run accumulates.</para>
///
/// <para>This keeps a byte offset and hands back only whole lines appended past it. A trailing partial
/// line is left unconsumed until its newline arrives, so a reader can never see half a JSON object —
/// and because a newline byte cannot occur inside a UTF-8 multi-byte sequence, cutting the buffer at
/// one is always safe. The file is opened <c>FileShare.ReadWrite</c> (see
/// <see cref="SharedFileRead"/>) because the writer still holds it.</para>
/// </summary>
public sealed class FileLineTail
{
    /// <summary>Per-poll ceiling. A tail that has fallen a long way behind catches up over several
    /// polls instead of allocating the whole gap at once; the offset advances either way, so nothing
    /// is dropped. Sized so a reconnecting client's one-off backlog read is not chunked in practice —
    /// the buffer is allocated to the data ACTUALLY available, so a quiet tail allocates nothing.</summary>
    public const int MaxChunkBytes = 8 << 20;

    private const char Bom = '\uFEFF';

    private string? _path;
    private long _offset;

    /// <summary>The file currently followed, or null before the first <see cref="Follow"/>.</summary>
    public string? Path => _path;

    /// <summary>Bytes of complete lines already handed out. Exposed for tests and diagnostics — it is
    /// the number that must NOT go back to zero on a quiet poll.</summary>
    public long Offset => _offset;

    /// <summary>Point the tail at a file. Returns true when this is a DIFFERENT file from the one being
    /// followed, so the caller can reset whatever sequence numbering it derives from the lines (the
    /// console pane restarts line numbers when a new session's log appears).</summary>
    public bool Follow(string? path)
    {
        if (string.Equals(path, _path, StringComparison.Ordinal)) return false;
        _path = path;
        _offset = 0;
        return true;
    }

    /// <summary>Whatever complete lines have been appended since the last call. Empty — with no read
    /// past the file length — when nothing was appended, which is the common case and the whole point.
    /// A file that SHRANK (truncated or rotated in place) resets the tail to the top and replays it,
    /// because the alternative is silently skipping the new file's first N bytes.</summary>
    public IReadOnlyList<string> ReadAppended()
    {
        if (_path == null || !File.Exists(_path)) return [];
        FileStream? fs = null;
        try
        {
            try { fs = SharedFileRead.Open(_path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

            var length = fs.Length;
            if (length < _offset) _offset = 0;
            if (length <= _offset) return [];

            var want = (int)Math.Min(length - _offset, MaxChunkBytes);
            var buffer = new byte[want];
            fs.Seek(_offset, SeekOrigin.Begin);
            var read = 0;
            while (read < want)
            {
                var n = fs.Read(buffer, read, want - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return [];

            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline < 0)
            {
                // No complete line in this window. If the window is a FULL chunk with no newline the
                // file holds a single monstrous line; consume it rather than wedging the tail forever.
                if (read < MaxChunkBytes) return [];
                _offset += read;
                return [Decode(buffer, read, _offset == read)];
            }

            var atStart = _offset == 0;
            var text = Decode(buffer, lastNewline + 1, atStart);
            _offset += lastNewline + 1;

            var lines = text.Split('\n');
            var result = new List<string>(lines.Length);
            // Split on a string ending in '\n' leaves one trailing empty element that is not a line.
            for (var i = 0; i < lines.Length - 1; i++)
                result.Add(lines[i].TrimEnd('\r'));
            return result;
        }
        finally
        {
            fs?.Dispose();
        }
    }

    private static string Decode(byte[] buffer, int count, bool atStart)
    {
        var text = Encoding.UTF8.GetString(buffer, 0, count);
        // A UTF-8 BOM is bytes in the file but not a character in the first line.
        return atStart && text.Length > 0 && text[0] == Bom ? text[1..] : text;
    }
}
