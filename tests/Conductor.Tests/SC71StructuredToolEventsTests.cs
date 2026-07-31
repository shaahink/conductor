using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Providers;

namespace Conductor.Tests;

/// <summary>
/// SC7.1 — tool events are stored STRUCTURED: name plus extracted fields, each value truncated on its
/// own, the stored JSON never cut. Plus transcript schema v2 with back-compat reading of v1 lines, and
/// the out-of-repo write scope the session verdict reports.
///
/// The bar these pin is a capture bar, not a display bar. The old code stored
/// <c>Trunc(input.GetRawText(), 150)</c> — one argument blob cut mid-string — so a <c>file_path</c>
/// past character 150 was gone for good: no Face, no report, no verdict could recover it, however
/// clever. Every test here is written so it would FAIL against that capture.
/// </summary>
public sealed class SC71StructuredToolEventsTests
{
    private static (AgentStreamState State, List<ToolCall> Tools, List<(string Kind, string Text)> Plain) NewClaudeState()
    {
        var tools = new List<ToolCall>();
        var plain = new List<(string, string)>();
        var state = new AgentStreamState(
            (k, t) => plain.Add((k, t)),
            onTool: (call, text) => { tools.Add(call); plain.Add(("tool", text)); });
        return (state, tools, plain);
    }

