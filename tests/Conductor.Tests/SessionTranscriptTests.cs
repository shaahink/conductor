using Conductor.Core.Orchestration;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// M2.4 — the session-history dir must hold a readable <c>transcript.md</c> (the design doc lists it
/// alongside prompt/verdict/handover/cost). <see cref="RunLoop.RenderTranscript"/> folds the raw agent
/// NDJSON stream (logs/session-NNN.jsonl) into markdown; these drive it directly with a synthetic
/// stream so the render contract is pinned without a full toy run.
/// </summary>
public sealed class SessionTranscriptTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    private static SessionRecord Rec() => new() { Number = 7, Stage = "M2", Kind = SessionKind.Deliver };

    [Fact]
    public void RenderTranscript_FoldsOpencodeStream_IntoReadableMarkdown()
    {
        // Leading BOM on the first line is exactly what SessionRunner's raw log writes.
        File.WriteAllText(_path, string.Join("\n",
            "﻿{\"type\":\"step_start\",\"session_id\":\"a\"}",
            "{\"type\":\"text\",\"part\":{\"text\":\"Reading the plan.\"}}",
            "{\"type\":\"tool_use\",\"part\":{\"tool\":\"Edit\",\"state\":{\"title\":\"TRACKER.md\"}}}",
            "{\"type\":\"step_finish\",\"part\":{\"cost\":0.01,\"tokens\":{\"input\":10}}}",
            "{\"type\":\"text\",\"part\":{\"text\":\"SESSION-RESULT: delivered.\"}}"));

        var md = RunLoop.RenderTranscript(_path, Rec());

        Assert.Contains("# Session 007 — M2 — Deliver", md, StringComparison.Ordinal);
        Assert.Contains("Reading the plan.", md, StringComparison.Ordinal);
        Assert.Contains("- **Edit** — TRACKER.md", md, StringComparison.Ordinal);
        Assert.Contains("SESSION-RESULT: delivered.", md, StringComparison.Ordinal);
        // step_finish is cost bookkeeping (cost.json), never transcript noise.
        Assert.DoesNotContain("0.01", md, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderTranscript_PreservesUnparseableLines_Verbatim()
    {
        File.WriteAllText(_path, string.Join("\n",
            "{\"type\":\"error\",\"part\":{\"text\":\"usage limit reached\"}}",
            "this is not json at all"));

        var md = RunLoop.RenderTranscript(_path, Rec());

        Assert.Contains("**error:** usage limit reached", md, StringComparison.Ordinal);
        // A line we cannot parse is kept in a fence rather than silently dropped — a new provider
        // wire format must never lose content.
        Assert.Contains("this is not json at all", md, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderTranscript_MissingFile_ReturnsHeaderOnly()
    {
        var md = RunLoop.RenderTranscript(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.jsonl"), Rec());
        Assert.Contains("# Session 007", md, StringComparison.Ordinal);
    }
}
