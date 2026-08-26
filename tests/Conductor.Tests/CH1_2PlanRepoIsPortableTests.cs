using System.Text.Json;

using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// CH1.2 — a plan file this repo ships is loadable on a fresh clone, at any path, on any machine.
///
/// <para><b>What was wrong.</b> Every plan under <c>plans/</c> carried an ABSOLUTE machine path in
/// <c>repo</c> — <c>C:/code/conductor</c>, or <c>C:/code/conductor-baton</c> for the ones written
/// before the directory was renamed. <c>PlanConfig.Load</c> validates before it returns and
/// <c>Validate</c> refuses a <c>repo</c> that does not exist, so those files loaded on exactly one
/// machine in the world. Three <c>KS1_4DoctorPlanLintsTests</c> load this repo's own plan and lint
/// it; they re-pointed <c>plan.Repo</c> by hand AFTER the Load, which cannot help, because the Load
/// is what throws. They failed on every machine but the author's — CI included — for a whole era.</para>
///
/// <para><b>The route chosen, and why.</b> Of the two on offer — teach the tests to resolve the repo
/// root they run in, or teach plans a repo-relative form — only the second makes the plan FILE
/// portable rather than the test. A test-side fix leaves <c>conductor doctor -p plans/...</c>
/// refusing to load this repo's own worked examples on a fresh clone, which is the thing a reader
/// tries first. So: <b><c>plan.repo</c> may be written relative to the plan file's own directory</b>
/// and is resolved against it at load (<c>PlanConfig.ResolveRepoAgainstPlanFile</c>). Sixteen shipped
/// plans became <c>".."</c> or <c>"../.."</c>. An absolute value is still honoured untouched, which
/// is the right answer for the one kind of plan that is NOT portable by nature: a plan currently
/// driving a run names the checkout it drives.</para>
///
/// <para><b>The trap the route carries.</b> Resolution mutates the model, so the model and the file
/// now disagree about <c>repo</c> by construction — and <c>PlanDocumentEditor</c> saves by diffing a
/// re-serialised model against the file's own text. Unguarded, the first <c>plan set</c> of any key
/// would splice the absolutised path back in, and the portability would be gone with nothing in the
/// diff to name it. <see cref="Saving_a_plan_does_not_re_absolutise_the_repo_it_was_loaded_from"/>
/// is that guard.</para>
/// </summary>
public sealed class CH1_2PlanRepoIsPortableTests
{
    /// <summary>Plans that are DELIBERATELY still machine-absolute: a plan driving a live run names
    /// the checkout it drives, and it is read by the engine version installed when that run started —
    /// which, for this era, predates the relative form. Asserted exactly, so the set can only change
    /// by someone editing this line on purpose.</summary>
    private static readonly string[] StillAbsoluteByDesign = ["plans/charkh/core.plan.json"];

    [Fact]
    public void Every_shipped_plan_names_its_repo_relative_to_itself()
    {
        var root = RepoRoot();
        if (root is null) return; // not a full checkout — soft skip, as the other plan sweeps do

        var absolute = new List<string>();
        foreach (var (rel, repo) in ShippedPlans(root))
            if (repo.Length > 0 && (Path.IsPathRooted(repo) || (repo.Length >= 3 && repo[1] == ':')))
                absolute.Add(rel);

        Assert.Equal(StillAbsoluteByDesign, absolute);
    }

    /// <summary>The bar in one test: take the plan file and the tracker it names, put them in a
    /// directory that is not this checkout and has never heard of it, and load. That is what a fresh
    /// clone is.</summary>
    [Fact]
    public void This_repos_own_plan_loads_from_a_clone_that_is_not_this_checkout()
    {
        var root = RepoRoot();
        if (root is null) return;

        var clone = FreshClone(root, "plans/karvansara/core.plan.json");
        try
        {
            var plan = PlanConfig.Load(Path.Combine(clone, "plans", "karvansara", "core.plan.json"));

            Assert.Equal(Path.GetFullPath(clone), Path.GetFullPath(plan.Repo));
            Assert.Equal("../..", plan.RepoAsWritten);
            Assert.True(File.Exists(plan.TrackerPath), $"tracker did not resolve inside the clone: {plan.TrackerPath}");
        }
        finally { TestTemp.DeleteTree(clone); }
    }

