using Conductor.Core.Store;

namespace Conductor.Tests;

/// <summary>
/// K3.1: a live test that drove the engine used to reopen its database at
/// <c>&lt;repo&gt;/.conductor/run.db</c>, because that is where it was. The store now lives in a
/// machine-level home keyed by repo plus plan, so a test that wants "the database the engine just
/// wrote" has to ask the same question the engine asked.
///
/// <para>Asking through the catalogue is deliberate: it exercises the index on every live test in
/// the suite rather than only in <c>K3_1StateHomeTests</c>. The process-wide home is a temp
/// directory (see <see cref="TestEnvironmentIsolation"/>), so this never reaches the operator's real
/// history.</para>
/// </summary>
internal static class TestState
{
    /// <summary>The run database the engine resolved for this repo. Throws rather than returning a
    /// plausible-but-empty path — a test asserting against a database nobody wrote is worse than a
    /// test that fails loudly.</summary>
    internal static string RunDb(string repo)
    {
        var normalized = StateHome.NormalizeRepo(repo);
        var entry = StateCatalogue.Read(StateHome.Root)
            .FirstOrDefault(e => string.Equals(StateHome.NormalizeRepo(e.Repo), normalized, StringComparison.Ordinal));
        return entry?.RunDb
            ?? throw new InvalidOperationException(
                $"K3.1: no catalogued run store for '{repo}' under '{StateHome.Root}'. " +
                "Either the engine never resolved a plan for this repo, or the state home moved mid-test.");
    }
}
