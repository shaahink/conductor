using System.IO.Compression;
using Conductor.Core.Update;

namespace Conductor.Tests;

/// <summary>
/// SC8.3 — the half of <c>conductor update</c> that touches the operator's disk. The rename dance is
/// the only part of this verb that can leave a machine without a working <c>conductor</c>, so the
/// failure paths are measured here rather than reasoned about: what happens between the rename and
/// the copy is the whole risk.
/// </summary>
public sealed class UpdateSwapTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("conductor-swap-tests-").FullName;

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // --- the rename dance -------------------------------------------------------------------------

    [Fact]
    public void Replace_PutsTheNewBinaryInPlaceAndRemovesTheOldOne()
    {
        var destination = Write("conductor.exe", "OLD ENGINE");
        var replacement = Write("staged/conductor.exe", "NEW ENGINE");

        var result = BinarySwap.Replace(destination, replacement);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal("NEW ENGINE", File.ReadAllText(destination));
        Assert.False(File.Exists(destination + BinarySwap.RetiredSuffix), "the retired binary should be gone when it is not locked");
        Assert.Null(result.Retired);
    }

    [Fact]
    public void Replace_InstallsWhereNothingWasThere()
    {
        // The face is shipped in the same archive; an engine installed without one still gets it.
        var destination = Path.Combine(_dir, "conductor-face.exe");
        var replacement = Write("staged/conductor-face.exe", "NEW FACE");

        var result = BinarySwap.Replace(destination, replacement);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal("NEW FACE", File.ReadAllText(destination));
        Assert.Contains("was not present", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_RefusesWhenTheStagedFileIsNotThere_AndLeavesTheDestinationAlone()
    {
        var destination = Write("conductor.exe", "OLD ENGINE");
        var result = BinarySwap.Replace(destination, Path.Combine(_dir, "staged", "absent.exe"));

        Assert.False(result.Ok);
        Assert.Equal("OLD ENGINE", File.ReadAllText(destination));
    }

    [Fact]
    public void Replace_RollsBackWhenTheCopyFailsAfterTheRename()
    {
        // This is the dangerous window: the destination name has already been vacated. A crash or a
        // failed copy here without a rollback leaves the operator with NO conductor at all.
        var destination = Write("conductor.exe", "OLD ENGINE");
        var replacement = Write("staged/conductor.exe", "NEW ENGINE");

        SwapResult result;
        using (File.Open(replacement, FileMode.Open, FileAccess.Read, FileShare.None))
            result = BinarySwap.Replace(destination, replacement);

        Assert.False(result.Ok);
        Assert.True(File.Exists(destination), "the previous binary must be back at its own name");
        Assert.Equal("OLD ENGINE", File.ReadAllText(destination));
        Assert.Contains("put back", result.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(destination + BinarySwap.RetiredSuffix));
    }

    [Fact]
    public void Replace_KeepsTheRollbackWhenAPreviousRetiredFileIsStillLocked()
    {
        // A Windows update cannot delete the running image, so `conductor.exe.old` survives. The next
        // update must not reuse that name — overwriting it would throw away the only rollback copy.
        var destination = Write("conductor.exe", "OLD ENGINE");
        var replacement = Write("staged/conductor.exe", "NEW ENGINE");
        var stale = Write("conductor.exe" + BinarySwap.RetiredSuffix, "PREVIOUS ENGINE");

        // FileShare.Read, not None: deleting needs FILE_SHARE_DELETE, so the sweep still fails —
        // which is the condition under test — while the assertions below can still read the file.
        using (File.Open(stale, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = BinarySwap.Replace(destination, replacement);

            Assert.True(result.Ok, result.Detail);
            Assert.Equal("NEW ENGINE", File.ReadAllText(destination));
            // The locked file was stepped around, not through: had the swap reused that name, the
            // rollback copy for the update that parked it would be gone.
            Assert.Equal("PREVIOUS ENGINE", File.ReadAllText(stale));
            // ...and the binary this swap retired was cleaned up afterwards, since nothing holds it.
            Assert.DoesNotContain(Directory.GetFiles(_dir),
                f => Path.GetFileName(f).StartsWith("conductor.exe" + BinarySwap.RetiredSuffix + ".", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void SweepRetired_ClearsWhatAPreviousUpdateCouldNotDelete_AndTouchesNothingElse()
    {
        Write("conductor.exe", "ENGINE");
        Write("conductor.exe" + BinarySwap.RetiredSuffix, "PREVIOUS");
        Write("conductor.exe" + BinarySwap.RetiredSuffix + ".1234", "OLDER");
        Write("conductor-face.exe", "FACE");

        var swept = BinarySwap.SweepRetired(_dir);

        Assert.Equal(2, swept);
        Assert.True(File.Exists(Path.Combine(_dir, "conductor.exe")));
        Assert.True(File.Exists(Path.Combine(_dir, "conductor-face.exe")));
        Assert.Empty(Directory.GetFiles(_dir, "*" + BinarySwap.RetiredSuffix + "*"));
    }

    // --- verification -----------------------------------------------------------------------------

    [Fact]
    public void Sha256_IsTheStandardDigest()
    {
        // Known vector: sha256("abc"). A hand-rolled hex/casing slip would otherwise only surface as a
        // mismatch against a real release, which is the worst place to find it.
        var path = Write("abc.txt", "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ArchiveUnpacker.Sha256(path));
    }

    [Fact]
    public void ExpectedSha_ReadsASha256sumManifest()
    {
        const string manifest = """
            ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  conductor-linux-x64.tar.gz
            3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b *dist/conductor-windows-x64.zip
            """;
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ArchiveUnpacker.ExpectedSha(manifest, "conductor-linux-x64.tar.gz"));
        // the binary marker and a path prefix are both shapes sha256sum writes
        Assert.Equal("3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b",
            ArchiveUnpacker.ExpectedSha(manifest, "conductor-windows-x64.zip"));
        Assert.Null(ArchiveUnpacker.ExpectedSha(manifest, "conductor-macos-arm64.tar.gz"));
        Assert.Null(ArchiveUnpacker.ExpectedSha(null, "conductor-linux-x64.tar.gz"));
    }

    [Fact]
    public void Extract_UnpacksAZipAndTheEngineIsFindable()
    {
        var staged = Path.Combine(_dir, "stage");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "conductor.exe"), "ENGINE");
        File.WriteAllText(Path.Combine(staged, "conductor-face.exe"), "FACE");
        var archive = Path.Combine(_dir, "conductor-windows-x64.zip");
        ZipFile.CreateFromDirectory(staged, archive);

        var into = Path.Combine(_dir, "unpacked");
        ArchiveUnpacker.Extract(archive, into);

        var engine = ArchiveUnpacker.Find(into, "conductor.exe");
        Assert.NotNull(engine);
        Assert.Equal("ENGINE", File.ReadAllText(engine!));
        Assert.NotNull(ArchiveUnpacker.Find(into, "conductor-face.exe"));
        Assert.Null(ArchiveUnpacker.Find(into, "conductor-nope.exe"));
    }

    [Fact]
    public void Extract_RefusesAnArchiveKindNoReleasePublishes()
    {
        var archive = Write("conductor-windows-x64.rar", "not really");
        var ex = Assert.Throws<InvalidOperationException>(() => ArchiveUnpacker.Extract(archive, Path.Combine(_dir, "out")));
        Assert.Contains("unsupported archive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AskVersion_ReportsAFailureRatherThanThrowingWhenTheFileIsNotRunnable()
    {
        var notAnExe = Write("conductor.exe", "this is text, not a program");
        var (ok, output) = BinarySwap.AskVersion(notAnExe, TimeSpan.FromSeconds(10));
        Assert.False(ok);
        Assert.NotEmpty(output);
    }
}
