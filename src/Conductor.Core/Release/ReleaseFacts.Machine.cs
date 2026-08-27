namespace Conductor.Core.Release;

/// <summary>A live conductor image: the pid and the binary it is executing.</summary>
public sealed record LiveEngine(int Pid, string Path);

/// <summary><paramref name="Blockers"/> is <c>UpdateSafety.Blockers</c>'s answer verbatim — the same
/// detector <c>conductor update</c> refuses on, so the two verbs cannot disagree about whether a
/// binary swap is safe.</summary>
public sealed record ProcessFacts(
    IReadOnlyList<string> Blockers,
    IReadOnlyList<LiveEngine> Live,
    int? ConductorPid);

/// <summary>Trap 18's inputs. <paramref name="MigrationsSince"/> is the migration files that landed
/// between the commit the INSTALLED engine was built from and the tree's HEAD.</summary>
public sealed record MigrationFacts(
    int TreeVersion,
    string? InstalledSha,
    string? InstalledVersion,
    bool InstalledDirty,
    IReadOnlyList<string> MigrationsSince,
    long? StoreVersion,
    string? StorePath);

