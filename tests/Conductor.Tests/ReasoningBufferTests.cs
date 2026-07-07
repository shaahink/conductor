using Conductor.Ui;

namespace Conductor.Tests;

public class ReasoningBufferTests
{
    [Fact]
    public void CollapsesGrowingSnapshotsIntoOneEntry()
    {
        var b = new ReasoningBuffer();
        var t = DateTime.UtcNow;
        b.Add("Let me first", t);
        b.Add("Let me first pop the stash", t);
        b.Add("Let me first pop the stash, then branch", t);
        Assert.Equal(1, b.Count);
        Assert.Equal("Let me first pop the stash, then branch", b.All()[0].Text);
    }

    [Fact]
    public void KeepsDistinctParagraphsSeparate()
    {
        var b = new ReasoningBuffer();
        var t = DateTime.UtcNow;
        b.Add("First thought about identity.", t);
        b.Add("Second, unrelated thought about seams.", t);
        Assert.Equal(2, b.Count);
    }

    [Fact]
    public void IgnoresEmptyAndCapsHistory()
    {
        var b = new ReasoningBuffer(cap: 5);
        b.Add("   ", DateTime.UtcNow);
        Assert.Equal(0, b.Count);
        for (var i = 0; i < 20; i++) b.Add($"distinct thought {i}", DateTime.UtcNow);
        Assert.True(b.Count <= 5);
    }

    [Fact]
    public void RecentReturnsTail()
    {
        var b = new ReasoningBuffer();
        for (var i = 0; i < 10; i++) b.Add($"thought {i}", DateTime.UtcNow);
        var recent = b.Recent(3);
        Assert.Equal(3, recent.Count);
        Assert.Equal("thought 9", recent[^1].Text);
    }
}
