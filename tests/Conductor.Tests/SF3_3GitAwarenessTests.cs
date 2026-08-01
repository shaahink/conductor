using Conductor.Core;
using Conductor.Core.Face;
using Conductor.Core.Http;

namespace Conductor.Tests;

/// <summary>
/// SF3.3 — git awareness on the wire, and FU-OWNER-10's build identity beside it.
/// <para>These drive a REAL git repo rather than asserting against parsed strings alone: the branch
/// header this code reads is porcelain output, and the only way to know we read it the way git
/// writes it is to make git write it. The pure-string cases below cover the shapes that are awkward
/// to produce on demand (a detached HEAD mid-rebase, a branch behind its upstream).</para>
/// </summary>
public sealed class SF3_3GitAwarenessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cond-sf33-" + Guid.NewGuid().ToString("N")[..8]);

    public SF3_3GitAwarenessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        GitSnapshotCache.Clear();
        try { DeleteTree(_dir); } catch (IOException) { /* a git pack file still held open — the temp dir is disposable */ }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- against a real repo

    [Fact]
    public void Probe_reads_branch_head_and_subject_from_a_real_repo()
    {
        InitRepo(_dir, "feat/sarban");

        var snap = GitSnapshot.Probe(_dir);

        Assert.Equal("feat/sarban", snap.Branch);
        Assert.False(snap.Detached);
        Assert.Equal(40, snap.HeadSha.Length);
        Assert.Equal("init", snap.HeadSubject);
        Assert.False(snap.Dirty);
        Assert.Equal(0, snap.DirtyCount);
        Assert.Equal("clean", snap.DirtySummary);
        var only = Assert.Single(snap.RecentCommits);
        Assert.Equal("init", only.Subject);
        Assert.StartsWith(only.Sha, snap.HeadSha, StringComparison.Ordinal);
    }

    /// <summary>A dirty tree is dirty, is counted, and says what is dirty. The count is the fact the
    /// status strip renders; the summary is what Home spells out.</summary>
    [Fact]
    public void Probe_counts_and_summarises_a_dirty_tree()
    {
        InitRepo(_dir, "main");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "# edited");
        File.WriteAllText(Path.Combine(_dir, "untracked.txt"), "new");

        var snap = GitSnapshot.Probe(_dir);

        Assert.True(snap.Dirty);
        Assert.Equal(2, snap.DirtyCount);
        Assert.Contains("README.md", snap.DirtySummary, StringComparison.Ordinal);
        Assert.Contains("untracked.txt", snap.DirtySummary, StringComparison.Ordinal);
    }

    /// <summary>The distinction the whole record exists to protect: a branch with no upstream serves
    /// NULL ahead/behind, not 0/0. Rendering "↑0 ↓0" for a branch that was never pushed would tell
    /// the operator their work is safely on a remote when no remote has ever seen it.</summary>
    [Fact]
    public void A_branch_with_no_upstream_serves_null_ahead_behind_not_zero()
    {
        InitRepo(_dir, "main");

        var snap = GitSnapshot.Probe(_dir);

        Assert.Null(snap.Upstream);
        Assert.Null(snap.Ahead);
        Assert.Null(snap.Behind);
    }

    /// <summary>And with a real upstream, ahead is a real count. A local clone stands in for a
    /// remote: cloning gives the working copy a tracking branch without a network.</summary>
    [Fact]
    public void A_tracking_branch_reports_ahead_of_its_upstream()
    {
        var origin = Path.Combine(_dir, "origin");
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(origin);
        InitRepo(origin, "main");
        Git(_dir, "clone", origin, work);
        Git(work, "config", "user.email", "sf33@test");
        Git(work, "config", "user.name", "SF33 Test");
        File.WriteAllText(Path.Combine(work, "second.txt"), "two");
        Git(work, "add", "second.txt");
        Git(work, "commit", "-m", "the second commit", "--no-gpg-sign");

        var snap = GitSnapshot.Probe(work);

        Assert.NotNull(snap.Upstream);
        Assert.Equal(1, snap.Ahead);
        Assert.Equal(0, snap.Behind);
        Assert.Equal("the second commit", snap.HeadSubject);
        Assert.Equal(2, snap.RecentCommits.Count);
    }

    /// <summary>A path that is not a git repo is answered, not thrown at: IsRepo false, everything
    /// else empty. The Face needs to be able to SAY "not a git repo" — an exception or a null block
    /// would read exactly like an older engine that does not serve git at all.</summary>
    [Fact]
    public void A_non_repo_directory_is_not_a_repo_rather_than_an_error()
    {
        var plain = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(plain);

        var dto = GitDto.From(GitSnapshot.Probe(plain));

        Assert.False(dto.IsRepo);
        Assert.Equal("", dto.Branch);
        Assert.Equal("", dto.HeadSha);
        Assert.Empty(dto.RecentCommits);
    }

    // ---------------------------------------------------------------- the porcelain header shapes

    [Theory]
    // no upstream at all
    [InlineData("## main", "main", false, null, null, null)]
    // tracked and level: 0/0, NOT null — "in sync" is a different fact from "never pushed"
    [InlineData("## main...origin/main", "main", false, "origin/main", 0, 0)]
    [InlineData("## feat/x...origin/feat/x [ahead 2]", "feat/x", false, "origin/feat/x", 2, 0)]
    [InlineData("## feat/x...origin/feat/x [behind 3]", "feat/x", false, "origin/feat/x", 0, 3)]
    [InlineData("## feat/x...origin/feat/x [ahead 2, behind 3]", "feat/x", false, "origin/feat/x", 2, 3)]
    // detached HEAD — the state a half-finished rebase or a `git checkout <sha>` leaves behind
    [InlineData("## HEAD (no branch)", "", true, null, null, null)]
    // a fresh `git init` with no commits: a branch name and nothing else
    [InlineData("## No commits yet on master", "master", false, null, null, null)]
    public void ParseBranchLine_survives_every_porcelain_shape(
        string line, string branch, bool detached, string? upstream, int? ahead, int? behind)
    {
        var got = GitSnapshot.ParseBranchLine(line);

        Assert.Equal(branch, got.Branch);
        Assert.Equal(detached, got.Detached);
        Assert.Equal(upstream, got.Upstream);
        Assert.Equal(ahead, got.Ahead);
        Assert.Equal(behind, got.Behind);
    }

    // ---------------------------------------------------------------- the cache, which is the point

    /// <summary>The cache is not an optimisation detail, it is the reason this block can exist at
    /// all: GET /state is polled once a second by every attached Face, and git awareness must not
    /// cost two process spawns per second per viewer. What a cache does is defined by what it does
    /// NOT do, so the probe is counted and the clock is a parameter.</summary>
    [Fact]
    public void The_cache_probes_once_per_ttl_not_once_per_poll()
    {
        GitSnapshotCache.Clear();
        var probes = 0;
        var t0 = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        GitSnapshot Probe(string _) { probes++; return GitSnapshot.None with { Branch = "b" + probes }; }

        // Ten polls inside one TTL — the Face's tick rate — must cost exactly one probe.
        for (var i = 0; i < 10; i++)
            Assert.Equal("b1", GitSnapshotCache.Get("r", Probe, t0.AddMilliseconds(i * 100)).Branch);
        Assert.Equal(1, probes);

        // Past the TTL it re-reads, so a commit made in another terminal shows up on its own.
        var later = GitSnapshotCache.Get("r", Probe, t0 + GitSnapshotCache.Ttl + TimeSpan.FromMilliseconds(1));
        Assert.Equal("b2", later.Branch);
        Assert.Equal(2, probes);
    }

    /// <summary>Two repos do not share one cached answer. A machine running two conductor runs is
    /// the normal case here, not the exotic one.</summary>
    [Fact]
    public void The_cache_is_keyed_by_repo()
    {
        GitSnapshotCache.Clear();
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        var a = GitSnapshotCache.Get("repo-a", r => GitSnapshot.None with { Branch = r }, now);
        var b = GitSnapshotCache.Get("repo-b", r => GitSnapshot.None with { Branch = r }, now);

        Assert.Equal("repo-a", a.Branch);
        Assert.Equal("repo-b", b.Branch);
    }

    // ---------------------------------------------------------------- FU-OWNER-10: which build?

    /// <summary>The Face's build is read out of the binary's embedded Go build settings. Synthesised
    /// rather than taken from whatever happens to be built on the machine running the suite — the
    /// assertion is about the READER, and a suite that needs a Go toolchain to run is a worse test.
    /// (That the markers are really there is measured separately: this repo's own
    /// <c>face-go/bin/conductor-face.exe</c> carries <c>vcs.revision=</c> at ~10.5 MB.)</summary>
    [Fact]
    public void FaceBuildStamp_reads_the_go_vcs_revision_out_of_a_binary()
    {
        var fake = Path.Combine(_dir, "conductor-face.exe");
        File.WriteAllBytes(fake, SynthesiseGoBinary("7d7372ed64ffc73ce90e383c052c82c99e885fd0", modified: false));

        Assert.Equal("7d7372ed64ff", FaceBuildStamp.Describe(fake));
    }

    /// <summary>A Face built from a dirty tree says so, for the same reason the engine's own stamp
    /// does: two binaries claiming one commit are otherwise indistinguishable.</summary>
    [Fact]
    public void FaceBuildStamp_marks_a_dirty_build()
    {
        var fake = Path.Combine(_dir, "conductor-face-dirty.exe");
        File.WriteAllBytes(fake, SynthesiseGoBinary("7d7372ed64ffc73ce90e383c052c82c99e885fd0", modified: true));

        Assert.Equal("7d7372ed64ff.dirty", FaceBuildStamp.Describe(fake));
    }

    /// <summary>The rule that makes the field worth showing: a binary with no VCS stamp reports its
    /// file date IN WORDS, never a sha it did not read. An invented commit is worse than no answer —
    /// the entire follow-up exists because a guessed version was quoted and was wrong.</summary>
    [Fact]
    public void FaceBuildStamp_never_invents_a_sha()
    {
        var unstamped = Path.Combine(_dir, "no-stamp.exe");
        File.WriteAllBytes(unstamped, "not a go binary at all"u8.ToArray());

        var stamp = FaceBuildStamp.Describe(unstamped);

        Assert.StartsWith("unstamped", stamp, StringComparison.Ordinal);
        Assert.DoesNotContain("vcs", stamp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And a marker whose value is not a sha is rejected rather than printed. The markers
    /// are ASCII inside an arbitrary binary; matching one does not make the bytes after it a commit.</summary>
    [Fact]
    public void FaceBuildStamp_rejects_a_non_hex_revision()
    {
        var junk = Path.Combine(_dir, "junk.exe");
        File.WriteAllBytes(junk, "\0\0build\tvcs.revision=this-is-not-a-sha\n\0\0"u8.ToArray());

        Assert.StartsWith("unstamped", FaceBuildStamp.Describe(junk), StringComparison.Ordinal);
    }

    /// <summary>The one-line form the status strip and the CLI both render, so they cannot drift
    /// into two spellings of the same fact.</summary>
    [Fact]
    public void The_build_line_names_engine_and_face()
    {
        Assert.Equal("engine 0.1.1-alpha+2fea70327497 · face 7d7372ed64ff",
            FaceBuildStamp.Line("0.1.1-alpha", "2fea70327497d9c", "7d7372ed64ff"));
        // No Face built: the engine half still answers rather than the whole line disappearing.
        Assert.Equal("engine 0.1.1-alpha+2fea70327497",
            FaceBuildStamp.Line("0.1.1-alpha", "2fea70327497d9c", ""));
        // An engine with no git at build time says its version and stops — "unknown" is not a commit.
        Assert.Equal("engine 0.1.1-alpha", FaceBuildStamp.Line("0.1.1-alpha", BuildInfo.UnknownCommit, ""));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The bytes a Go build embeds for a VCS-stamped binary, padded so the markers are not
    /// at offset zero — the reader scans, it does not assume a position.</summary>
    private static byte[] SynthesiseGoBinary(string revision, bool modified)
    {
        var head = new byte[4096];
        var settings = System.Text.Encoding.ASCII.GetBytes(
            $"  build\tvcs=git\nbuild\tvcs.revision={revision}\nbuild\tvcs.time=2026-07-31T03:14:42Z\n" +
            $"build\tvcs.modified={(modified ? "true" : "false")}\n  ");
        return [.. head, .. settings, .. new byte[1024]];
    }

    private static void InitRepo(string dir, string branch)
    {
        Git(dir, "init", "-b", branch);
        Git(dir, "config", "user.email", "sf33@test");
        Git(dir, "config", "user.name", "SF33 Test");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# t");
        Git(dir, "add", "README.md");
        Git(dir, "commit", "-m", "init", "--no-gpg-sign");
    }

    private static void Git(string dir, params string[] args)
    {
        var r = ProcessRunner.Run("git", args, dir, TimeSpan.FromSeconds(60));
        // Asserted, not fired and forgotten: a silently failing setup command is how a git assertion
        // becomes vacuous (bug 8, fixed in SF0.2 for exactly this reason).
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)} failed ({r.ExitCode}): {r.Output}");
    }

    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }
}
