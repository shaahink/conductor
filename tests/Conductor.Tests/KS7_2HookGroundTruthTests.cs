using System.Text.Json;

using Conductor.Commands;
using Conductor.Core.Events;
using Conductor.Core.Providers;

namespace Conductor.Tests;

/// <summary>
/// KS7.2 — the hook channel as the primary source for what a session did.
/// </summary>
/// <remarks>
/// The corpus in <c>testdata/ks72</c> is not synthetic and not edited: it is both channels of ONE
/// live <c>claude -p</c> run on 2.1.235 — the raw <c>--output-format stream-json</c> output and the
/// raw stdin of every hook that fired during it, captured side by side. That is what makes the
/// equivalence test worth anything. A hand-written corpus would only prove that two functions this
/// file also wrote agree with each other.
/// </remarks>
public sealed class KS7_2HookGroundTruthTests
{
    // ── the replay corpus: two channels, one run ──

    /// <summary>
    /// The acceptance. Hook-derived and transcript-derived digests must agree on the same run.
    /// </summary>
    /// <remarks>
    /// They agree because both go through <see cref="ToolEventExtractor"/> — the hook's
    /// <c>tool_input</c> and the stream's <c>tool_use.input</c> are the same object — so a drift
    /// between them can only ever be a drift in DELIVERY, and that is exactly what this test is for.
    /// </remarks>
    [Fact]
    public void HookDerivedDigest_MatchesTranscriptDerived_OnTheReplayCorpus()
    {
        var transcript = TranscriptDigest();
        var hook = HookDigest(out _);

        Assert.Equal(SessionDigest.TranscriptSource, transcript.Source);
        Assert.Equal(SessionDigest.HookSource, hook.Source);
        Assert.Equal(transcript.ToolCalls, hook.ToolCalls);
        Assert.Equal(transcript.Mix, hook.Mix);
        Assert.Equal(transcript.FilesTouched, hook.FilesTouched);
        Assert.Equal(transcript.Claims, hook.Claims);
        Assert.Equal(transcript.BackgroundJobs, hook.BackgroundJobs);
        Assert.Equal(transcript.Commands, hook.Commands);
    }

    /// <summary>The corpus is only worth something if it actually holds a range of calls. Seven tool
    /// calls over five distinct tools, two of which the permission layer refused — pinned so a future
    /// edit to the fixture cannot quietly shrink it to something the test passes trivially.</summary>
    [Fact]
    public void TheReplayCorpus_CoversARangeOfCalls()
    {
        var transcript = TranscriptDigest();
        Assert.Equal(7, transcript.ToolCalls);
        Assert.Equal(new[] { "Bash", "Edit", "Grep", "Read", "Write" }, transcript.Mix.Keys.Order().ToArray());
        Assert.Single(transcript.FilesTouched);
    }

    /// <summary>
    /// The measurement that set this checkpoint's design, pinned against the corpus that produced it:
    /// <b>PostToolUse does not fire for a call that was refused or failed.</b>
    /// </summary>
    /// <remarks>
    /// Two of the corpus's seven calls were refused by print mode ("requires approval"). Both have a
    /// PreToolUse and neither has a PostToolUse. Recording on PreToolUse is therefore what keeps the
    /// hook channel counting the same population as the transcript; a PostToolUse-only channel would
    /// have reported five, and a session whose test commands all failed would have reported none.
    /// </remarks>
    [Fact]
    public void RefusedCalls_HaveNoPostToolUse_AndAreCountedAsFailures()
    {
        var payloads = HookPayloads();
        var pre = payloads.Count(p => EventNameOf(p) == "PreToolUse");
        var post = payloads.Count(p => EventNameOf(p) == "PostToolUse");
        Assert.Equal(7, pre);
        Assert.Equal(5, post);

        var hook = HookDigest(out var entries);
        Assert.Equal(7, hook.ToolCalls);
        Assert.Equal(2, hook.FailedCalls);

        var failed = entries.Where(e => !e.Succeeded).Select(e => e.Call.Field("command")).ToList();
        Assert.Equal(2, failed.Count);
        Assert.Contains(failed, c => c!.Contains("powershell", StringComparison.Ordinal));
        Assert.Contains(failed, c => c!.Contains("conductor task --done", StringComparison.Ordinal));
    }

