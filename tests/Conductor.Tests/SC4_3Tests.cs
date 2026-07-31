using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>SC4.3 — multi-repo honesty, a gate cache key that covers the gate's own world, and a
/// freshness check that can see uncommitted work.
///
/// <para>Every git-touching test here builds REAL repositories in a temp directory and drives the
/// shipped code paths over them. The bugs this checkpoint closes are all "the code read the wrong
/// repo / the wrong clock", which source reading is exactly the wrong instrument to catch.</para>
/// </summary>
public sealed class SC4_3Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cond-sc43-" + Guid.NewGuid().ToString("N")[..8]);

    public SC4_3Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- helpers

    private static ProcResult Git_(string dir, params string[] args) =>
        ProcessRunner.Run("git", args, dir, TimeSpan.FromSeconds(60), CancellationToken.None);

    private string NewRepo(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Git_(dir, "init", "-b", "main");
        Git_(dir, "config", "user.email", "sc43@test");
        Git_(dir, "config", "user.name", "SC43 Test");
        Git_(dir, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# " + name);
        Git_(dir, "add", "README.md");
        Git_(dir, "commit", "-m", "chore: initial commit", "--no-gpg-sign");
        return dir;
    }

    private static string Commit(string repo, string file, string content, string subject)
    {
        File.WriteAllText(Path.Combine(repo, file), content);
        Git_(repo, "add", file);
        Git_(repo, "commit", "-m", subject, "--no-gpg-sign");
        return Git.Head(repo);
    }

    private static PlanConfig PlanOn(string repo, params string[] satellites) => new()
    {
        Repo = repo,
        SatelliteRepos = [.. satellites],
        Gates = [new GateConfig { Name = "build", Command = "exit 0" }],
    };

    // ---------------------------------------------------------------- (1) satellites are diffed

    [Fact]
    public void Resolve_takes_relative_paths_and_drops_the_primary_and_duplicates()
    {
        var primary = NewRepo("primary");
        var sat = NewRepo("sibling");

        // "sibling" relative to the primary, the same repo again absolutely, the primary itself,
        // and a blank — the honest answer is one satellite.
        var plan = PlanOn(primary, "../sibling", sat, primary, "   ");
        var resolved = SatelliteRepos.Resolve(plan);

        Assert.Single(resolved);
        Assert.Equal("sibling", resolved[0].Label);
        Assert.Equal(Path.GetFullPath(sat).TrimEnd(Path.DirectorySeparatorChar), resolved[0].Path);
    }

    [Fact]
    public void A_commit_in_a_satellite_is_seen_even_though_the_primary_repo_never_moved()
    {
        var primary = NewRepo("primary");
        var sat = NewRepo("sibling");
        var plan = PlanOn(primary, sat);

        var heads = SatelliteRepos.Heads(plan);
        var primaryHead = Git.Head(primary);
        Assert.Single(heads);

        Commit(sat, "feature.txt", "work", "feat: deliver the checkpoint here");

        // The primary repo is untouched — this is the sk #3 shape exactly.
        Assert.Empty(Git.CommitsSince(primary, primaryHead));

        var satCommits = SatelliteRepos.CommitsSince(plan, heads);
        Assert.Single(satCommits);
        Assert.Contains("feat: deliver the checkpoint here", satCommits[0], StringComparison.Ordinal);
        Assert.EndsWith("[sibling]", satCommits[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_verdicts_progress_signal_counts_satellite_work_and_still_drops_bookkeeping()
    {
        var rec = new SessionRecord
        {
            NewCommits = ["aaaaaaa chore(conductor): status"],
            SatelliteCommits =
            [
                "bbbbbbb chore(conductor): status [sibling]",
                "ccccccc feat: the actual delivery [sibling]",
            ],
        };

        var work = SessionProgress.WorkCommits(rec);

        // The label is a SUFFIX so SC4.2's subject test still sees past the sha. A prefix would have
        // made the satellite's bookkeeping commit indistinguishable from work.
        Assert.Single(work);
        Assert.Contains("the actual delivery", work[0], StringComparison.Ordinal);
        Assert.True(SessionProgress.HasWorkCommits(rec));
    }

    [Fact]
    public void A_session_with_only_satellite_bookkeeping_has_no_work_commits()
    {
        var rec = new SessionRecord { SatelliteCommits = ["bbbbbbb chore(conductor): status [sibling]"] };
        Assert.False(SessionProgress.HasWorkCommits(rec));
        Assert.Null(SessionProgress.LastSatelliteCommitRef(rec));
    }

    [Fact]
    public void The_checkpoint_commit_ref_names_the_satellite_a_bare_sha_would_not_resolve_in()
    {
        var rec = new SessionRecord { SatelliteCommits = ["ccccccc feat: delivery [sibling]"] };
        Assert.Equal("ccccccc@sibling", SessionProgress.LastSatelliteCommitRef(rec));
    }

    [Fact]
    public void Workflow_hasCommits_is_true_for_a_session_that_only_committed_in_a_satellite()
    {
        var rec = new SessionRecord
        {
            NewCommits = [],
            SatelliteCommits = ["ccccccc feat: delivery [sibling]"],
            NewlyDone = [],
        };
        var vars = WorkflowVarsFactory.Build(rec, stageAttempts: 1, gatesGreen: true,
            verifierScore: null, verifierPassed: false, circuitBroken: false, stageComplete: false);
        Assert.True(vars.HasCommits);
    }

    [Fact]
    public void A_missing_or_non_git_satellite_is_ignored_rather_than_failing_the_session()
    {
        var primary = NewRepo("primary");
        var notARepo = Path.Combine(_root, "plain-dir");
        Directory.CreateDirectory(notARepo);

        var plan = PlanOn(primary, Path.Combine(_root, "nope"), notARepo);
        var logged = new List<string>();
        var heads = SatelliteRepos.Heads(plan, logged.Add);

        Assert.Empty(heads);
        Assert.Equal(2, logged.Count); // it says so once per unusable satellite, and carries on
        Assert.Empty(SatelliteRepos.CommitsSince(plan, heads));
    }

    [Fact]
    public void Doctor_fails_loudly_on_a_satellite_path_that_is_not_a_repo()
    {
        var primary = NewRepo("primary");
        var good = DoctorCommand.CheckSatelliteRepos(PlanOn(primary, NewRepo("sibling")));
        Assert.Equal("ok", good.State);
        Assert.Contains("sibling", good.Message, StringComparison.Ordinal);

        var bad = DoctorCommand.CheckSatelliteRepos(PlanOn(primary, Path.Combine(_root, "typo-here")));
        Assert.Equal("fail", bad.State);
        Assert.Contains("will NOT count", bad.Message, StringComparison.Ordinal);

        var none = DoctorCommand.CheckSatelliteRepos(PlanOn(primary));
        Assert.Equal("ok", none.State);
    }

    // ---------------------------------------------------------------- (2) the gate cache key

    [Fact]
    public void The_cache_key_changes_when_the_gates_command_changes_at_the_same_head()
    {
        var repo = NewRepo("primary");
        var head = Git.Head(repo);
        var plan = PlanOn(repo);

        var before = GateRunner.CacheKey(plan, plan.Gates[0], head);
        var after = GateRunner.CacheKey(plan, new GateConfig { Name = "build", Command = "exit 1" }, head);

        Assert.NotEqual(before, after);
        Assert.Equal(before, GateRunner.CacheKey(plan, plan.Gates[0], head)); // stable
    }

    [Fact]
    public void The_cache_key_changes_when_the_gates_own_working_directory_repo_moves()
    {
        var primary = NewRepo("primary");
        var sat = NewRepo("sibling");
        var head = Git.Head(primary);
        // A gate that builds the sibling repo: cwd points outside the primary's history entirely.
        var plan = new PlanConfig { Repo = primary, Gates = [new GateConfig { Name = "sib-build", Command = "exit 0", Cwd = "../sibling" }] };

        var before = GateRunner.CacheKey(plan, plan.Gates[0], head);
        Commit(sat, "src.txt", "changed", "feat: sibling moved");
        var after = GateRunner.CacheKey(plan, plan.Gates[0], head);

        // The primary HEAD did not move — under the old key these were the same string, and the
        // gate was served a pass for a tree that had changed underneath it.
        Assert.Equal(head, Git.Head(primary));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void The_cache_key_changes_when_a_declared_watch_path_is_touched()
    {
        var repo = NewRepo("primary");
        var head = Git.Head(repo);
        var genDir = Path.Combine(repo, "generated");
        Directory.CreateDirectory(genDir);
        File.WriteAllText(Path.Combine(genDir, "a.txt"), "v1");

        var gate = new GateConfig { Name = "gen", Command = "exit 0", WatchPaths = ["generated"] };
        var plan = new PlanConfig { Repo = repo, Gates = [gate] };

        var before = GateRunner.CacheKey(plan, gate, head);
        File.SetLastWriteTimeUtc(Path.Combine(genDir, "a.txt"), DateTime.UtcNow.AddMinutes(5));
        var after = GateRunner.CacheKey(plan, gate, head);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void A_single_repo_plans_cache_key_still_tracks_the_primary_head()
    {
        var repo = NewRepo("primary");
        var plan = PlanOn(repo);
        var k1 = GateRunner.CacheKey(plan, plan.Gates[0], Git.Head(repo));
        Commit(repo, "x.txt", "x", "feat: move");
        var k2 = GateRunner.CacheKey(plan, plan.Gates[0], Git.Head(repo));
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void The_battery_signature_changes_when_a_gates_command_is_edited_mid_run()
    {
        var a = new PlanConfig { Repo = ".", Gates = [new GateConfig { Name = "build", Command = "dotnet build" }] };
        var b = new PlanConfig { Repo = ".", Gates = [new GateConfig { Name = "build", Command = "dotnet build --no-restore" }] };

        // Same HEAD, same gate NAME — the old signature was byte-identical and the phase gate
        // announced "tree unchanged since last green battery — reusing result".
        Assert.NotEqual(GateRunner.BatterySignature(a, "abc123", null), GateRunner.BatterySignature(b, "abc123", null));
    }

    // ---------------------------------------------------------------- (3) skipIfFresh vs a dirty tree

    [Fact]
    public void MostRecentChangeTime_sees_an_uncommitted_edit_the_commit_clock_cannot()
    {
        var repo = NewRepo("primary");
        var committed = Git.MostRecentCommitTime(repo);
        Assert.NotNull(committed);

        File.WriteAllText(Path.Combine(repo, "src.txt"), "uncommitted work");
        File.SetLastWriteTimeUtc(Path.Combine(repo, "src.txt"), DateTime.UtcNow.AddMinutes(10));

        var changed = Git.MostRecentChangeTime(repo);
        Assert.NotNull(changed);
        Assert.True(changed > committed, $"expected the uncommitted edit ({changed}) to beat the last commit ({committed})");
    }

    [Fact]
    public void MostRecentChangeTime_can_exclude_the_freshness_artifact_itself()
    {
        var repo = NewRepo("primary");
        var outDir = Path.Combine(repo, "bin");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "app.exe"), "built");
        File.SetLastWriteTimeUtc(Path.Combine(outDir, "app.exe"), DateTime.UtcNow.AddMinutes(10));

        // Untracked build output dates itself newer than everything; excluded, it stops doing so.
        var withArtifact = Git.MostRecentChangeTime(repo);
        var withoutArtifact = Git.MostRecentChangeTime(repo, "bin");
        Assert.True(withArtifact > withoutArtifact);
    }

    [Fact]
    public async Task SkipIfFresh_runs_the_gate_when_uncommitted_work_is_newer_than_the_artifact()
    {
        var repo = NewRepo("primary");
        var marker = Path.Combine(repo, "gate-ran.txt");
        var outDir = Path.Combine(repo, "out");
        Directory.CreateDirectory(outDir);

        // The artifact is newer than the last commit: the F7.5 check alone calls this fresh.
        await File.WriteAllTextAsync(Path.Combine(outDir, "built.txt"), "artifact");
        Directory.SetLastWriteTimeUtc(outDir, DateTime.UtcNow.AddMinutes(5));

        // …and the agent's work is sitting uncommitted, NEWER than the artifact. This is the normal
        // mid-session state, and it is precisely what the gate exists to check.
        await File.WriteAllTextAsync(Path.Combine(repo, "src.txt"), "the change the gate must see");
        File.SetLastWriteTimeUtc(Path.Combine(repo, "src.txt"), DateTime.UtcNow.AddMinutes(20));

        var plan = new PlanConfig
        {
            Repo = repo,
            Gates =
            [
                new GateConfig
                {
                    Name = "build",
                    Command = OperatingSystem.IsWindows()
                        ? "Set-Content -Path gate-ran.txt -Value ran"
                        : "echo ran > gate-ran.txt",
                    SkipIfFresh = "out",
                    TimeoutMinutes = 2,
                },
            ],
        };

        var results = await GateRunner.RunAllAsync(plan, ct: CancellationToken.None);

        Assert.Single(results);
        Assert.False(results[0].Cached, "the gate was served as fresh over an uncommitted change newer than its output");
        Assert.True(File.Exists(marker), "the gate did not actually execute");
    }

    [Fact]
    public async Task SkipIfFresh_still_caches_on_a_clean_tree_whose_artifact_is_newer_than_the_last_commit()
    {
        var repo = NewRepo("primary");
        var outDir = Path.Combine(repo, "out");
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "built.txt"), "artifact");
        Directory.SetLastWriteTimeUtc(outDir, DateTime.UtcNow.AddMinutes(5));
        // "out" is untracked, so it is the only dirty path — and it is the artifact, which the
        // change scan excludes. Nothing else has moved since the commit.

        var plan = new PlanConfig
        {
            Repo = repo,
            Gates = [new GateConfig { Name = "build", Command = "exit 1", SkipIfFresh = "out", TimeoutMinutes = 2 }],
        };

        var results = await GateRunner.RunAllAsync(plan, ct: CancellationToken.None);

        // The caching this feature exists for is intact: a failing command never ran.
        Assert.True(results[0].Cached, "the freshness cache stopped working on a genuinely unchanged tree");
    }
}
