namespace Conductor.Core.Store;

/// <summary>What the pass found. Produced read-only, so this is exactly what the dry run prints and
/// exactly what <see cref="StateRepair.Apply"/> acts on.</summary>
public sealed record RepairPlan(
    string Root,
    IReadOnlyList<StoreSurvey> Stores,
    int RunRows,
    int DistinctRuns,
    IReadOnlyList<DuplicateRun> Duplicates,
    IReadOnlyList<string> Deferred);

/// <summary>What the pass did.</summary>
public sealed record RepairOutcome(
    string BackupDir,
    IReadOnlyList<string> StoresChanged,
    int RowsDeleted,
    IReadOnlyList<string> Notes);