    /// <summary>A Write whose content is sent BEFORE the path — the exact shape the old 150-char cut
    /// destroyed. The path must survive whole, and the body must arrive as counts rather than as 400
    /// characters of somebody's file.</summary>
    [Fact]
    public void ClaudeToolUse_PathSurvivesBehindALongBody_AndTheBodyBecomesCounts()
    {
        var (state, tools, _) = NewClaudeState();
        var body = new string('x', 900) + "\nsecond line\nthird line";
        var line = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                id = "m1",
                content = new[]
                {
                    new { type = "tool_use", name = "Write", input = new { content = body, file_path = @"C:\code\conductor\src\App.cs" } },
                },
            },
        });

        new ClaudeProvider().ParseLine(line, state);

        var call = Assert.Single(tools);
        Assert.Equal("Write", call.Name);
        // The whole path, not a fragment of one: the old capture cut ~750 characters before this.
        Assert.Equal(@"C:\code\conductor\src\App.cs", call.Field("path"));
        Assert.Equal("923", call.Field("bytes")); // 900 body chars + two newlines + 21 of text
        Assert.Equal("3", call.Field("lines"));
        // The file body itself is never stored — a 400-char slice of it is neither the file nor useful.
        Assert.DoesNotContain(call.Fields.Values, v => v.Contains("xxxxx", StringComparison.Ordinal));
    }

    [Fact]
    public void ClaudeToolUse_Bash_CarriesCommandAndPurpose()
    {
        var (state, tools, _) = NewClaudeState();
        var line = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                id = "m1",
                content = new[]
                {
                    new
                    {
                        type = "tool_use",
                        name = "Bash",
                        input = new { description = "build the engine", command = "dotnet build Conductor.slnx -clp:ErrorsOnly" },
                    },
                },
            },
        });

        new ClaudeProvider().ParseLine(line, state);

        var call = Assert.Single(tools);
        Assert.Equal("dotnet build Conductor.slnx -clp:ErrorsOnly", call.Field("command"));
        Assert.Equal("build the engine", call.Field("purpose"));
    }

    [Fact]
    public void ClaudeToolUse_Edit_CarriesTheLineDelta()
    {
        var (state, tools, _) = NewClaudeState();
        var line = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                id = "m1",
                content = new[]
                {
                    new
                    {
                        type = "tool_use",
                        name = "Edit",
                        input = new
                        {
                            file_path = "src/Renderer.cs",
                            old_string = "a\nb\nc",
                            new_string = "a\nb\nc\nd\ne",
                        },
                    },
                },
            },
        });

        new ClaudeProvider().ParseLine(line, state);

        var call = Assert.Single(tools);
        Assert.Equal("src/Renderer.cs", call.Field("path"));
        Assert.Equal("5", call.Field("linesAdded"));
        Assert.Equal("3", call.Field("linesRemoved"));
    }

    /// <summary>An MCP-exposed conductor verb: the checkpoint id and status are the point of the call,
    /// and both must be addressable fields rather than substrings of a blob.</summary>
    [Fact]
    public void ClaudeToolUse_McpTaskUpdate_CarriesTaskIdAndStatus()
    {
        var (state, tools, _) = NewClaudeState();
        var line = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                id = "m1",
                content = new[]
                {
                    new
                    {
                        type = "tool_use",
                        name = "mcp__conductor-tasks__task_update",
                        input = new { id = "SC7.1", status = "done", evidence = ".conductor/evidence/SC7/x.md" },
                    },
                },
            },
        });

        new ClaudeProvider().ParseLine(line, state);

        var call = Assert.Single(tools);
        Assert.Equal("mcp__conductor-tasks__task_update", call.Name);
        Assert.Equal("SC7.1", call.Field("taskId"));
        Assert.Equal("done", call.Field("status"));
        Assert.Equal(".conductor/evidence/SC7/x.md", call.Field("evidence"));
    }

    /// <summary>`id` means a checkpoint on a task verb and nothing of the sort anywhere else — a wrong
    /// checkpoint id on the board's evidence trail is worse than no field at all.</summary>
    [Fact]
    public void ToolEventExtractor_IdIsOnlyATaskId_OnATaskVerb()
    {
        using var doc = JsonDocument.Parse("""{"id":"42","url":"https://example.test/x"}""");
        var fetch = ToolEventExtractor.Extract("WebFetch", doc.RootElement);
        Assert.Null(fetch.Field("taskId"));
        Assert.Equal("42", fetch.Field("id"));
        Assert.Equal("https://example.test/x", fetch.Field("url"));
    }

    /// <summary>A search tool's `path` is a search ROOT, not a file it touched. Keeping the names apart
    /// is what stops the out-of-repo write check from ever reporting a Grep as a write.</summary>
    [Fact]
    public void ToolEventExtractor_SearchRootIsNotAWrittenPath()
    {
        using var doc = JsonDocument.Parse("""{"pattern":"TODO","path":"C:\\elsewhere"}""");
        var grep = ToolEventExtractor.Extract("Grep", doc.RootElement);
        Assert.Equal("TODO", grep.Field("pattern"));
        Assert.Null(grep.Field("path"));
        Assert.Equal(@"C:\elsewhere", grep.Field("in"));
        Assert.False(ToolEventExtractor.IsWrite("Grep"));
    }

    /// <summary>The core contract: the VALUE is truncated, the JSON is not. A 5000-character command
    /// is stored as one complete (shortened) string inside a transcript line that still parses.</summary>
    [Fact]
    public void LongValueIsTruncated_ButTheStoredTranscriptLineIsStillValidJson()
    {
        var (state, tools, _) = NewClaudeState();
        var huge = "dotnet test " + new string('q', 5000);
        var line = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                id = "m1",
                content = new[] { new { type = "tool_use", name = "Bash", input = new { command = huge } } },
            },
        });

        new ClaudeProvider().ParseLine(line, state);
        var call = Assert.Single(tools);

        var stored = new TranscriptLine(1, DateTimeOffset.UnixEpoch, "7", "tool", ToolLine.Render(call))
        {
            V = TranscriptLog.SchemaVersion,
            Tool = call,
        };
        var json = JsonSerializer.Serialize(stored, TranscriptJsonContext.Default.TranscriptLine);

        // The written line is COMPLETE JSON — the failure mode this checkpoint removes was a blob cut
        // mid-string, which no reader could parse back into anything.
        using var parsed = JsonDocument.Parse(json);
        var command = parsed.RootElement.GetProperty("tool").GetProperty("fields").GetProperty("command").GetString();
        Assert.NotNull(command);
        Assert.Equal(ToolEventExtractor.MaxFieldChars + 1, command!.Length);
        Assert.EndsWith("\u2026", command, StringComparison.Ordinal);
        Assert.StartsWith("dotnet test qqq", command, StringComparison.Ordinal);
    }

    /// <summary>A nested object or array is reported by shape. Storing a slice of one would be the same
    /// mid-string cut under a different name.</summary>
    [Fact]
    public void NestedArgumentsAreReportedByShape_NeverAsACutFragment()
    {
        using var doc = JsonDocument.Parse("""{"todos":[{"a":1},{"a":2},{"a":3}],"opts":{"x":1,"y":2}}""");
        var call = ToolEventExtractor.Extract("TodoWrite", doc.RootElement);
        Assert.Equal("[3 items]", call.Field("todos"));
        Assert.Equal("{2 fields}", call.Field("opts"));
    }

    // ── schema v2 + v1 back-compat ──

    [Fact]
    public void TranscriptLog_WritesV2WithStructure_AndReadsItBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sc71-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var log = new TranscriptLog(path))
            {
                var call = new ToolCall("Write", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["path"] = @"C:\code\conductor\src\App.cs",
                    ["bytes"] = "922",
                });
                log.Append("7", "tool", ToolLine.Render(call), call);
            }

            var read = Assert.Single(TranscriptLog.ReadAll(path));
            Assert.Equal(2, read.V);
            Assert.NotNull(read.Tool);
            Assert.Equal("Write", read.Tool!.Name);
            Assert.Equal(@"C:\code\conductor\src\App.cs", read.Tool.Field("path"));
            Assert.Equal("922", read.Tool.Field("bytes"));
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    /// <summary>A file written by the PREVIOUS engine: no <c>v</c>, no <c>tool</c>, and a text field
    /// holding the tool name followed by a JSON blob cut mid-string. It must still read — reporting v1
    /// honestly, and recovering the one thing the old format never truncated away: the name.</summary>
    [Fact]
    public void TranscriptLog_ReadsV1Lines_StampsThemV1_AndRecoversTheToolName()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sc71-v1-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                """{"seq":1,"ts":"2026-07-30T10:00:00+00:00","sessionId":"3","kind":"text","text":"Reading the plan."}""",
                """{"seq":2,"ts":"2026-07-30T10:00:01+00:00","sessionId":"3","kind":"tool","text":"Edit {\"file_path\":\"C:\\\\code\\\\conductor\\\\src\\\\Very\\\\Long\\\\Path\\\\That\\\\Got\\\\Cu\u2026"}""",
                """{"seq":3,"ts":"2026-07-30T10:00:02+00:00","sessionId":"3","kind":"tool","text":"Bash {\"command\":\"dotnet build\"}"}""",
            ]);

            var read = TranscriptLog.ReadAll(path);
            Assert.Equal(3, read.Count);

            // Every old line reads, and says which era wrote it.
            Assert.All(read, l => Assert.Equal(1, l.V));
            Assert.Null(read[0].Tool); // a text line has no tool structure to recover

            // The NAME always survived the old capture — it was outside the truncated blob.
            Assert.NotNull(read[1].Tool);
            Assert.Equal("Edit", read[1].Tool!.Name);
            // ...and the fields did not, because the blob was cut mid-string. Reported as absent
            // rather than guessed at: this loss is exactly what schema v2 stops making.
            Assert.Null(read[1].Tool!.Field("path"));

            // A v1 line whose blob happened to survive whole still yields its fields.
            Assert.Equal("Bash", read[2].Tool!.Name);
            Assert.Equal("dotnet build", read[2].Tool!.Field("command"));
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    /// <summary>The same back-compat read, on the text payloads the PUBLISHED engine actually
    /// produced for a rig run — the exact three tool calls the SC7.1 proof drove, at the exact cut
    /// the old <c>Trunc(rawJson, 150)</c> made. Two of the three are unrecoverable, and the read must
    /// say so rather than invent fields.</summary>
    [Fact]
    public void TranscriptLog_ReadsRealV1LinesWrittenByThePublishedEngine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sc71-real-v1-{Guid.NewGuid():N}.jsonl");
        try
        {
            // The three TEXT payloads a rig run's published engine wrote, character for character —
            // `Trunc(input.GetRawText(), 150)` applied to each call's arguments. The Write line stops
            // after `{"content":"` (12 chars) plus 138 z's, 250 characters short of the file_path
            // that followed it. Serialised here through the same writer the engine used, so the
            // on-disk escaping is the engine's and not this test's idea of it.
            string[] legacyTexts =
            [
                "Bash {\"description\":\"probe the toolchain before touching anything, because the last " +
                    "three attempts died on a missing SDK and nobody could tell from the tra…",
                "Write {\"content\":\"" + new string('z', 138) + "…",
                "mcp__conductor-tasks__task_update {\"id\":\"R1.1\",\"status\":\"done\",\"evidence\":\".conductor/evidence/R1/rig.md\"}",
            ];
            File.WriteAllLines(path, legacyTexts.Select((text, i) => JsonSerializer.Serialize(new
            {
                seq = i + 2,
                ts = "2026-07-31T14:45:38.797+00:00",
                sessionId = "1",
                kind = "tool",
                text,
            })));

            // The fixture really is v1: no schema marker, no structure — which is the whole problem.
            var onDisk = File.ReadAllLines(path);
            Assert.All(onDisk, l => Assert.DoesNotContain("\"v\":", l, StringComparison.Ordinal));
            Assert.All(onDisk, l => Assert.DoesNotContain("\"tool\":", l, StringComparison.Ordinal));

            var read = TranscriptLog.ReadAll(path);
            Assert.Equal(3, read.Count);
            Assert.All(read, l => Assert.Equal(1, l.V));

            // Names recover from every one of them.
            Assert.Equal(["Bash", "Write", "mcp__conductor-tasks__task_update"], read.Select(l => l.Tool!.Name));

            // The two whose blob was cut lost their arguments for good — the Bash line's `command`
            // and the Write line's `file_path` were never in the file. That loss is the entire reason
            // schema v2 exists, and reading them back must not pretend otherwise.
            Assert.Null(read[0].Tool!.Field("command"));
            Assert.Null(read[1].Tool!.Field("path"));

            // The one whose blob happened to fit still yields its fields.
            Assert.Equal("R1.1", read[2].Tool!.Field("taskId"));
            Assert.Equal("done", read[2].Tool!.Field("status"));
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    // ── out-of-repo write scope ──

    [Theory]
    [InlineData(@"C:\repo\src\App.cs", false)]
    [InlineData(@"src\App.cs", false)]
    [InlineData(@"C:\repo", false)]
    [InlineData(@"..\elsewhere\App.cs", true)]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", true)]
    [InlineData(@"C:\repo-other\App.cs", true)]
    public void RepoScope_JudgesWritePaths(string path, bool expectedOutside)
    {
        Assert.Equal(expectedOutside, RepoScope.IsOutside(@"C:\repo", path, out _));
    }

    /// <summary>SC4.3 established that a plan naming satellite repos means work legitimately lands
    /// there. Flagging those as strays would make the note noise for the most careful plans.</summary>
    [Fact]
    public void RepoScope_ADeclaredSatelliteIsNotOutside()
    {
        string[] satellites = [@"C:\sibling"];
        Assert.False(RepoScope.IsOutside(@"C:\repo", satellites, @"C:\sibling\src\App.cs", out _));
        Assert.True(RepoScope.IsOutside(@"C:\repo", satellites, @"C:\other\src\App.cs", out var full));
        Assert.Equal(@"C:\other\src\App.cs", full);
    }

    [Fact]
    public void ToolEventExtractor_KnowsWhichToolsWrite()
    {
        Assert.True(ToolEventExtractor.IsWrite("Write"));
        Assert.True(ToolEventExtractor.IsWrite("Edit"));
        Assert.True(ToolEventExtractor.IsWrite("MultiEdit"));
        Assert.True(ToolEventExtractor.IsWrite("NotebookEdit"));
        Assert.True(ToolEventExtractor.IsWrite("mcp__filesystem__write_file"));
        Assert.False(ToolEventExtractor.IsWrite("Read"));
        Assert.False(ToolEventExtractor.IsWrite("Bash"));
        Assert.False(ToolEventExtractor.IsWrite("mcp__conductor-tasks__task_update"));
    }

    // ── the other provider on the same vocabulary ──

    /// <summary>Opencode's <c>part.state.input</c> is the same argument object as claude's
    /// <c>tool_use.input</c>, so it goes through the same extractor. Two wires, one vocabulary — or the
    /// Face and the verdict would have to learn both.</summary>
    [Fact]
    public void OpencodeToolUse_UsesTheSameStructuredVocabulary()
    {
        var (state, tools, _) = NewClaudeState();
        new OpencodeProvider().ParseLine(
            """{"type":"tool_use","part":{"tool":"Write","state":{"title":"Write App.cs","input":{"file_path":"src/App.cs","content":"one\ntwo"}}}}""",
            state);

        var call = Assert.Single(tools);
        Assert.Equal("Write", call.Name);
        Assert.Equal("src/App.cs", call.Field("path"));
        Assert.Equal("2", call.Field("lines"));
    }

    /// <summary>Opencode often sends only its own rendered title. That is still the best structure
    /// available and is kept as a purpose, not thrown away — and the emitted TEXT stays exactly what it
    /// was, so the `bg logs` tail and the markdown transcript render unchanged.</summary>
    [Fact]
    public void OpencodeToolUse_WithOnlyATitle_KeepsItAsPurpose_AndTheTextIsUnchanged()
    {
        var (state, tools, plain) = NewClaudeState();
        new OpencodeProvider().ParseLine(
            """{"type":"tool_use","part":{"tool":"Edit","state":{"title":"TRACKER.md"}}}""", state);

        var call = Assert.Single(tools);
        Assert.Equal("Edit", call.Name);
        Assert.Equal("TRACKER.md", call.Field("purpose"));
        Assert.Equal(("tool", "Edit TRACKER.md"), plain[0]);
    }

    /// <summary>A consumer that wires no tool handler (the <c>bg logs</c> tail, an old test) still gets
    /// the line on the plain channel. Two channels rather than a widened emit, so nothing had to
    /// change to keep working.</summary>
    [Fact]
    public void AConsumerWithNoToolHandler_StillSeesThePlainToolLine()
    {
        var seen = new List<(string Kind, string Text)>();
        var state = new AgentStreamState((k, t) => seen.Add((k, t)));
        new ClaudeProvider().ParseLine(
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","name":"Read","input":{"file_path":"README.md"}}]}}""",
            state);

        var (kind, text) = Assert.Single(seen);
        Assert.Equal("tool", kind);
        // SC7.2 made this line the readable one (`Read README.md`, not the `Read path=…` field dump).
        // The bar this test holds is unchanged: a consumer wiring no tool handler still gets the line.
        Assert.Equal("Read README.md", text);
    }
}