    /// <summary>The negative control: the form this replaced cannot resolve to the clone it was read
    /// from. On a machine that happens to have <c>C:/code/conductor</c> the load SUCCEEDS and points
    /// somewhere else entirely — the worse half of the bug, and the half a "does it throw" assertion
    /// would miss.</summary>
    [Fact]
    public void The_old_absolute_form_cannot_resolve_to_the_clone_it_was_read_from()
    {
        var root = RepoRoot();
        if (root is null) return;

        var clone = FreshClone(root, "plans/karvansara/core.plan.json");
        try
        {
            var planPath = Path.Combine(clone, "plans", "karvansara", "core.plan.json");
            File.WriteAllText(planPath, File.ReadAllText(planPath)
                .Replace(RelativeForm, AbsoluteForm, StringComparison.Ordinal));

            string? loadedRepo = null;
            try { loadedRepo = PlanConfig.Load(planPath).Repo; }
            catch (InvalidOperationException) { /* the other machine's half: refused outright */ }

            if (loadedRepo is not null)
                Assert.NotEqual(Path.GetFullPath(clone), Path.GetFullPath(loadedRepo));
        }
        finally { TestTemp.DeleteTree(clone); }
    }

    /// <summary>Load absolutises; Save must not write that back. Every plan edit goes through this
    /// path — <c>plan set</c>, <c>add-stage</c>, the Face's editor — so one unguarded save undoes the
    /// portability for good.</summary>
    [Fact]
    public void Saving_a_plan_does_not_re_absolutise_the_repo_it_was_loaded_from()
    {
        var root = RepoRoot();
        if (root is null) return;

        var clone = FreshClone(root, "plans/karvansara/core.plan.json");
        try
        {
            var planPath = Path.Combine(clone, "plans", "karvansara", "core.plan.json");
            var plan = PlanConfig.Load(planPath);
            plan.Save();                                   // bumps planVersion — a real, minimal edit

            var text = File.ReadAllText(planPath);
            Assert.Contains(RelativeForm, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetFullPath(clone), text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(plan.PlanVersion, PlanConfig.Load(planPath).PlanVersion);   // the edit landed
        }
        finally { TestTemp.DeleteTree(clone); }
    }

    // ────────────────────────────────────────── fixtures ──────────────────────────────────────────

    private const string RelativeForm = "\"repo\": \"../..\"";
    private const string AbsoluteForm = "\"repo\": \"C:/code/conductor\"";

    /// <summary>Plan files carry <c>//</c> comments and trailing commas — KS3.2 exists to keep them —
    /// so read them the way <see cref="PlanConfig.JsonOpts"/> does.</summary>
    private static readonly JsonDocumentOptions PlanJson = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Every plan file under <c>plans/</c>, with the repo-relative path it sits at and the
    /// top-level <c>repo</c> it names. <c>github.repo</c> is a different key at a different depth and
    /// is untouched by this.</summary>
    private static IEnumerable<(string Rel, string Repo)> ShippedPlans(string root)
    {
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "plans"), "*.plan.json", SearchOption.AllDirectories)
                                   .OrderBy(f => f, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f), PlanJson);
            var repo = doc.RootElement.TryGetProperty("repo", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? "" : "";
            yield return (Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), repo);
        }
    }

    /// <summary>A directory holding the plan file and the tracker it names, at the same relative
    /// paths, and nothing else — a clone as far as the loader is concerned.</summary>
    private static string FreshClone(string root, string planRelPath)
    {
        var clone = Path.Combine(Path.GetTempPath(), "conductor-ch12-clone-" + Guid.NewGuid().ToString("N")[..8]);
        Copy(root, clone, planRelPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, planRelPath)), PlanJson);
        Copy(root, clone, doc.RootElement.GetProperty("tracker").GetString()!);
        return clone;
    }

    private static void Copy(string fromRoot, string toRoot, string relPath)
    {
        var dest = Path.Combine(toRoot, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(Path.Combine(fromRoot, relPath), dest, overwrite: true);
    }

    private static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "plans"))) return d.FullName;
        return null;
    }
}
