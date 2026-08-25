using Conductor.Core.Store;

namespace Conductor.Core.Inbox;

/// <summary>DV3.4 — one project this machine knows about, as routing needs to see it.
///
/// <para>Not a new concept and deliberately not a new list: <see cref="StateCatalogue"/> already
/// keeps every (repo, plan) this machine has ever run, keyed and slugged (K3). Routing reads THAT
/// rather than growing a registry of its own, because a second list of projects is a second list to
/// keep true.</para></summary>
/// <param name="Plan">The plan name — what a push's identity line says, and what a person types.</param>
/// <param name="Repo">The checkout. Two clones of one plan are two projects here, which is why the
/// repo is carried rather than derived.</param>
/// <param name="Slug">The catalogue's own key-safe name, unique by construction.</param>
/// <param name="StateDir">Where this project's <c>.conductor</c> is — the inbox's parent.</param>
/// <param name="Present">Whether that checkout is still on this disk. False is the case findings
/// §6.10 names: an entry whose repo has moved or vanished, which must be refused by name and parked,
/// never dropped.</param>
public sealed record ProjectRef(string Plan, string Repo, string Slug, string StateDir, bool Present)
{
    /// <summary>What a person is shown and what they may type back. The plan name where there is
    /// one, because that is what the push says.</summary>
    public string Name => Plan.Length > 0 ? Plan : RepoLeaf;

    /// <summary>The checkout's folder name — the second thing that distinguishes two clones of one
    /// plan, and the only one a reader can see at a glance.</summary>
    public string RepoLeaf => Path.GetFileName(Repo.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>Where this project's inbox lives.</summary>
    public InboxStore Inbox() => new(StateDir);
}

/// <summary>The answer to "which project did they mean": one, or a sentence saying why not.</summary>
/// <param name="Project">The match, or null.</param>
/// <param name="Refusal">Why there is no match, in words that can be said to the sender. Names the
/// thing they typed and what this machine actually has — the <c>GithubConfig.Board</c> rule, reused
/// a third time (findings §1.5).</param>
public sealed record ProjectMatch(ProjectRef? Project, string? Refusal);

/// <summary>DV3.4 — every project this machine can file a note against, and the one lookup that
/// turns a typed word or a push's identity line into one of them.
///
/// <para>Matching is deliberately narrow: an exact slug, an exact plan name, or an exact repo folder
/// name, case-insensitively. No prefixes and no fuzzy match — a note is the owner's own words about
/// a project, and a router that guesses filed them somewhere they will never be read. Two matches
/// are refused with both named, for the same reason.</para></summary>
public sealed class ProjectDirectory
{
    private readonly string _root;
    private readonly ProjectRef? _local;

    /// <param name="stateHomeRoot">The machine's state home. Defaults to the resolved one.</param>
    /// <param name="local">The project whose engine is running this surface, if any. Always
    /// routable even when the catalogue has not seen it yet — a fresh run must not be unable to
    /// receive a note about itself.</param>
    public ProjectDirectory(string? stateHomeRoot = null, ProjectRef? local = null)
    {
        _root = string.IsNullOrWhiteSpace(stateHomeRoot) ? StateHome.Root : stateHomeRoot;
        _local = local;
    }

    /// <summary>The machine's state home, as this directory resolved it. Where the dead-letter box
    /// and the sticky selections live too.</summary>
    public string Root => _root;

    /// <summary>The project whose run is hosting this surface, or null.</summary>
    public ProjectRef? Local => _local;

    /// <summary>Every project, catalogue first, with the local run folded in if the catalogue has
    /// not caught up. Ordered by name so a list shown to a person is stable between two readings.</summary>
    public IReadOnlyList<ProjectRef> All()
    {
        var byKey = new Dictionary<string, ProjectRef>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Read())
        {
            var repo = entry.Repo ?? "";
            var stateDir = Path.Combine(repo, StateHome.ScratchDirName);
            byKey[entry.Slug] = new ProjectRef(entry.Plan ?? "", repo, entry.Slug, stateDir,
                Directory.Exists(repo));
        }

        if (_local is { } local) byKey[local.Slug] = local;

        return [.. byKey.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private IReadOnlyList<StateCatalogueEntry> Read()
    {
        try { return StateCatalogue.Read(_root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];   // an unreadable catalogue is a machine with one project: the local one
        }
    }

    /// <summary>The project a typed word or a push's plan name means. Never a guess: an unknown word
    /// and an ambiguous one both come back as a sentence naming what this machine has.</summary>
    public ProjectMatch Resolve(string? typed)
    {
        var wanted = typed?.Trim();
        if (wanted is not { Length: > 0 })
            return new ProjectMatch(null, "Name a project: " + Listed() + ".");

        var all = All();
        var hits = all.Where(p => Matches(p, wanted)).ToList();

        if (hits.Count == 1) return new ProjectMatch(hits[0], null);

        if (hits.Count == 0)
            return new ProjectMatch(null,
                $"There is no project called \"{wanted}\" on this machine. It has: {Listed()}.");

        // Two clones of one plan is the ordinary way to get here, so the disambiguator offered is
        // the one that actually differs: the slug.
        return new ProjectMatch(null,
            $"\"{wanted}\" matches {hits.Count} projects — "
            + string.Join(", ", hits.Select(h => h.Slug + " (" + h.RepoLeaf + ")"))
            + ". Name one of those slugs.");
    }

    private static bool Matches(ProjectRef p, string wanted) =>
        string.Equals(p.Slug, wanted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(p.Plan, wanted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(p.RepoLeaf, wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>What this machine has, for a refusal or a list. Clipped, because a machine with
    /// forty runs must not answer a typo with forty lines.</summary>
    public string Listed(int max = 8)
    {
        var all = All();
        if (all.Count == 0) return "no projects yet";
        var names = all.Take(max).Select(p => p.Name + (p.Present ? "" : " (repo missing)"));
        var rest = all.Count - Math.Min(max, all.Count);
        return string.Join(", ", names) + (rest > 0 ? $", and {rest} more" : "");
    }
}
