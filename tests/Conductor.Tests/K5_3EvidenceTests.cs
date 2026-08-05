using System.Security.Cryptography;
using System.Text;
using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Core.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K5.3 — evidence as a first-class artifact. The engine half landed in <c>e618c06</c> with no test
/// over any of it; this is that test.
///
/// <para>What these have to prove, in the order the checkpoint states it: a non-text kind is
/// first-class (a PNG is the case the item exists for, and "text or other" would have made the
/// motivating case the fallback); the artifact carries path, kind, checkpoint, session, sha and
/// created-at; the registry is a FOLD of events, so it survives the round trip through the store
/// rather than being a directory scan wearing a different name; and the free-text
/// <c>--evidence</c> field is untouched — a registry that breaks every existing claim is not an
/// improvement, so the claim row is asserted to still read back exactly what was written.</para>
/// </summary>
public sealed class K5_3EvidenceTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"conductor-ev-{Guid.NewGuid():N}");

    public K5_3EvidenceTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_repo); } catch (IOException) { /* best effort */ }
    }

    private string StateDir => Path.Combine(_repo, ".conductor");

    /// <summary>A real PNG header, not a text file called .png: the kind must come from what the file
    /// IS to a surface that will try to send it, and the hash must be over real binary bytes.</summary>
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
        0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
    ];

    private string WriteFile(string relativePath, byte[] bytes)
    {
        var full = Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    private string WriteText(string relativePath, string text) =>
        WriteFile(relativePath, Encoding.UTF8.GetBytes(text));

    // ------------------------------------------------------------------ kinds

    [Theory]
    [InlineData("shot.png", EvidenceKinds.Image)]
    [InlineData("dashboard.JPEG", EvidenceKinds.Image)]
    [InlineData("diagram.svg", EvidenceKinds.Image)]
    [InlineData("walkthrough.mp4", EvidenceKinds.Video)]
    [InlineData("narration.m4a", EvidenceKinds.Audio)]
    [InlineData("K5.3-notes.md", EvidenceKinds.Text)]
    [InlineData("gate.log", EvidenceKinds.Text)]
    [InlineData("LICENSE", EvidenceKinds.Text)]
    [InlineData("timings.csv", EvidenceKinds.Data)]
    [InlineData("frame.html", EvidenceKinds.Data)]
    [InlineData("bundle.zip", EvidenceKinds.Archive)]
    [InlineData("conductor.exe", EvidenceKinds.Binary)]
    public void FromPath_MakesTheScreenshotKindsFirstClass(string name, string expected) =>
        Assert.Equal(expected, EvidenceKinds.FromPath(name));

    [Fact]
    public void IsVisual_IsTrueForExactlyTheKindsAChatCanRenderInline()
    {
        Assert.True(EvidenceKinds.IsVisual(EvidenceKinds.Image));
        Assert.True(EvidenceKinds.IsVisual(EvidenceKinds.Video));
        Assert.False(EvidenceKinds.IsVisual(EvidenceKinds.Text));
        Assert.False(EvidenceKinds.IsVisual(EvidenceKinds.Archive));
        Assert.False(EvidenceKinds.IsVisual("IMAGE")); // ordinal on purpose: the vocabulary is lower-case
    }

    // ------------------------------------------------------------------ the reader

    [Fact]
    public async Task ReadAsync_ReadsAPngAsAnImageArtifact_WithTheWholeModelPopulated()
    {
        var full = WriteFile(".conductor/evidence/K5/K5.3-face-surface.png", PngBytes);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero));

        var artifact = await EvidenceReader.ReadAsync(full, _repo, "K5.3", 19, "claim", clock);

        Assert.NotNull(artifact);
        Assert.Equal(".conductor/evidence/K5/K5.3-face-surface.png", artifact.Path);
        Assert.Equal(EvidenceKinds.Image, artifact.Kind);
        Assert.Equal("K5.3", artifact.CheckpointId);
        Assert.Equal("K5", artifact.StageId);
        Assert.Equal(19, artifact.SessionNumber);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(PngBytes)), artifact.Sha256);
        Assert.Equal(PngBytes.Length, artifact.Bytes);
        Assert.Equal(clock.GetUtcNow(), artifact.CreatedUtc);
        Assert.Equal("claim", artifact.Source);
    }

    /// <summary>The checkpoint id is RECOVERED from the file name rather than demanded, because the
    /// convention already exists — every evidence file in this repo is named for its checkpoint. A
    /// watcher has no claim to read it off, so this is the only way a dropped screenshot arrives
    /// knowing what it evidences.</summary>
    [Theory]
    [InlineData(".conductor/evidence/K5/K5.3-wire.md", "K5.3", "K5")]
    [InlineData(".conductor/evidence/SF6/SF6_1-budget.txt", "SF6.1", "SF6")]
    [InlineData("docs/evidence/K1/K1.4-mcp-merge.log", "K1.4", "K1")]
    [InlineData(".conductor/evidence/K5/screenshot.png", null, "K5")]
    public async Task ReadAsync_RecoversCheckpointAndStageFromThePathConventionTheRepoAlreadyUses(
        string rel, string? checkpoint, string? stage)
    {
        var full = WriteText(rel, "x");
        var artifact = await EvidenceReader.ReadAsync(full, _repo, null, null, "watcher");
        Assert.NotNull(artifact);
        Assert.Equal(checkpoint, artifact.CheckpointId);
        Assert.Equal(stage, artifact.StageId);
    }

    /// <summary>An explicit checkpoint from a claim beats the guess from the file name — the claim
    /// knows, the regex infers.</summary>
    [Fact]
    public async Task ReadAsync_PrefersTheClaimsCheckpointOverTheNameItInferred()
    {
        var full = WriteText(".conductor/evidence/K5/K5.1-old-name.md", "x");
        var artifact = await EvidenceReader.ReadAsync(full, _repo, "K5.3", 19, "claim");
        Assert.Equal("K5.3", artifact!.CheckpointId);
    }

    /// <summary>Total, never throwing: registration runs at a session boundary and must never be able
    /// to fail a claim, so a file that vanished between the scan and the read is a null, not an
    /// exception that takes the session's verdict with it.</summary>
    [Fact]
    public async Task ReadAsync_IsTotal_AVanishedOrUnreadablePathIsNullNotAThrow()
    {
        Assert.Null(await EvidenceReader.ReadAsync(Path.Combine(_repo, "gone.png"), _repo, null, null, "claim"));
        Assert.Null(await EvidenceReader.ReadAsync(Path.Combine(_repo, "no-such-dir", "x.png"), _repo, null, null, "claim"));
    }

    /// <summary>A file outside the repo keeps its absolute path rather than a <c>../../..</c> ladder —
    /// a surface has to be able to open what it is shown.</summary>
    [Fact]
    public async Task ReadAsync_KeepsAnAbsolutePathForAFileOutsideTheRepo()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"conductor-ev-outside-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(outside, PngBytes);
        try
        {
            var artifact = await EvidenceReader.ReadAsync(outside, _repo, null, null, "claim");
            Assert.NotNull(artifact);
            Assert.DoesNotContain("../", artifact.Path, StringComparison.Ordinal);
            Assert.Equal(outside.Replace('\\', '/'), artifact.Path);
        }
        finally { File.Delete(outside); }
    }

    // ------------------------------------------------------------------ resolving the free-text field

    [Fact]
    public void ResolvePath_ResolvesTheShapesRealClaimsActuallyCarry()
    {
        var inRepo = WriteText(".conductor/evidence/K5/K5.3-notes.md", "x");
        WriteText(".conductor/gate.log", "x");

        // The repo-relative path an agent types into --evidence.
        Assert.Equal(Path.GetFullPath(inRepo),
            EvidenceReader.ResolvePath(".conductor/evidence/K5/K5.3-notes.md", _repo, StateDir));
        // Backticked, because that is how a path arrives from a markdown-minded agent.
        Assert.Equal(Path.GetFullPath(inRepo),
            EvidenceReader.ResolvePath("`.conductor/evidence/K5/K5.3-notes.md`", _repo, StateDir));
        // Absolute.
        Assert.Equal(Path.GetFullPath(inRepo), EvidenceReader.ResolvePath(inRepo, _repo, StateDir));
        // Relative to the STATE dir, not the repo — the other root a claim may be speaking from.
        Assert.NotNull(EvidenceReader.ResolvePath("gate.log", _repo, StateDir));
    }

    /// <summary>The free-text field is free text. Most of it is a sentence, and a sentence must
    /// resolve to nothing at all — quietly, leaving the claim exactly as it was.</summary>
    [Theory]
    [InlineData("all 1547 tests green, see the gate log")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".conductor/evidence/K5/not-written-yet.md")]
    public void ResolvePath_IsNullForAnythingThatIsNotAFileOnDisk(string evidence) =>
        Assert.Null(EvidenceReader.ResolvePath(evidence, _repo, StateDir));

    [Fact]
    public void ResolvePath_RejectsASentenceBeforeTouchingTheFilesystem()
    {
        Assert.Null(EvidenceReader.ResolvePath(new string('x', 401), _repo, StateDir));
        Assert.Null(EvidenceReader.ResolvePath("line one\nline two", _repo, StateDir));
        Assert.Null(EvidenceReader.ResolvePath(null, _repo, StateDir));
    }

    // ------------------------------------------------------------------ the registry

    private static EvidenceArtifact Artifact(string path, string sha, string kind = EvidenceKinds.Text,
        string? checkpoint = null, int? session = null) =>
        new(path, kind, checkpoint, "K5", session, sha, 10, DateTimeOffset.UnixEpoch, "claim");

    [Fact]
    public void Registry_IdentityIsPathPlusBytes_SoAReClaimIsOneArtifactAndAnEditIsTwo()
    {
        var registry = new EvidenceRegistry();
        Assert.True(registry.Add(Artifact("e/a.md", "sha-1")));
        // The same claim registered twice — one artifact. This is the whole reason a watcher can run
        // every session without re-announcing the same screenshot.
        Assert.False(registry.Add(Artifact("e/a.md", "sha-1")));
        // Edited bytes at the same path — honestly a second artifact.
        Assert.True(registry.Add(Artifact("e/a.md", "sha-2")));
        // Same bytes at a different path — also a second, because the path is what a surface shows.
        Assert.True(registry.Add(Artifact("e/b.md", "sha-1")));
        Assert.Equal(3, registry.Count);
        Assert.True(registry.Knows(Artifact("e/a.md", "sha-2")));
        Assert.False(registry.Knows(Artifact("e/c.md", "sha-1")));
    }

    [Fact]
    public void Registry_ForCheckpointAndLatest_AreWhatASurfaceAsksFor()
    {
        var registry = new EvidenceRegistry();
        registry.Add(Artifact("e/1.md", "s1", checkpoint: "K5.1"));
        registry.Add(Artifact("e/2.png", "s2", EvidenceKinds.Image, "K5.3"));
        registry.Add(Artifact("e/3.md", "s3", checkpoint: "k5.3")); // case-insensitive, like every other id here

        Assert.Equal(["e/2.png", "e/3.md"], registry.ForCheckpoint("K5.3").Select(a => a.Path));
        Assert.Equal(["e/3.md", "e/2.png"], registry.Latest(2).Select(a => a.Path)); // newest first
        Assert.Equal(3, registry.Latest(99).Count);
        Assert.Empty(registry.Latest(0));
        Assert.Empty(registry.Latest(-1));
    }

    /// <summary>The registry is a fold of events, so it replays. This is the difference between an
    /// evidence registry and a directory scan wearing a different name: delete the file and the run
    /// still knows it existed, and still knows which session produced it.</summary>
    [Fact]
    public void Registry_FoldsTheEventLog_AndIgnoresEverythingElseInIt()
    {
        var registry = EvidenceRegistry.From(
        [
            new RunStarted { Plan = "p", Repo = "r" },
            new EvidenceRegistered
            {
                Path = ".conductor/evidence/K5/shot.png", Kind = EvidenceKinds.Image, Sha256 = "abc",
                Bytes = 2048, CheckpointId = "K5.3", StageId = "K5", SessionNumber = 19, Source = "watcher",
                Ts = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero),
            },
            new StageEntered { StageId = "K5", Title = "K5" },
            new EvidenceRegistered
            {
                Path = ".conductor/evidence/K5/shot.png", Kind = EvidenceKinds.Image, Sha256 = "abc",
                Bytes = 2048, Source = "claim", // replayed twice: the fold de-dupes on the same key
            },
        ]);

        var artifact = Assert.Single(registry.Artifacts);
        Assert.Equal(EvidenceKinds.Image, artifact.Kind);
        Assert.Equal("K5.3", artifact.CheckpointId);
        Assert.Equal(19, artifact.SessionNumber);
        Assert.Equal(2048, artifact.Bytes);
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero), artifact.CreatedUtc);
        Assert.Equal("watcher", artifact.Source);
    }

    // ------------------------------------------------------------------ the watcher

    [Fact]
    public async Task Watcher_ReturnsOnlyWhatTheRegistryHasNotSeen_AndDoesNotMutateIt()
    {
        var dir = Path.Combine(StateDir, "evidence", "K5");
        WriteText(".conductor/evidence/K5/K5.3-first.md", "first");
        WriteText(".conductor/evidence/K5/second.png", "second");

        var registry = new EvidenceRegistry();
        var first = await EvidenceWatcher.ScanAsync([dir], registry, _repo, 19);
        Assert.Equal(2, first.Count);
        Assert.Equal("K5.3", first[0].CheckpointId);
        Assert.Equal(EvidenceKinds.Image, first.Single(a => a.Path.EndsWith(".png", StringComparison.Ordinal)).Kind);
        Assert.All(first, a => Assert.Equal("watcher", a.Source));
        Assert.All(first, a => Assert.Equal(19, a.SessionNumber));

        // The scan does not record: a crash between scan and emit re-finds the same files.
        Assert.Equal(0, registry.Count);
        Assert.Equal(2, (await EvidenceWatcher.ScanAsync([dir], registry, _repo, 19)).Count);

        foreach (var a in first) registry.Add(a);
        Assert.Empty(await EvidenceWatcher.ScanAsync([dir], registry, _repo, 20));

        // An EDITED file is new again — same path, different bytes.
        WriteText(".conductor/evidence/K5/K5.3-first.md", "first, corrected");
        var second = Assert.Single(await EvidenceWatcher.ScanAsync([dir], registry, _repo, 20));
        Assert.EndsWith("K5.3-first.md", second.Path, StringComparison.Ordinal);
        Assert.Equal(20, second.SessionNumber);
    }

    [Fact]
    public async Task Watcher_StopsAtMaxPerScan_BecauseAThousandFilesIsADifferentProblem()
    {
        var dir = Path.Combine(StateDir, "evidence", "K5");
        for (var i = 0; i < EvidenceWatcher.MaxPerScan + 12; i++)
            WriteText($".conductor/evidence/K5/file-{i:D3}.md", $"body {i}");

        var found = await EvidenceWatcher.ScanAsync([dir], new EvidenceRegistry(), _repo, 19);
        Assert.Equal(EvidenceWatcher.MaxPerScan, found.Count);
    }

    [Fact]
    public async Task Watcher_SurvivesADirectoryThatIsNotThere_AndRecursesIntoStageFolders()
    {
        WriteText(".conductor/evidence/K5/nested.md", "x");
        var roots = EvidenceWatcher.DefaultDirectories(_repo, StateDir);

        // docs/evidence does not exist in this repo; the state dir's evidence/ does.
        Assert.Equal(2, roots.Count);
        var found = await EvidenceWatcher.ScanAsync(roots, new EvidenceRegistry(), _repo, 19);
        Assert.Equal(".conductor/evidence/K5/nested.md", Assert.Single(found).Path);

        Assert.Empty(await EvidenceWatcher.ScanAsync(
            [Path.Combine(_repo, "nowhere"), "", null!], new EvidenceRegistry(), _repo, 19));
    }

    // ------------------------------------------------------------------ the store: the event survives, the claim is untouched

    /// <summary>The two halves that a unit test over records cannot reach: an
    /// <see cref="EvidenceRegistered"/> has to survive the polymorphic round trip through run.db (a
    /// missing <c>JsonDerivedType</c> is a silent write failure, which is how this project has lost
    /// events before), and the claim's free-text evidence has to read back BYTE FOR BYTE after the
    /// artifact was made from it.</summary>
    [Fact]
    public async Task Store_RoundTripsTheEventAndLeavesTheFreeTextClaimExactlyAsItWas()
    {
        var dbPath = Path.Combine(_repo, "run.db");
        using var store = new SqliteRunStore(dbPath, NullLogger<SqliteRunStore>.Instance);
        const string runId = "run-k53";
        store.SetRunId(runId);

        // 1. A real claim, made the way `conductor task --done <id> --evidence <path>` makes it.
        const string claimed = ".conductor/evidence/K5/K5.3-evidence-artifact.md";
        WriteText(claimed, "the artifact");
        store.SeedCheckpoints(runId, [("K5.3", "K5", "evidence is first class", "TODO", "-", "-")]);
        store.UpdateCheckpoint(runId, "K5.3", "DONE", "abc1234", claimed, source: "agent");

        var row = Assert.Single(store.GetCheckpoints(runId), c => c.Id == "K5.3");
        Assert.Equal(claimed, row.Evidence); // the free-text field, untouched and still stored

        // 2. The claim leg of RunLoop.RegisterEvidenceAsync, on the real row it reads.
        var resolved = EvidenceReader.ResolvePath(row.Evidence, _repo, StateDir);
        Assert.NotNull(resolved);
        var artifact = await EvidenceReader.ReadAsync(resolved, _repo, row.Id, 19, "claim");
        Assert.NotNull(artifact);
        Assert.Equal("K5.3", artifact.CheckpointId);

        // 3. The event survives run.db and folds back into the same artifact.
        store.Emit(new EvidenceRegistered
        {
            Path = artifact.Path, Kind = artifact.Kind, Sha256 = artifact.Sha256, Bytes = artifact.Bytes,
            CheckpointId = artifact.CheckpointId, StageId = artifact.StageId,
            SessionNumber = artifact.SessionNumber, Source = artifact.Source,
        });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!store.ReadAllEvents(runId).OfType<EvidenceRegistered>().Any() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var folded = Assert.Single(EvidenceRegistry.From(store.ReadAllEvents(runId)).Artifacts);
        Assert.Equal(artifact.Path, folded.Path);
        Assert.Equal(artifact.Sha256, folded.Sha256);
        Assert.Equal(EvidenceKinds.Text, folded.Kind);
        Assert.Equal(19, folded.SessionNumber);

        // 4. And the claim STILL reads back exactly what the agent wrote — the point of the whole
        //    "keep the free-text field working" clause.
        Assert.Equal(claimed, Assert.Single(store.GetCheckpoints(runId), c => c.Id == "K5.3").Evidence);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
