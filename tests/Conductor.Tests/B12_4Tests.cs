using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Models;
using Xunit;

namespace Conductor.Tests;

public sealed class B12_4Tests
{
    // ---------------------------------------------------------------- FollowupParser

    [Fact]
    public void FollowupParser_Read_ParsesStandardTable()
    {
        var path = WriteTempFile("followups-test.md", """
            ## Opened by B10

            | id | item | detail | owning stage | status |
            |---|---|---|---|---|
            | FU-B10-1 | No orchestrator harness | The readiness-ordering logic is untested. | B12 fix-lane | OPEN |
            | FU-B10-2 | Battery-collapse not measured | No automated metric compares pre/post tokens. | B12 | OPEN |
            """);

        try
        {
            var entries = FollowupParser.Read(path);
            Assert.Equal(2, entries.Count);

            Assert.Equal("FU-B10-1", entries[0].Id);
            Assert.Equal("No orchestrator harness", entries[0].Item);
            Assert.Equal("The readiness-ordering logic is untested.", entries[0].Detail);
            Assert.Equal("B12 fix-lane", entries[0].OwningStage);
            Assert.Equal("OPEN", entries[0].Status);

            Assert.Equal("FU-B10-2", entries[1].Id);
            Assert.Equal("Battery-collapse not measured", entries[1].Item);
            Assert.Equal("B12", entries[1].OwningStage);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void FollowupParser_Read_IgnoresClosedAndNonFU()
    {
        var path = WriteTempFile("followups-closed.md", """
            # Header
            | id | item | owning stage | status |
            |---|---|---|---|
            | FU-B0-1 | Sync-over-async | B2 | CLOSED |
            | FU-B0-2 | StringComparer | post-B2 | OPEN |
            | OTHER-1 | Not a followup | X | OPEN |
            """);

        try
        {
            var entries = FollowupParser.Read(path);
            // FU-B0-1 is CLOSED but we still parse it (status filter is in ReadOpenForStage)
            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.StartsWith("FU-", e.Id));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void FollowupParser_ReadOpenForStage_FiltersCorrectly()
    {
        var path = WriteTempFile("followups-filter.md", """
            | id | item | owning stage | status |
            |---|---|---|---|
            | FU-B10-1 | Harness gap | B12 fix-lane | OPEN |
            | FU-B10-2 | Battery metric | B12 | OPEN |
            | FU-B0-1 | Sync-over-async | B2 | OPEN |
            | FU-B3-1 | Loop harness | B12 fix-lane | CLOSED |
            """);

        try
        {
            var entries = FollowupParser.ReadOpenForStage(path, "B12");
            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.Contains("B12", e.OwningStage));
            Assert.All(entries, e => Assert.Equal("OPEN", e.Status));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task FollowupParser_UpdateStatus_WritesCorrectly()
    {
        var path = WriteTempFile("followups-update.md", """
            | id | item | owning stage | status |
            |---|---|---|---|
            | FU-B10-1 | Harness gap | B12 fix-lane | OPEN |
            | FU-B10-2 | Battery metric | B12 | OPEN |
            """);

        try
        {
            var ok = FollowupParser.UpdateStatus(path, "FU-B10-1", "CLOSED", "abc1234");
            Assert.True(ok);

            var updated = await File.ReadAllTextAsync(path);
            Assert.Contains("CLOSED (abc1234)", updated);
            Assert.Contains("OPEN", updated); // FU-B10-2 still OPEN

            // Re-parse to verify
            var entries = FollowupParser.Read(path);
            var e1 = entries.First(e => e.Id == "FU-B10-1");
            Assert.Contains("CLOSED", e1.Status);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void FollowupParser_HandlesVariableColumnSchemes()
    {
        // B0-style table: | id | rule | sites | why deferred | owning stage | status |
        var path = WriteTempFile("followups-b0-style.md", """
            ## Analyzer ratchets
            | id | rule | sites | why deferred | owning stage | status |
            |---|---|---|---|---|---|
            | FU-B0-1 | MA0045 (sync-over-async) | ~28 | Async rework is B2 | B2 | OPEN |
            | FU-B0-2 | MA0002 (StringComparer) | ~38 | Mechanical | post-B2 | OPEN |
            """);

        try
        {
            var entries = FollowupParser.Read(path);
            Assert.Equal(2, entries.Count);
            Assert.Equal("MA0045 (sync-over-async)", entries[0].Item);
            Assert.Equal("Async rework is B2", entries[0].Detail);
            Assert.Equal("B2", entries[0].OwningStage);
        }
        finally { TryDelete(path); }
    }

    // ---------------------------------------------------------------- fix-lane end-to-end

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FixLane_FromFollowup_MergeGateAcceptsAndUpdatesStatus()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            // Write a followups.md with one OPEN entry
            var followupsPath = Path.Combine(repo, ".conductor", "followups.md");
            Directory.CreateDirectory(Path.GetDirectoryName(followupsPath)!);
            await File.WriteAllTextAsync(followupsPath, """
                | id | item | detail | owning stage | status |
                |---|---|---|---|---|
                | FU-B12-T1 | Add project description to README | The README needs a project description line. | B12 | OPEN |
                """);

            // Verify parser sees it
            var entries = FollowupParser.ReadOpenForStage(followupsPath, "B12");
            Assert.Single(entries);
            Assert.Equal("FU-B12-T1", entries[0].Id);

            // Create a MutatingLaneConfig from the followup
            var lane = new MutatingLaneConfig
            {
                Id = $"fix-{entries[0].Id.ToLowerInvariant()}",
                Kind = "fix",
                Name = $"Fix: {entries[0].Item}",
                Prompt = $"Fix: {entries[0].Item}\n\n{entries[0].Detail}\n\nAdd a line to README.md describing the project.",
                TimeoutMinutes = 5,
                MergeGates = new List<GateConfig>
                {
                    new() { Name = "verify-readme", Command = "if (Select-String -Path README.md -Pattern 'orchestration') { exit 0 } else { exit 1 }", Shell = "powershell", TimeoutMinutes = 1 },
                },
            };

            // PlanConfig pointing at the test repo with the same merge gate
            var plan = new PlanConfig
            {
                Name = "fix-lane-test",
                Repo = repo,
                Gates = lane.MergeGates!, // same gate for plan-level fallback (not needed here)
                Agent = new AgentConfig
                {
                    Command = "cmd",
                    Args = new() { "/c", "echo Fixing: adding project description && echo Conductor is a stateful AI orchestration tool.>> README.md && git add README.md && git commit -m fix-add-readme-desc" },
                },
            };

            var sink = new CollectingEventSink();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var result = await MutatingLaneRunner.RunAsync(
                plan, lane, plan.Agent, "B12", sink, null, cts.Token);

            Assert.True(result.IsSuccess, $"fix-lane should succeed but: {result.Error}");
            Assert.True(result.AgentCommitted);
            Assert.True(result.MergeGatePassed);
            Assert.True(result.Merged);

            // Verify the merge gate event was emitted
            var events = sink.Events;
            Assert.Contains(events, e => e is MutatingLaneStarted ml && ml.LaneId == lane.Id);
            Assert.Contains(events, e => e is MergeGateVerdict mgv && mgv.Passed);
            Assert.Contains(events, e => e is MutatingLaneFinished mlf && mlf.Outcome == "success");

            // Verify the fix was actually merged into the primary tree
            var readme = await File.ReadAllTextAsync(Path.Combine(repo, "README.md"), cts.Token);
            Assert.Contains("orchestration", readme, StringComparison.OrdinalIgnoreCase);

            // Update the followup status
            var head = Git.Head(repo);
            var updated = FollowupParser.UpdateStatus(followupsPath, "FU-B12-T1", "CLOSED", head[..7]);
            Assert.True(updated);

            var finalEntries = FollowupParser.Read(followupsPath);
            var closed = finalEntries.First(e => e.Id == "FU-B12-T1");
            Assert.Contains("CLOSED", closed.Status);
        }
        finally { cleanup(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FixLane_BadDiff_MergeGateRejects()
    {
        var (repo, cleanup) = CreateTestRepo();
        try
        {
            var followupsPath = Path.Combine(repo, ".conductor", "followups.md");
            Directory.CreateDirectory(Path.GetDirectoryName(followupsPath)!);
            await File.WriteAllTextAsync(followupsPath, """
                | id | item | owning stage | status |
                |---|---|---|---|
                | FU-B12-T2 | Bad fix that breaks build | B12 | OPEN |
                """);

            var lane = new MutatingLaneConfig
            {
                Id = "fix-bad",
                Kind = "fix",
                Name = "Fix: bad fix",
                Prompt = "Do something bad.",
                TimeoutMinutes = 5,
                MergeGates = new List<GateConfig>
                {
                    new() { Name = "should-fail", Command = "exit 1", Shell = "powershell", TimeoutMinutes = 1 },
                },
            };

            var plan = new PlanConfig
            {
                Name = "fix-lane-reject-test",
                Repo = repo,
                Agent = new AgentConfig
                {
                    Command = "cmd",
                    Args = new() { "/c", "echo bad change>bad.txt && git add bad.txt && git commit -m bad-change-rejected" },
                },
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await MutatingLaneRunner.RunAsync(
                plan, lane, plan.Agent, "B12", null, null, cts.Token);

            Assert.True(result.AgentCommitted, "agent should have committed");
            Assert.False(result.MergeGatePassed, "merge gate should have failed");
            Assert.False(result.Merged);
            Assert.False(result.IsSuccess);

            // Primary tree should be untouched
            Assert.False(File.Exists(Path.Combine(repo, "bad.txt")),
                "primary tree should not contain the bad change");
        }
        finally { cleanup(); }
    }

    [Fact]
    public void FollowupParser_EmptyFile_ReturnsEmpty()
    {
        var path = WriteTempFile("followups-empty.md", "");
        try
        {
            var entries = FollowupParser.Read(path);
            Assert.Empty(entries);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void FollowupParser_NoFile_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.md");
        var entries = FollowupParser.Read(path);
        Assert.Empty(entries);
    }

    [Fact]
    public void FollowupParser_UpdateStatus_NonexistentFile_ReturnsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.md");
        var ok = FollowupParser.UpdateStatus(path, "FU-X", "CLOSED", null);
        Assert.False(ok);
    }

    // ---------------------------------------------------------------- helpers

    private static (string repoPath, Action cleanup) CreateTestRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-b12_4-test-{Guid.NewGuid():N}"[..42]);
        Directory.CreateDirectory(repo);

        Git.Exec(repo, "init", "-b", "main");
        Git.Exec(repo, "config", "user.email", "conductor@test.local");
        Git.Exec(repo, "config", "user.name", "Conductor Test");

        File.WriteAllText(Path.Combine(repo, "README.md"), "# Test Repo\n");
        Git.Exec(repo, "add", "README.md");
        Git.Exec(repo, "commit", "-m", "initial commit");

        void Cleanup()
        {
            try
            {
                var gitDir = Path.Combine(repo, ".git");
                if (Directory.Exists(gitDir))
                    foreach (var f in Directory.GetFiles(gitDir, "*", SearchOption.AllDirectories))
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(repo, recursive: true);
            }
            catch { }
        }

        return (repo, Cleanup);
    }

    private static string WriteTempFile(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-b12_4-{name}");
        File.WriteAllText(path, content);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