    /// <summary>Every recorded call carries the provider's own <c>tool_use_id</c>, and it is the SAME
    /// id the stream used. That identity is what lets a PostToolUse outcome be merged onto its call
    /// exactly, instead of matched by tool name and timing — which would go wrong the first time a
    /// session made two identical calls in parallel.</summary>
    [Fact]
    public void ToolUseIds_AreTheSameStringInBothChannels()
    {
        HookDigest(out var entries);
        var hookIds = entries.Select(e => e.Id).ToList();
        Assert.All(hookIds, id => Assert.False(string.IsNullOrEmpty(id)));

        var streamIds = StreamToolUses().Select(t => (string?)t.Id).ToList();
        Assert.Equal(streamIds, hookIds);
    }

    // ── the fallback: a hook-less agent still works ──

    /// <summary>No hook file at all — an opencode session, a <c>--bare</c> claude, any provider with
    /// no hook surface. The transcript-derived digest must stand exactly as it was, because it is the
    /// only source there is.</summary>
    [Fact]
    public void NoHookFile_LeavesTheTranscriptDigestAlone()
    {
        using var temp = new TempDir();
        Assert.Null(HookToolLog.BuildDigest(HookToolLog.PathFor(temp.Path, 1), temp.Path));
    }

    /// <summary>An empty hook file is the same answer as no hook file. A session that made no tool
    /// calls must not have a digest promoted over it that says it made none — that is a claim about
    /// the session, and it would erase whatever the transcript did manage to see.</summary>
    [Fact]
    public void EmptyHookFile_IsTreatedAsNoSourceAtAll()
    {
        using var temp = new TempDir();
        var path = HookToolLog.PathFor(temp.Path, 3);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        Assert.Null(HookToolLog.BuildDigest(path, temp.Path));
    }

