using Conductor.Http;
using System.Runtime.CompilerServices;

namespace Conductor.Tests;

/// <summary>
/// SC1.1: the suite must not read the developer's real credentials out of the ambient environment.
///
/// Several tests assert the "no bot token" branch — <c>DoctorCommandTests.CheckTelegram_Warn_
/// WhenConfiguredButNoToken</c>, <c>ControlPlaneServerTelegramTests</c>'s status/test cases — and
/// their correctness silently depended on <c>CONDUCTOR_TELEGRAM_TOKEN</c> being unset, which was
/// true in CI and on a clean machine. It stopped being true the moment the token was exported for
/// real use. Then those tests did not just fail: <c>POST /telegram/test</c> resolved the real token
/// and made a LIVE call to api.telegram.org against the developer's actual bot (the failure message
/// read "getMe succeeded (@conductor_app_bot)"), and would have gone on to send a message to the
/// fixture's made-up chat id.
///
/// A test run's result must not depend on who is running it, and no test may reach a real bot. The
/// variable is therefore cleared for the whole test process before any test executes. Tests that
/// need a token supply their own through <c>SecretsStore</c>, scoped to their own temp state dir.
/// A live-token round trip stays what it always was: a manual, credential-gated dogfood step.
/// </summary>
internal static class TestEnvironmentIsolation
{
    [ModuleInitializer]
    internal static void ClearAmbientCredentials()
    {
        // Process-scoped only: this never touches the user or machine environment.
        Environment.SetEnvironmentVariable("CONDUCTOR_TELEGRAM_TOKEN", null);
    }

    /// <summary>
    /// K3.1: the same argument, for state. <c>run.db</c> now resolves to a machine-level home, so a
    /// test that loads a plan and touches its store would write into the operator's REAL history —
    /// and, worse, could import a live <c>.conductor/run.db</c> into it. The suite gets its own home
    /// under the temp directory, and any ambient <c>CONDUCTOR_RUN_DB</c> is cleared so no outer
    /// environment can redirect a test at a real database.
    /// </summary>
    [ModuleInitializer]
    internal static void IsolateStateHome()
    {
        Environment.SetEnvironmentVariable(Core.Store.StateHome.RunDbEnvVar, null);
        var home = Path.Combine(Path.GetTempPath(), "conductor-tests", "state-home",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable(Core.Store.StateHome.HomeEnvVar, home);
    }

    /// <summary>The isolated state home this process is using. Tests that assert on catalogue or
    /// import behaviour read it from here rather than re-deriving it.</summary>
    internal static string StateHomeRoot => Core.Store.StateHome.Root;
}
