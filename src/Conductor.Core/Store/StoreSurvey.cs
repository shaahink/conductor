namespace Conductor.Core.Store;

/// <summary>One run row as the repair reads it.</summary>
public sealed record StoreRun(string RunId, string PlanName, string Status, string StartedUtc);

/// <summary>One catalogued store, and whether an engine is using it.</summary>
/// <param name="Foreign">The catalogue names it but it does not live under the state home being
/// repaired. It may still own a run; it is never written.</param>
public sealed record StoreSurvey(
    string Db,
    string Slug,
    string Plan,
    DateTimeOffset FirstSeenUtc,
    bool Live,
    bool Foreign,
    IReadOnlyList<StoreRun> Runs);

/// <summary>A run that exists in more than one store, and where it is going to live.</summary>
/// <param name="OwnerDb">The store that keeps it.</param>
/// <param name="OwnerReason">Why that one — printed, because an operator about to delete history is
/// owed the reasoning rather than a verdict.</param>
/// <param name="RemoveFrom">The stores it is removed from.</param>
public sealed record DuplicateRun(
    string RunId,
    string PlanName,
    string OwnerDb,
    string OwnerReason,
    IReadOnlyList<string> RemoveFrom);
