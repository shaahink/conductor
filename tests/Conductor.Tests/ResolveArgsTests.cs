using Conductor.Core;

namespace Conductor.Tests;

/// <summary>AgentSession.ResolveArgs — the arg-template substitution behind per-stage model routing.
/// {model} lets the plan editor's model picker actually change the spawned CLI; an unset model must
/// drop the flag+placeholder pair rather than pass an empty --model.</summary>
public sealed class ResolveArgsTests
{
    private static readonly string[] ClaudeTemplate =
        ["-p", "{prompt}", "--output-format", "stream-json", "--dangerously-skip-permissions", "--model", "{model}"];

    [Fact]
    public void SubstitutesModel_WhenSet()
    {
        var args = AgentSession.ResolveArgs(ClaudeTemplate, "do it", "s1", null, "claude-opus-4-8");
        Assert.Equal(["-p", "do it", "--output-format", "stream-json", "--dangerously-skip-permissions", "--model", "claude-opus-4-8"], args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DropsModelFlagAndPlaceholder_WhenModelUnset(string? model)
    {
        var args = AgentSession.ResolveArgs(ClaudeTemplate, "do it", "s1", null, model);
        Assert.Equal(["-p", "do it", "--output-format", "stream-json", "--dangerously-skip-permissions"], args);
        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public void DropsShortModelFlag_ForOpencodeStyle()
    {
        string[] template = ["run", "{prompt}", "-m", "{model}", "--auto"];
        var args = AgentSession.ResolveArgs(template, "go", "s1", null, model: null);
        Assert.Equal(["run", "go", "--auto"], args); // -m dropped, --auto preserved
    }

    [Fact]
    public void SubstitutesPromptAndSessionAndResumeId()
    {
        string[] template = ["-p", "{prompt}", "--session-id", "{sessionId}", "--resume", "{claudeSessionId}"];
        var fresh = AgentSession.ResolveArgs(template, "hi", "sess-9", resumeClaudeId: null, model: null);
        Assert.Equal(["-p", "hi", "--session-id", "sess-9", "--resume", "sess-9"], fresh); // claudeSessionId falls back to sessionId
        var resumed = AgentSession.ResolveArgs(template, "hi", "sess-9", resumeClaudeId: "claude-abc", model: null);
        Assert.Equal(["-p", "hi", "--session-id", "sess-9", "--resume", "claude-abc"], resumed);
    }
}
