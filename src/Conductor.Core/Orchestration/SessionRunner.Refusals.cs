namespace Conductor.Core.Orchestration;

#pragma warning disable MA0045 // sync file I/O by design — same boundary as SessionRunner.cs

/// <summary>
/// DV2.4 — how a session that came back WRONG is classified, and on what evidence.
/// <para>Split out of <c>SessionRunner.cs</c> because that file sits on the architecture ratchet's
/// 500-line ceiling, and because these two are one responsibility: reading what the backend actually
/// said when the agent itself said nothing. Bug #69 lived in exactly this gap.</para>
/// </summary>
public sealed partial class SessionRunner
{
    /// <summary>DV2.4, bug #69 — how long to wait out a usage limit, and on whose authority. The
    /// backend's own reset time when it gave one (a 5-hour window slept off 30 minutes at a time is
    /// ten more refusals; a 90-second one slept off for 30 minutes is 28 minutes of idle engine),
    /// the plan's flat <c>backoffMinutes</c> otherwise. A method rather than three lines inline
    /// because <see cref="RunAsync"/> sits ON the CA1502/CA1505 ratchet.</summary>
    private (TimeSpan Wait, string Source) BackoffWindow(string evidence, DateTime utcNow)
    {
        var stated = Providers.ProviderText.ResetWait(evidence, utcNow);
        return stated is null
            ? (TimeSpan.FromMinutes(_ctx.Plan.Limits.BackoffMinutes), "plan default")
            : (stated.Value, "reset time given by the backend");
    }

    /// <summary>The last lines the agent's process actually wrote — the only place a backend refusal
    /// appears when the CLI's own result envelope is empty.
    /// <para>DV2.4, bug #69: this used <c>File.ReadAllText</c>, which opens with
    /// <c>FileShare.Read</c>. Every caller runs while the session is still alive and
    /// <see cref="AgentSession"/> holds the same file open for WRITING, so Windows refused the read,
    /// the <c>IOException</c> was swallowed, and the tail came back EMPTY — always. The 429
    /// classifier was reading a blank string and filing rate limits as agent errors. Measured with a
    /// probe log line: <c>exit=1 said=True rt=[] tail=[] evid=[ ]</c> on a session whose raw log
    /// contained "Claude AI usage limit reached" on its last line.</para>
    /// <para><c>FileShare.ReadWrite</c> says what is true: another handle is writing this file and
    /// that is fine, a tail of a live log is allowed to be a snapshot.</para></summary>
    private string LastRawTail(string rawLogPath)
    {
        try
        {
            using var fs = new FileStream(rawLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return GateRunner.TailOf(reader.ReadToEnd(), 10);
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
}