    /// <summary>A torn tail — the session was killed between the newline and the next line — costs
    /// that line and nothing else.</summary>
    [Fact]
    public void AHalfWrittenLine_CostsOnlyItself()
    {
        using var temp = new TempDir();
        var path = HookToolLog.PathFor(temp.Path, 4);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "{\"utc\":\"2026-08-18T00:00:00Z\",\"tool\":\"Read\",\"id\":\"a\",\"f\":{\"path\":\"x.txt\"}}\n"
            + "{\"utc\":\"2026-08-18T00:00:01Z\",\"tool\":\"Wri\n"
            + "{\"utc\":\"2026-08-18T00:00:02Z\",\"tool\":\"Grep\",\"id\":\"c\",\"f\":{\"pattern\":\"x\"}}\n");
        var digest = HookToolLog.BuildDigest(path, temp.Path);
        Assert.NotNull(digest);
        Assert.Equal(2, digest!.ToolCalls);
    }

    /// <summary>An outcome line may arrive before the call it belongs to — parallel tool calls
    /// interleave in the file, and two hook processes race for the write. The merge is by id, so file
    /// order does not decide the answer.</summary>
    [Fact]
    public void AnOutcomeLine_MergesOntoItsCall_WhateverTheFileOrder()
    {
        using var temp = new TempDir();
        var path = HookToolLog.PathFor(temp.Path, 5);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "{\"id\":\"b\",\"ok\":true,\"ms\":7}\n"
            + "{\"utc\":\"2026-08-18T00:00:00Z\",\"tool\":\"Read\",\"id\":\"a\",\"f\":{\"path\":\"x.txt\"}}\n"
            + "{\"utc\":\"2026-08-18T00:00:01Z\",\"tool\":\"Read\",\"id\":\"b\",\"f\":{\"path\":\"y.txt\"}}\n");
        var entries = HookToolLog.ReadEntries(path);
        Assert.Equal(2, entries.Count);
        Assert.False(entries[0].Succeeded);
        Assert.True(entries[1].Succeeded);
        Assert.Equal(7, entries[1].DurationMs);
    }

    // ── the hook verb itself ──

    /// <summary>The recording half of the verb, driven through its real entry point. A PreToolUse
    /// payload produces a call line; the same command on PostToolUse produces the outcome that
    /// completes it.</summary>
    [Fact]
    public async Task TheHookVerb_RecordsACall_AndThenItsOutcome()
    {
        using var temp = new TempDir();
        var events = Path.Combine(temp.Path, "hook-tools", "001.jsonl");
        const string id = "toolu_test_1";

        await RunAsync(temp, events, Payload("PreToolUse", id, "Write",
            new { file_path = Path.Combine(temp.Path, "a.txt"), content = "hi" }));
        await RunAsync(temp, events, Payload("PostToolUse", id, "Write",
            new { file_path = Path.Combine(temp.Path, "a.txt"), content = "hi" }, durationMs: 12));

        var entries = HookToolLog.ReadEntries(events);
        var entry = Assert.Single(entries);
        Assert.Equal("Write", entry.Call.Name);
        Assert.Equal(Path.Combine(temp.Path, "a.txt"), entry.Call.Field("path"));
        Assert.True(entry.Succeeded);
        Assert.Equal(12, entry.DurationMs);
    }

    /// <summary>Without <c>--tool-events</c> the verb never reads stdin and never writes a file. This
    /// is the shape every invocation before KS7.2 had, and the budget rail has to keep working in it.</summary>
    [Fact]
    public async Task WithoutToolEvents_TheVerbRecordsNothing()
    {
        using var temp = new TempDir();
        var cmd = new HookBudgetCommand { Payload = Payload("PreToolUse", "x", "Read", new { file_path = "a" }) };
        var exit = await cmd.ExecuteAsync(null!, new HookBudgetCommand.Settings { StateDir = temp.Path })
            .ConfigureAwait(true);
        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "hook-tools")));
    }

    /// <summary>A payload with no tool in it — SessionStart, Stop, SessionEnd — is not this channel's
    /// business and must not become a phantom call.</summary>
    [Fact]
    public async Task ANonToolHookEvent_IsNotRecorded()
    {
        using var temp = new TempDir();
        var events = Path.Combine(temp.Path, "hook-tools", "001.jsonl");
        var wrote = await HookToolLog.TryAppendFromHookPayloadAsync(
            events, "{\"hook_event_name\":\"SessionStart\",\"session_id\":\"s\"}", DateTime.UtcNow)
            .ConfigureAwait(true);
        Assert.False(wrote);
        Assert.False(File.Exists(events));
    }

    // ── bug #19 class: a claim made through the CLI ──

    [Theory]
    [InlineData("conductor task --done KS7.2 --evidence x.md", "KS7.2", "done")]
    [InlineData("conductor task --in-progress KS7.2", "KS7.2", "in_progress")]
    [InlineData("powershell -NoProfile -Command \"conductor task --done R1.1 --evidence hello.txt\"", "R1.1", "done")]
    [InlineData("dotnet run --project src/Conductor -- task --skipped B4.2", "B4.2", "skipped")]
    [InlineData("conductor task --todo KS9.1", "KS9.1", "todo")]
    [InlineData("conductor task --blocked KS9.1", "KS9.1", "blocked")]
    public void ACliClaim_IsRead(string command, string id, string status)
    {
        Assert.True(SessionDigest.TryReadCliClaim(command, out var gotId, out var gotStatus));
        Assert.Equal(id, gotId);
        Assert.Equal(status, gotStatus);
    }

    /// <summary>A fabricated claim is worse than a missed one, so the reader is narrow on purpose:
    /// looking at the text of a claim is not making one, <c>--amend</c> moves no card, and a flag with
    /// no id after it is not a claim.</summary>
    [Theory]
    [InlineData("grep -n \"task --done\" prompt.md")]
    [InlineData("cat plans/karvansara/EDGE-TRACKER.md | head -40")]
    [InlineData("conductor task --amend KS7.2 --note \"acceptance is wrong\"")]
    [InlineData("conductor task --done --evidence x.md")]
    [InlineData("conductor task --list")]
    [InlineData("dotnet build Conductor.slnx")]
    public void NotAClaim_IsNotRead(string command)
    {
        Assert.False(SessionDigest.TryReadCliClaim(command, out _, out _));
    }

    /// <summary>The defect itself, at the level the digest reports it. A session that claims through
    /// the shell — which is what this repo's own prompt instructs — used to produce a digest saying
    /// zero claims, and that number was read as "delivered nothing".</summary>
    [Fact]
    public void ASessionThatClaimsThroughTheShell_ShowsTheClaimInItsDigest()
    {
        var digest = new SessionDigest();
        digest.Add(new ToolCall("Bash", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["command"] = "conductor task --done KS7.2 --evidence .conductor/evidence/KS7/ks7-2.md",
        }));
        Assert.Equal(["KS7.2 -> done"], digest.Claims);
    }

    /// <summary>The same board move made twice — once through the MCP tool, once through the CLI —
    /// is one claim, not two. Sessions do exactly this when the MCP call fails and they fall back.</summary>
    [Fact]
    public void TheSameClaimBothWays_CountsOnce()
    {
        var digest = new SessionDigest();
        digest.Add(new ToolCall("mcp__conductor-tasks__task_update", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["taskId"] = "KS7.2",
            ["status"] = "done",
        }));
        digest.Add(new ToolCall("Bash", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["command"] = "conductor task --done KS7.2 --evidence x.md",
        }));
        Assert.Equal(["KS7.2 -> done"], digest.Claims);
    }

    /// <summary>The source is stored, not inferred — a reader must be able to check on any given
    /// session whether the hook actually delivered, rather than take the design's word for it.</summary>
    [Fact]
    public void TheDigestSource_SurvivesARoundTrip()
    {
        var digest = new SessionDigest { Source = SessionDigest.HookSource, FailedCalls = 2 };
        digest.Add(new ToolCall("Read", new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "a.txt" }));
        var back = SessionDigest.FromJson(digest.ToJson());
        Assert.NotNull(back);
        Assert.Equal(SessionDigest.HookSource, back!.Source);
        Assert.Equal(2, back.FailedCalls);
        Assert.Contains("via hook", back.Summary(), StringComparison.Ordinal);
    }

    // ── helpers ──

    private static Task<int> RunAsync(TempDir temp, string events, string payload)
    {
        var cmd = new HookBudgetCommand { Payload = payload };
        return cmd.ExecuteAsync(null!, new HookBudgetCommand.Settings { StateDir = temp.Path, ToolEvents = events });
    }

    private static string Payload(string eventName, string id, string tool, object input, int? durationMs = null)
        => JsonSerializer.Serialize(new
        {
            hook_event_name = eventName,
            session_id = "s",
            tool_use_id = id,
            tool_name = tool,
            tool_input = input,
            duration_ms = durationMs,
        });

    private static string CorpusDir() =>
        Path.Combine(RepoRoot(), "tests", "Conductor.Tests", "testdata", "ks72");

    private static List<JsonElement> HookPayloads() =>
        File.ReadAllLines(Path.Combine(CorpusDir(), "probe-hooks.jsonl"))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();

    private static string? EventNameOf(JsonElement payload) =>
        payload.TryGetProperty("hook_event_name", out var el) ? el.GetString() : null;

    /// <summary>The stream's tool_use blocks, exactly as <c>ClaudeProvider</c> reads them.</summary>
    private static List<(string Id, ToolCall Call)> StreamToolUses()
    {
        var calls = new List<(string, ToolCall)>();
        foreach (var line in File.ReadAllLines(Path.Combine(CorpusDir(), "probe-stream.jsonl")))
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant") continue;
                if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) continue;
                if (content.ValueKind != JsonValueKind.Array) continue;
                foreach (var block in content.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out var bt) || bt.GetString() != "tool_use") continue;
                    var name = block.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var input = block.TryGetProperty("input", out var i) ? i : default;
                    calls.Add((block.GetProperty("id").GetString()!, ToolEventExtractor.Extract(name, input)));
                }
            }
        }
        return calls;
    }

    private static SessionDigest TranscriptDigest()
    {
        var digest = new SessionDigest();
        foreach (var (_, call) in StreamToolUses()) digest.Add(call);
        return digest;
    }

    /// <summary>The hook side of the corpus, driven through the REAL append and read — not a
    /// shortcut that hands the digest the same objects the transcript path built.</summary>
    private static SessionDigest HookDigest(out IReadOnlyList<HookToolLog.Entry> entries)
    {
        using var temp = new TempDir();
        var path = HookToolLog.PathFor(temp.Path, 1);
        var at = new DateTime(2026, 8, 18, 22, 0, 0, DateTimeKind.Utc);
        foreach (var line in File.ReadAllLines(Path.Combine(CorpusDir(), "probe-hooks.jsonl")))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            HookToolLog.TryAppendFromHookPayloadAsync(path, line, at).GetAwaiter().GetResult();
            at = at.AddSeconds(1);
        }
        entries = HookToolLog.ReadEntries(path);
        return HookToolLog.BuildDigest(path, null)!;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ks72-" + Guid.NewGuid().ToString("N")[..10]);

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
