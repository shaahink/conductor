using Conductor.Core;
using System.Text;

namespace Conductor.Tests;

public sealed class LessonsManagerTests : IDisposable
{
    private readonly string _tmpDir;

    public LessonsManagerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"conductor-lessons-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    [Fact]
    public void AppendCreatesFileWithEntry()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("B8", 1, "Flaky test race between async drain and ReadAll.");

        var content = mgr.ReadContent();
        Assert.Contains("B8-1", content);
        Assert.Contains("Flaky test race", content);
        Assert.Contains("# Lessons learned", content);
    }

    [Fact]
    public void MultipleEntriesStackNewestFirst()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("B7", 45, "First entry.");
        mgr.Append("B8", 1, "Second entry.");

        var content = mgr.ReadContent();
        var idxFirst = content.IndexOf("B7-45", StringComparison.Ordinal);
        var idxSecond = content.IndexOf("B8-1", StringComparison.Ordinal);
        Assert.True(idxSecond >= 0);
        Assert.True(idxSecond < idxFirst, "newest entry should appear before older entries");
    }

    [Fact]
    public void BoundedRotationEvictsOldestPastCap()
    {
        var mgr = new LessonsManager(_tmpDir, maxBytes: 1024);
        // Fill with enough entries to exceed 1KB
        for (var i = 1; i <= 50; i++)
            mgr.Append("B0", i, new string('x', 80));

        var content = mgr.ReadContent();
        var bytes = Encoding.UTF8.GetByteCount(content);
        Assert.True(bytes <= 1152, $"content {bytes} bytes should be ~1KB (cap=1024 + overhead)");

        // Latest entries should still be present
        Assert.Contains("B0-50", content);
        Assert.Contains("B0-49", content);
    }

    [Fact]
    public void ReadRecentReturnsLimitedEntries()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("B5", 20, "Lesson five.");
        mgr.Append("B6", 30, "Lesson six.");
        mgr.Append("B7", 40, "Lesson seven.");

        var recent = mgr.ReadRecent(2);
        Assert.Contains("B7-40", recent);
        Assert.Contains("B6-30", recent);
        Assert.DoesNotContain("B5-20", recent); // limited to 2
    }

    [Fact]
    public void ReadContentReturnsEmptyWhenNoFile()
    {
        var mgr = new LessonsManager(_tmpDir);
        Assert.Equal("", mgr.ReadContent());
    }

    [Fact]
    public void ReadRecentReturnsEmptyWhenNoFile()
    {
        var mgr = new LessonsManager(_tmpDir);
        Assert.Equal("", mgr.ReadRecent(5));
    }

    [Fact]
    public void EntryCountReflectsStoredEntries()
    {
        var mgr = new LessonsManager(_tmpDir);
        Assert.Equal(0, mgr.EntryCount());

        mgr.Append("B0", 1, "First.");
        Assert.Equal(1, mgr.EntryCount());

        mgr.Append("B0", 2, "Second.");
        Assert.Equal(2, mgr.EntryCount());
    }

    [Fact]
    public void EmptyTextStillCreatesEntry()
    {
        var mgr = new LessonsManager(_tmpDir);
        mgr.Append("B1", 5, "   "); // whitespace only

        var content = mgr.ReadContent();
        Assert.Contains("B1-5", content);
        Assert.True(mgr.EntryCount() >= 1);
    }

    [Fact]
    public void DirIsCreatedAutomatically()
    {
        var deepDir = Path.Combine(_tmpDir, "nested", "path");
        Assert.False(Directory.Exists(deepDir));

        var mgr = new LessonsManager(deepDir);
        mgr.Append("B8", 1, "Should create directory.");

        Assert.True(Directory.Exists(deepDir));
        Assert.True(File.Exists(Path.Combine(deepDir, "lessons.md")));
    }
}
