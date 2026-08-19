using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Http;
using Conductor.Core.Providers;

namespace Conductor.Tests;

/// <summary>
/// SC7.2 — the wire carries a READABLE line per tool call, and a per-session digest is computed,
/// stored and served.
///
/// SC7.1 made the structure survive capture; every bar here is about spending it. The lines pinned
/// below are devcontext #10's worked example verbatim — that example is the acceptance shape for this
/// checkpoint, and each assertion is written so it would fail against both the pre-SC7.1 blob and
/// against SC7.1's structural <c>Edit path=… linesAdded=12 linesRemoved=3</c> dump.
/// </summary>
public sealed class SC72DigestAndWireLinesTests
{
    private static ToolCall Call(string name, params (string Key, string Value)[] fields)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in fields) map[k] = v;
        return new ToolCall(name, map);
    }

    // ── part 1: the wire one-liner ──

    /// <summary>The spec's three example lines, verbatim, plus the bg_start form from devcontext #10.
    /// The one departure is deliberate and uniform: an MCP tool always carries its server, so
    /// <c>bg_start</c> reads <c>conductor bg_start</c> exactly as <c>task_update</c> reads
    /// <c>conductor task_update</c> — the example wrote one with the prefix and one without, and a
    /// reader seeing a bare <c>bg_start</c> cannot tell whose background job it was.</summary>
    [Theory]
    [InlineData("Edit LibrarySurfaceRenderer.cs (+12/-3)")]
    [InlineData("Bash dotnet build src/App")]
    [InlineData("conductor task_update G1.1 -> done")]
    [InlineData("conductor bg_start \"G1.1 full solution build\"")]
    public void TheWorkedExampleLinesRenderVerbatim(string expected)
    {
        var calls = new[]
        {
            Call("Edit", ("path", @"C:\code\dev\src\DevContext.Core\Rendering\LibrarySurfaceRenderer.cs"),
                         ("linesAdded", "12"), ("linesRemoved", "3")),
            Call("Bash", ("command", "dotnet build src/App")),
            Call("mcp__conductor-tasks__task_update", ("taskId", "G1.1"), ("status", "done")),
            Call("mcp__conductor-tasks__bg_start", ("purpose", "G1.1 full solution build"),
                                                  ("command", "dotnet build Conductor.slnx")),
        };

        Assert.Contains(expected, calls.Select(ToolLine.Render), StringComparer.Ordinal);
    }

    /// <summary>The regression this checkpoint exists to end: what the Face rendered before it. The
    /// pre-SC7.1 capture was a JSON blob cut mid-string; SC7.1's line was complete but was still a
    /// field dump. Neither shape may come back.</summary>
    [Fact]
    public void TheLineIsNeitherAJsonBlobNorAFieldDump()
    {
        var line = ToolLine.Render(Call("Edit",
            ("path", @"C:\code\conductor\src\Conductor\Core\Providers\ToolLine.cs"),
            ("linesAdded", "40"), ("linesRemoved", "1")));

        Assert.Equal("Edit ToolLine.cs (+40/-1)", line);
        Assert.DoesNotContain("{", line, StringComparison.Ordinal);
        Assert.DoesNotContain("path=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", line, StringComparison.Ordinal);
    }

    /// <summary>A live claude tool_use envelope reaches the transcript's TEXT as the readable line,
    /// while the structured payload is untouched beside it — this is a rendering, not a second
    /// capture, and a reader who wants the whole path still has it.</summary>
    [Fact]
    public void ClaudeEmitsTheReadableLine_AndTheStructureSurvivesBesideIt()
    {
        var lines = new List<(string Kind, string Text)>();
        var captured = new List<ToolCall>();
        var state = new AgentStreamState(
            (k, t) => lines.Add((k, t)),
            onTool: (call, text) => { captured.Add(call); lines.Add(("tool", text)); });

        var envelope = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                id = "m1",
                content = new[]
                {
                    new { type = "tool_use", name = "Bash", input = new { command = "dotnet test Conductor.slnx --filter SC72" } },
                },
            },
        });

        new ClaudeProvider().ParseLine(envelope, state);

        var (kind, text) = Assert.Single(lines);
        Assert.Equal("tool", kind);
        Assert.Equal("Bash dotnet test Conductor.slnx --filter SC72", text);
        Assert.Equal("dotnet test Conductor.slnx --filter SC72", Assert.Single(captured).Field("command"));
    }

    /// <summary>An argv ARRAY — conductor's own bg_start shape — joins back into the command line a
    /// human recognises. SC7.1 stored it as <c>[3 items]</c>: complete, and unreadable.</summary>
    [Fact]
    public void AnArgvArrayCommandJoinsIntoAReadableCommand()
    {
        using var doc = JsonDocument.Parse("""{"command":["dotnet","test","Conductor.slnx"],"purpose":"full suite"}""");
        var call = ToolEventExtractor.Extract("mcp__conductor-tasks__bg_start", doc.RootElement);

        Assert.Equal("dotnet test Conductor.slnx", call.Field("command"));
        Assert.Equal("conductor bg_start \"full suite\"", ToolLine.Render(call));
    }

    /// <summary>An unknown tool with nothing recognisable renders as its bare name. Inventing a line
    /// out of a fragment of its arguments is the failure this stage removed.</summary>
    [Fact]
    public void AnUnknownToolWithNoUsefulFieldsRendersAsItsName()
    {
        Assert.Equal("SomeNewTool", ToolLine.Render(Call("SomeNewTool")));
        Assert.Equal("SomeNewTool why", ToolLine.Render(Call("SomeNewTool", ("purpose", "why"))));
    }

    // ── part 2: the digest ──

    /// <summary>The worked example's five facts, folded from a session's calls.</summary>
    [Fact]
    public void TheDigestCarriesTheWorkedExamplesFiveFacts()
    {
        var repo = OperatingSystem.IsWindows() ? @"C:\code\conductor" : "/code/conductor";
        var digest = new SessionDigest();
        foreach (var call in SampleSession(repo)) digest.Add(call, repo);

        Assert.Equal(9, digest.ToolCalls);
        Assert.Equal(6, digest.DistinctTools);
        Assert.Equal(3, digest.Mix["Bash"]);
        Assert.Equal(2, digest.Mix["Edit"]);
        // The MCP prefix is off the mix key: the same logical tool counts once however the harness
        // exposed it (`mcp__conductor-tasks__bg_start` and a bare `bg_start` are one row, not two).
        Assert.Equal(1, digest.Mix["bg_start"]);

        // Files are counted per file, repo-relative, and only WRITES count — the Read below is not here.
        Assert.Equal(2, digest.FilesTouched.Count);
        Assert.Equal(3, digest.FileWrites);
        Assert.Equal(2, digest.FilesTouched["src/Conductor/Core/Providers/ToolLine.cs"]);
        Assert.Equal(1, digest.FilesTouched["src/Conductor/Core/Events/SessionDigest.cs"]);

        Assert.Equal("SC7.2 -> in_progress", Assert.Single(digest.Claims));
        Assert.Equal("SC7.2 digest proof run", Assert.Single(digest.BackgroundJobs));

        // The notable shell calls only: the `grep` is excluded by its VERB, because it matches every
        // keyword in the notable list and is not a build. The bg_start's own command counts — a
        // backgrounded suite is a shell call like any other.
        Assert.Equal(3, digest.Commands.Count);
        Assert.Contains("dotnet build Conductor.slnx -clp:ErrorsOnly", digest.Commands, StringComparer.Ordinal);
        Assert.DoesNotContain(digest.Commands, c => c.StartsWith("grep", StringComparison.Ordinal));
    }

    /// <summary>A write OUTSIDE the repo keeps its absolute path, so it is visible as one on sight —
    /// the same signal SC7.1's verdict note reports as a count.</summary>
    [Fact]
    public void AnOutOfRepoWriteStaysAbsoluteInTheDigest()
    {
        var repo = OperatingSystem.IsWindows() ? @"C:\code\conductor" : "/code/conductor";
        var stray = OperatingSystem.IsWindows() ? @"C:\Users\me\.claude\MEMORY.md" : "/home/me/.claude/MEMORY.md";
        var digest = new SessionDigest();
        digest.Add(Call("Write", ("path", stray), ("bytes", "40")), repo);

        Assert.Equal(stray, Assert.Single(digest.FilesTouched).Key);
    }

    /// <summary>Ranked descending by count, then by name — a digest that reshuffles between two reads
    /// of the same data is one a reader stops trusting.</summary>
    [Fact]
    public void TheMixIsRankedStably()
    {
        var digest = new SessionDigest();
        foreach (var name in new[] { "Read", "Bash", "Bash", "Edit", "Bash", "Edit", "Apple" })
            digest.Add(Call(name));

        Assert.Equal(new[] { "Bash", "Edit", "Apple", "Read" }, SessionDigest.Ranked(digest.Mix).Select(p => p.Key));
    }

    /// <summary>The rendered block is the worked example's shape — the acceptance form named in the
    /// spec — and it is what an agent reads back through <c>session_detail</c>.</summary>
    [Fact]
    public void TheRenderedBlockMatchesTheWorkedExampleShape()
    {
        var repo = OperatingSystem.IsWindows() ? @"C:\code\conductor" : "/code/conductor";
        var digest = new SessionDigest();
        foreach (var call in SampleSession(repo)) digest.Add(call, repo);

        var text = digest.Render();

        Assert.Contains("TOOL CALLS: 9", text, StringComparison.Ordinal);
        Assert.Contains("distinct tools: 6", text, StringComparison.Ordinal);
        Assert.Contains("MIX: Bash 3, Edit 2", text, StringComparison.Ordinal);
        Assert.Contains("FILES TOUCHED (3 writes over 2 files):", text, StringComparison.Ordinal);
        Assert.Contains("src/Conductor/Core/Providers/ToolLine.cs  2x", text, StringComparison.Ordinal);
        Assert.Contains("CLAIMS: SC7.2 -> in_progress", text, StringComparison.Ordinal);
        Assert.Contains("BACKGROUND JOBS (1):", text, StringComparison.Ordinal);
        Assert.Contains("SC7.2 digest proof run", text, StringComparison.Ordinal);
        Assert.Contains("BUILD / TEST / EVIDENCE COMMANDS (3):", text, StringComparison.Ordinal);
    }

    /// <summary>The one-line form the run log carries beside the session's exit line.</summary>
    [Fact]
    public void TheSummaryLineReadsAtAGlance()
    {
        var repo = OperatingSystem.IsWindows() ? @"C:\code\conductor" : "/code/conductor";
        var digest = new SessionDigest();
        foreach (var call in SampleSession(repo)) digest.Add(call, repo);

        // KS7.2 gave the line a provenance suffix, and it is unconditional on purpose: a digest that
        // does not say where its numbers came from cannot be argued with when hook and transcript
        // disagree. Both endings are pinned, so the label can neither vanish nor lie about a fallback.
        Assert.Equal("9 tool calls · 6 tools · 2 files (3 writes) · 1 claim · 1 bg job · 3 build/test commands · via transcript",
            digest.Summary());

        digest.Source = SessionDigest.HookSource;
        Assert.EndsWith("· 3 build/test commands · via hook", digest.Summary(), StringComparison.Ordinal);
    }

    /// <summary>Round-trips through the JSON that run.db's <c>sessions.digest</c> column stores. An
    /// EMPTY digest stores nothing at all: a session that says nothing about what it did must not read
    /// as one that provably did nothing.</summary>
    [Fact]
    public void TheDigestRoundTripsThroughStoredJson_AndAnEmptyOneStoresNothing()
    {
        Assert.Null(new SessionDigest().ToJson());
        Assert.Null(SessionDigest.FromJson(null));
        Assert.Null(SessionDigest.FromJson("not json"));

        var repo = OperatingSystem.IsWindows() ? @"C:\code\conductor" : "/code/conductor";
        var digest = new SessionDigest();
        foreach (var call in SampleSession(repo)) digest.Add(call, repo);

        var back = SessionDigest.FromJson(digest.ToJson());

        Assert.NotNull(back);
        Assert.Equal(digest.ToolCalls, back!.ToolCalls);
        Assert.Equal(digest.Summary(), back.Summary());
        Assert.Equal(digest.Render(), back.Render());
    }

    /// <summary>The collections are capped so a session that writes a thousand files cannot put a
    /// thousand strings in state.json — and the total keeps counting past the cap, so the digest never
    /// under-reports what happened just because it stopped listing it.</summary>
    [Fact]
    public void TheCollectionsAreCapped_ButTheTotalKeepsCounting()
    {
        var repo = OperatingSystem.IsWindows() ? @"C:\code\conductor" : "/code/conductor";
        var digest = new SessionDigest();
        for (var i = 0; i < SessionDigest.MaxTrackedFiles + 50; i++)
            digest.Add(Call("Write", ("path", $"src/f{i}.cs"), ("bytes", "10")), repo);

        Assert.Equal(SessionDigest.MaxTrackedFiles, digest.FilesTouched.Count);
        Assert.Equal(SessionDigest.MaxTrackedFiles + 50, digest.ToolCalls);
    }

    // ── part 3: what /sessions serves ──

    /// <summary>The DTO ranks once, on the engine side: two clients sorting the same map independently
    /// is two chances to disagree about what one session did.</summary>
    [Fact]
    public void TheServedDtoIsRankedAndFlattened()
    {
        var repo = OperatingSystem.IsWindows() ? @"C:\code\conductor" : "/code/conductor";
        var digest = new SessionDigest();
        foreach (var call in SampleSession(repo)) digest.Add(call, repo);

        var dto = SessionDigestDto.From(digest);

        Assert.NotNull(dto);
        Assert.Equal(9, dto!.ToolCalls);
        Assert.Equal(6, dto.DistinctTools);
        Assert.Equal("Bash", dto.Mix[0].Name);
        Assert.Equal(3, dto.Mix[0].Count);
        Assert.Equal("src/Conductor/Core/Providers/ToolLine.cs", dto.FilesTouched[0].Name);
        Assert.Equal(3, dto.FileWrites);
        Assert.Equal("SC7.2 -> in_progress", Assert.Single(dto.Claims));
    }

    /// <summary>No digest is served as null, never as a zeroed one — the wire must not let a session
    /// from before this column read as a session that did nothing.</summary>
    [Fact]
    public void AMissingDigestIsServedAsNull()
    {
        Assert.Null(SessionDigestDto.From(null));
        Assert.Null(SessionDigestDto.From(new SessionDigest()));
    }

    /// <summary>The whole session row, serialised the way <c>/sessions</c> serialises it, carries the
    /// digest — this is the endpoint's own JSON contract, not a hand-built object.</summary>
    [Fact]
    public void TheSessionsPayloadCarriesTheDigest()
    {
        var repo = OperatingSystem.IsWindows() ? @"C:\code\conductor" : "/code/conductor";
        var digest = new SessionDigest();
        foreach (var call in SampleSession(repo)) digest.Add(call, repo);

        var payload = new SessionsDto([
            new SessionRowDto(12, "SC7", "Deliver", "2026-07-31T10:00:00Z", null, "Advanced",
                1, 0, "gates GREEN", "landed", 1, Digest: SessionDigestDto.From(digest)),
        ]);

        var json = JsonSerializer.Serialize(payload, ControlPlaneJsonContext.Default.SessionsDto);
        using var parsed = JsonDocument.Parse(json);
        var served = parsed.RootElement.GetProperty("sessions")[0].GetProperty("digest");

        Assert.Equal(9, served.GetProperty("toolCalls").GetInt32());
        Assert.Equal("Bash", served.GetProperty("mix")[0].GetProperty("name").GetString());
        Assert.Equal("src/Conductor/Core/Providers/ToolLine.cs",
            served.GetProperty("filesTouched")[0].GetProperty("name").GetString());
        Assert.Equal("SC7.2 digest proof run", served.GetProperty("backgroundJobs")[0].GetString());
    }

    /// <summary>One session's worth of calls, in the mix a real Deliver session produces.</summary>
    private static IEnumerable<ToolCall> SampleSession(string repo)
    {
        var sep = OperatingSystem.IsWindows() ? '\\' : '/';
        string Abs(string rel) => repo + sep + rel.Replace('/', sep);

        yield return Call("Read", ("path", Abs("src/Conductor/Core/Providers/ClaudeProvider.cs")));
        yield return Call("mcp__conductor-tasks__task_update", ("taskId", "SC7.2"), ("status", "in_progress"));
        yield return Call("Write", ("path", Abs("src/Conductor/Core/Providers/ToolLine.cs")), ("bytes", "4200"), ("lines", "164"));
        yield return Call("Edit", ("path", Abs("src/Conductor/Core/Providers/ToolLine.cs")), ("linesAdded", "8"), ("linesRemoved", "2"));
        yield return Call("Edit", ("path", Abs("src/Conductor/Core/Events/SessionDigest.cs")), ("linesAdded", "3"), ("linesRemoved", "1"));
        yield return Call("Bash", ("command", "dotnet build Conductor.slnx -clp:ErrorsOnly"));
        yield return Call("Bash", ("command", "grep -rn \"build\" src/"));
        yield return Call("Bash", ("command", "dotnet test Conductor.slnx --filter SC72"));
        yield return Call("mcp__conductor-tasks__bg_start", ("purpose", "SC7.2 digest proof run"),
                                                            ("command", "dotnet test Conductor.slnx"));
    }
}
