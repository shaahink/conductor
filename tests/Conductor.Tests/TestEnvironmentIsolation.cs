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
}
