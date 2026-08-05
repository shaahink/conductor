namespace Conductor.Tests;

/// <summary>
/// Deleting a scratch repo at the end of a live test. This looks like plumbing and is not: before
/// K3.1 the engine held <c>&lt;repo&gt;/.conductor/run.db</c> open, so
/// <c>Directory.Delete(repo, recursive: true)</c> threw <see cref="IOException"/> on the FIRST
/// subdirectory (".conductor" sorts before ".git"), every caller caught it, and the delete quietly
/// stopped there. Once K3.1 moved the store out of the working tree the delete got as far as
/// <c>.git</c> — whose object files git marks read-only — and threw
/// <see cref="UnauthorizedAccessException"/>, which nobody caught. Thirty green tests went red
/// without a single behaviour changing.
///
/// <para>So: clear the read-only bit on the way down, and swallow what is left. A scratch directory
/// that survives teardown is litter in the temp folder, never a test result.</para>
/// </summary>
internal static class TestTemp
{
    internal static void DeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            ClearReadOnly(new DirectoryInfo(path));
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort. A file still held open by a child process that has not exited yet is the
            // ordinary case, and it is the OS's problem, not the test's.
        }
    }

    private static void ClearReadOnly(DirectoryInfo dir)
    {
        foreach (var f in dir.EnumerateFiles())
            if (f.Attributes.HasFlag(FileAttributes.ReadOnly))
                f.Attributes &= ~FileAttributes.ReadOnly;
        foreach (var d in dir.EnumerateDirectories())
            ClearReadOnly(d);
    }
}
