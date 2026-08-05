namespace Conductor.Tests;

/// <summary>
/// <c>StateMigration.Announce</c> and <c>StateMigration.Warn</c> are process-wide sinks — the store
/// persists, it does not present (K2.2), so the shell installs a writer and everything else leaves
/// them null. Two test classes that both install a capturing sink therefore race: xUnit runs classes
/// in parallel, and one class's <c>finally { Warn = null; }</c> lands between the other's call and its
/// assertion. That is not hypothetical — it cost a green run to diagnose. Classes that touch either
/// sink join this collection and run one at a time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class StateSinkCollection
{
    public const string Name = "state-migration-sinks";
}
