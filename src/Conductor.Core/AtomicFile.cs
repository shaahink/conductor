using System.Text;

namespace Conductor.Core;

/// <summary>Write a small file so a reader never sees half of it: temp file, then rename over the
/// target.
///
/// <para>Lifted out of the inbox at DV4 (repair), where it had grown into the engine's de-facto
/// atomic writer while still living on a store whose subject is notes. Four unrelated records — the
/// courier's durable offset, its presence claim, its settings, and the dead-letter box — were
/// reaching into a note store for a file primitive, which is the sort of coupling
/// <c>DV3_3TranscriptionTests.Prune_is_the_only_code_in_the_engine_that_deletes_an_inbox_file</c>
/// reads as an inbox file being touched from somewhere it should not be.</para>
///
/// <para>Overwrite is allowed here on purpose: every caller is rewriting a whole small record it
/// owns. Anything that must never lose an earlier write appends instead.</para></summary>
public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        try { File.Move(temp, path, overwrite: true); }
        catch (IOException) { TryDelete(temp); }
    }

    /// <summary>Removes the temp file THIS class just wrote, and never anything else.</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a stray .tmp- file is litter; failing a write because we could not tidy it is worse
        }
    }
}
