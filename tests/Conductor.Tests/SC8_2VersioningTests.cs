using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Conductor.Core;

namespace Conductor.Tests;

/// <summary>
/// SC8.2 — the version number is DERIVED, never typed. Three things have to stay true, and each of
/// them has failed somewhere in the wild:
/// <list type="number">
/// <item>no literal version in the project file — the one the csproj carried said 2.0.0 while the
/// newest tag said v0.1.0, and nothing noticed for the life of the repo;</item>
/// <item>the running assembly's version actually agrees with the git tag height, which is what
/// catches a checkout with no tags (it silently yields <c>0.0.0-alpha.0</c>);</item>
/// <item>every release tag has a CHANGELOG.md section, because <c>release.yml</c> publishes that
/// section as the release body and refuses the release without it.</item>
/// </list>
/// <para>The git-backed tests degrade to a no-op when git or the repository is unavailable (a source
/// archive, a packaging sandbox) rather than failing for the environment — the same call
/// <c>StampBuildInfo</c>'s <c>ContinueOnError</c> makes.</para>
/// </summary>
public sealed partial class SC8_2VersioningTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [GeneratedRegex(@"^v(?<base>\d+\.\d+\.\d+)-(?<height>\d+)-g[0-9a-f]+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DescribeLine();

    private static string? Git(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = RepoRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30_000);
            return p.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no git on PATH
        }
    }

    [Fact]
    public void TheProjectFileCarriesNoHandTypedVersion()
    {
        // Parsed as XML, not grepped: the csproj comment explains why there is no Version property,
        // and a text search would find the word in the explanation. Elements are what MSBuild reads.
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "Conductor", "Conductor.csproj"));
        // Top-level PropertyGroups only — a PropertyGroup inside a Target is computed at build time,
        // which is precisely the mechanism this checkpoint installs.
        var properties = doc.Root!.Elements("PropertyGroup").Elements().ToList();

        foreach (var typed in new[] { "Version", "VersionPrefix", "AssemblyVersion", "InformationalVersion" })
        {
            // InformationalVersion IS set — inside the StampBuildInfo target, from $(Version) plus the
            // commit. What must not come back is a literal in a PropertyGroup at the top of the file.
            Assert.DoesNotContain(properties, e => e.Name.LocalName == typed);
        }

        // ...and the thing that replaced it is actually referenced. Removing the constant without
        // wiring the deriver would leave every build claiming the SDK default 1.0.0.
        Assert.Contains(doc.Descendants("PackageReference"),
            e => e.Attribute("Include")?.Value == "MinVer");
        Assert.Contains(properties, e => e.Name.LocalName == "MinVerTagPrefix" && e.Value == "v");

        // A floor would be a second, hand-maintained source of truth able to disagree with the tags
        // in silence. The tags are the whole answer; see the csproj comment.
        Assert.DoesNotContain(properties, e => e.Name.LocalName == "MinVerMinimumMajorMinor");
    }

    [Fact]
    public void TheEngineVersionIsDerivedFromTheNewestReleaseTag()
    {
        // v0.1.0-54-g1c2330f : the newest v-tag reachable from HEAD, how far past it we are, and
        // the commit. --long forces the height even when HEAD is exactly on the tag.
        var described = Git("describe --tags --long --match v[0-9]*");
        if (described is null) return; // no git, no tags, or a source archive — nothing to compare against

        var m = DescribeLine().Match(described);
        Assert.True(m.Success, $"unexpected `git describe` output: {described}");

        var parts = m.Groups["base"].Value.Split('.');
        var tag = $"v{m.Groups["base"].Value}";

        // Deliberately NOT describe's height — see MinVerHeight. The two numbers answer different
        // questions and only one of them is in the assembly.
        var tagSha = Git($"rev-list -n 1 {tag}");
        if (tagSha is null) return;
        if (MinVerHeight(tagSha) is not int height) return;

        // A history with no merges since the tag has exactly one path back to it, so the two
        // definitions must coincide. That equality is what keeps the walk honest on a linear
        // checkout — a fresh clone, a CI runner — where a broken walk would otherwise go unnoticed.
        if (Git($"rev-list --count --merges {tag}..HEAD") == "0")
        {
            Assert.Equal(
                int.Parse(m.Groups["height"].Value, System.Globalization.CultureInfo.InvariantCulture),
                height);
        }

        // MinVer's rule: on the tag you ARE the tag; past it you are a prerelease of the next patch,
        // which orders above the tag and below that patch.
        var expectedBase = height == 0
            ? m.Groups["base"].Value
            : $"{parts[0]}.{parts[1]}.{int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) + 1}";

        var actual = BuildInfo.Current.Version;
        Assert.StartsWith(expectedBase, actual, StringComparison.Ordinal);

        if (height == 0)
        {
            Assert.Equal(expectedBase, actual);
        }
        else
        {
            Assert.Matches($@"^{Regex.Escape(expectedBase)}-alpha\.0\.\d+$", actual);
        }

        // The exact height is only comparable when THIS binary was built from THIS commit. Building,
        // then committing, then running the suite without a rebuild moves git on without moving the
        // assembly — a real situation, and not a versioning defect. The SC8.1 commit stamp is what
        // lets the assertion know which case it is in, so the strict check runs whenever it can. A
        // merge history used to be a second such case and is no longer: the height above is computed
        // MinVer's way, so there is nothing left for a merge to disagree about.
        var head = Git("rev-parse --short=12 HEAD");
        if (head is not null && string.Equals(head, BuildInfo.Current.CommitSha, StringComparison.OrdinalIgnoreCase))
        {
            var expected = height == 0 ? expectedBase : $"{expectedBase}-alpha.0.{height}";
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// The height MinVer stamps: the SHORTEST number of commits from HEAD back to the tagged commit,
    /// walking every parent. This is emphatically not what <c>git describe</c> prints — describe's
    /// height is rev-list's set difference, every commit unique to HEAD across ALL parents — and the
    /// two coincide only on a linear history. On <c>feat/karvan</c>, <c>v0.3.0..HEAD</c> is 20 by
    /// describe and 15 by MinVer: the tag is <c>e897c2c7</c>, the merge <c>c4febc1</c> has it as its
    /// FIRST parent, and the other five commits came in from the side branch. Both numbers are right
    /// for their own definition; only MinVer's is the one compiled into the assembly, so only
    /// MinVer's may be compared against it. Reading describe's number here is what made this test
    /// fail for a topology that was never a versioning defect.
    /// </summary>
    /// <returns>The height, or <c>null</c> if git cannot answer — the same degrade-to-no-op the rest
    /// of this class makes when there is no repository to ask.</returns>
    private static int? MinVerHeight(string tagSha)
    {
        var head = Git("rev-parse HEAD");
        if (head is null) return null;
        if (string.Equals(head, tagSha, StringComparison.Ordinal)) return 0;

        // Confirmed against this repo: a `A..B` range does NOT rewrite parents, so the excluded tagged
        // commit still appears by name in its children's parent lists, which is exactly the terminator
        // the walk needs. Bounded by the range rather than by all of history.
        var listed = Git($"rev-list --parents {tagSha}..HEAD");
        if (listed is null) return null;

        var parents = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var shas = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            parents[shas[0]] = shas[1..];
        }

        // Breadth-first over parent edges: the first arrival at the tagged commit is the shortest
        // path, and the number of edges walked to get there IS the height. A parent missing from the
        // map forked before the tag, and no walk backwards from there can reach a descendant — a dead
        // end, correctly, rather than a shorter route.
        var seen = new HashSet<string>(StringComparer.Ordinal) { head };
        var frontier = new List<string> { head };
        for (var distance = 1; frontier.Count > 0; distance++)
        {
            var next = new List<string>();
            foreach (var commit in frontier)
            {
                if (!parents.TryGetValue(commit, out var ancestors)) continue;
                foreach (var ancestor in ancestors)
                {
                    if (string.Equals(ancestor, tagSha, StringComparison.Ordinal)) return distance;
                    if (seen.Add(ancestor)) next.Add(ancestor);
                }
            }
            frontier = next;
        }

        return null; // the tag is not an ancestor of HEAD — describe would not have named it
    }

    [Fact]
    public void EveryReleaseTagHasItsOwnChangelogSection()
    {
        var changelog = Path.Combine(RepoRoot(), "CHANGELOG.md");
        Assert.True(File.Exists(changelog), "CHANGELOG.md is missing — release.yml publishes its sections as release notes");

        var text = File.ReadAllText(changelog);
        Assert.Contains("## [Unreleased]", text, StringComparison.Ordinal);

        var tags = Git("tag --list v[0-9]*");
        if (tags is null) return;

        foreach (var tag in tags.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var version = tag[1..];
            Assert.True(
                text.Contains($"## [{version}]", StringComparison.Ordinal),
                $"tag {tag} has no '## [{version}]' section in CHANGELOG.md — release.yml would refuse to publish it");
        }
    }
}
