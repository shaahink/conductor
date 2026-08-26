namespace Conductor.Core.Integrations.Cloud;

/// <summary>DV5.1 — what the installed <c>claude</c> CLI actually does about <c>--cloud</c>, measured
/// rather than read off the findings doc.
///
/// <para>NEXT-ERA-FINDINGS-2026-08-23 §2.3 CL-2 assumed conductor could fire a NEW cloud session
/// headlessly and read the session id and URL back. Measured against <b>claude 2.1.246</b> on
/// 2026-08-26, it cannot: the create direction is interactive-only and refuses three separate ways.
/// The refusals are quoted here verbatim because the chat reply hands them to the owner unaltered —
/// a paraphrase of a platform refusal is how an owner ends up debugging conductor instead of the
/// platform.</para>
///
/// <para>The FOLLOW-UP direction is headless and is what <c>/cloud</c> drives. See
/// <c>.conductor/evidence/DV5/dv5.1-cloud-flags.md</c> for the full help output these came from.</para></summary>
public static class CloudCliFacts
{
    /// <summary>The CLI these facts were measured against. A different version is a reason to
    /// re-measure, not a reason to assume.</summary>
    public const string MeasuredVersion = "2.1.246";

    /// <summary>When they were measured.</summary>
    public const string MeasuredOn = "2026-08-26";

    public const string Executable = "claude";

    /// <summary>Verbatim, from <c>claude --cloud "…" -p</c>.</summary>
    public const string RefusalWithPrint =
        "Error: --cloud cannot be combined with --print.\n"
        + "Starting a new cloud session with --cloud is interactive only: drop --print, or drop "
        + "--cloud to run locally. To message an existing cloud session instead, pass its ID: "
        + "`claude -p \"message\" --cloud <session-id>` (find IDs at claude.ai/code).";

    /// <summary>Verbatim, from <c>claude --cloud "…" --bg</c>.</summary>
    public const string RefusalWithBackground =
        "--bg and --cloud are different backends. Use `claude --cloud '<task>'` directly to start a "
        + "cloud session.";

    /// <summary>Verbatim, from <c>claude --cloud "…"</c> with stdout piped.</summary>
    public const string RefusalWithoutTty =
        "Error: --cloud requires an interactive terminal.\n"
        + "Non-interactive invocations (piped stdout, --init-only, --sdk-url) run locally and would "
        + "silently ignore --cloud. Drop --cloud, or run from a TTY.";

    /// <summary>What a cloud session costs, as far as this engine can ever know. §2.4 item 1: there
    /// is no per-turn telemetry for work on a machine conductor does not control, and
    /// <c>--max-budget-usd</c> is documented "only works with --print", which the create direction
    /// forbids. The word is the value — a zero here would be exactly the lie KS4 exists to catch.</summary>
    public const string UnknownCost = "unknown";

    /// <summary>Where a session id is found, since the CLI names it and this engine must not invent
    /// a URL shape it has never observed.</summary>
    public const string SessionHome = "claude.ai/code";

    /// <summary>Verbatim, from <c>claude -p "say ok" --cloud 00000000-0000-4000-8000-000000000000</c>
    /// — the live probe that corrected this engine's guess at the id shape. It also CONFIRMS the
    /// follow-up direction: the CLI says what <c>--cloud</c> does with <c>--print</c>, which is the
    /// one thing conductor drives.</summary>
    public const string RefusalNotASession =
        "Error: --cloud \"<id>\" is not a cloud session ID or URL.\n"
        + "With --print, --cloud sends the prompt to an existing cloud session: pass its ID "
        + "(session_... or cse_...) or its claude.ai/code URL. To start a new cloud session from a "
        + "description instead, drop --print.";

    /// <summary>The exact command an owner must run, on a terminal, to start the session conductor
    /// cannot start for them.</summary>
    public static string CreateCommand(string task) => $"claude --cloud \"{task.Replace("\"", "'", StringComparison.Ordinal)}\"";

    /// <summary>The argument vector for the one direction that IS headless, in the order the CLI's
    /// own refusal message spells it.</summary>
    public static string[] FollowUpArgs(string sessionId, string message) =>
        ["-p", message, "--cloud", sessionId];
}
